using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Pos.Hub.Security;

namespace Pos.Hub.Tests;

/// <summary>The shop's own certificates.</summary>
public sealed class HubCertificatesTests : IDisposable
{
    private static readonly IPAddress ShopAddress = IPAddress.Parse("192.168.1.50");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pos-cert-tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero));
    private IReadOnlyList<IPAddress> _addresses = [ShopAddress];

    public HubCertificatesTests() => Directory.CreateDirectory(_folder);

    private HubSettings Settings => new()
    {
        BranchId = Guid.NewGuid(),
        BranchCode = "TEST1",
        DatabasePath = Path.Combine(_folder, "hub.db"),
    };

    private HubCertificates Create() => new(Settings, _clock, () => ["shop-pc", "shop-pc.local"], () => _addresses);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Temp files; leftovers are harmless.
        }
    }

    [Fact]
    public void Shop_ca_is_a_certificate_authority_and_the_hub_certificate_chains_to_it()
    {
        using var certs = Create();

        var basic = certs.Ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(basic.CertificateAuthority);
        Assert.Equal(certs.Ca.Subject, certs.Ca.Issuer);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certs.Ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = _clock.Now.UtcDateTime;
        Assert.True(chain.Build(certs.Server), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
    }

    [Fact]
    public void Hub_certificate_covers_this_pc_and_meets_browser_rules()
    {
        using var certs = Create();
        var san = certs.Server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        Assert.Contains("shop-pc", san.EnumerateDnsNames());
        Assert.Contains("shop-pc.local", san.EnumerateDnsNames());
        Assert.Contains("localhost", san.EnumerateDnsNames());
        Assert.Contains(ShopAddress, san.EnumerateIPAddresses());
        Assert.Contains(IPAddress.Loopback, san.EnumerateIPAddresses());
        Assert.True(certs.Server.HasPrivateKey);
        Assert.NotNull(certs.Server.GetECDsaPublicKey());
        Assert.True(certs.Server.NotAfter.ToUniversalTime() - certs.Server.NotBefore.ToUniversalTime() <= TimeSpan.FromDays(398));
        Assert.Contains(certs.Server.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void Certificates_are_kept_after_a_restart()
    {
        string caFingerprint, serverThumbprint;
        using (var first = Create())
        {
            caFingerprint = first.CaFingerprint;
            serverThumbprint = first.Server.Thumbprint;
        }

        using var second = Create();

        Assert.Equal(caFingerprint, second.CaFingerprint);
        Assert.Equal(serverThumbprint, second.Server.Thumbprint);
        Assert.False(second.EnsureCurrent());
    }

    [Fact]
    public void New_address_gets_a_new_hub_certificate_from_the_same_ca()
    {
        using var certs = Create();
        var ca = certs.CaFingerprint;
        var before = certs.Server.Thumbprint;

        _addresses = [IPAddress.Parse("192.168.1.77")];

        Assert.True(certs.EnsureCurrent());
        Assert.NotEqual(before, certs.Server.Thumbprint);
        Assert.Equal(ca, certs.CaFingerprint);
        Assert.Contains(IPAddress.Parse("192.168.1.77"), certs.Server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses());
    }

    [Fact]
    public void Hub_certificate_is_renewed_a_month_before_it_ends()
    {
        using var certs = Create();
        var firstEnd = certs.Server.NotAfter;

        _clock.Now += TimeSpan.FromDays(300);
        Assert.False(certs.EnsureCurrent());

        _clock.Now += TimeSpan.FromDays(70); // 27 days left
        Assert.True(certs.EnsureCurrent());
        Assert.True(certs.Server.NotAfter > firstEnd);
    }

    [Fact]
    public void Certificate_files_cannot_be_opened_without_the_hub_key()
    {
        using (Create())
        {
        }

        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_folder, "hub-ca.pfx"), "guess"));
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_folder, "hub-tls.pfx"), null));
    }

    [Fact]
    public void Check_code_is_four_groups_of_four()
    {
        using var certs = Create();

        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", certs.CaCheckCode);
        Assert.StartsWith(certs.CaCheckCode.Replace("-", string.Empty, StringComparison.Ordinal), certs.CaFingerprint, StringComparison.Ordinal);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", certs.CaPem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", certs.CaPem, StringComparison.Ordinal);
    }
}

/// <summary>The hub running on a real web server: HTTPS on the network port, HTTP only for this PC.</summary>
public sealed class HttpsConnectionTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pos-https-tests", Guid.NewGuid().ToString("N"));
    private readonly int _httpPort = FreePort();
    private readonly int _httpsPort = FreePort();
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Hub:DatabasePath", Path.Combine(_folder, "hub.db"));
            b.UseSetting("Hub:LocalHttpPort", _httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Hub:NetworkHttpsPort", _httpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        _factory.UseKestrel();
        _factory.StartServer();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Temp files; leftovers are harmless.
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A client that trusts only the shop CA, like a till with the CA installed.</summary>
    private static HttpClient TillTrusting(X509Certificate2 ca) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate);
        },
    });

    [Fact]
    public async Task Till_with_the_shop_ca_connects_over_https()
    {
        using var local = new HttpClient();
        var pem = await local.GetStringAsync(new Uri($"http://localhost:{_httpPort}/ca.crt"));
        using var ca = X509Certificate2.CreateFromPem(pem);
        var health = await local.GetStringAsync(new Uri($"http://localhost:{_httpPort}/api/health"));

        using var till = TillTrusting(ca);
        var response = await till.GetAsync(new Uri($"https://127.0.0.1:{_httpsPort}/api/health"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checkCode = string.Join('-', Enumerable.Range(0, 4).Select(i => HubCertificates.Fingerprint(ca).Substring(i * 4, 4)));
        Assert.Contains(checkCode, health, StringComparison.Ordinal); // the code shown on the hub matches the downloaded CA
    }

    [Fact]
    public async Task Device_without_the_shop_ca_refuses_the_connection()
    {
        using var stranger = new HttpClient();

        await Assert.ThrowsAsync<HttpRequestException>(() => stranger.GetAsync(new Uri($"https://127.0.0.1:{_httpsPort}/api/health")));
    }

    [Fact]
    public async Task Plain_http_is_not_offered_on_the_network_port()
    {
        using var client = new HttpClient();

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(new Uri($"http://127.0.0.1:{_httpsPort}/api/health")));
    }

    [Fact]
    public async Task Plain_http_listens_on_this_pc_only()
    {
        using var client = new HttpClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri($"http://localhost:{_httpPort}/api/health"))).StatusCode);
        var nonLoopback = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (nonLoopback is not null)
        {
            using var socket = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => socket.ConnectAsync(nonLoopback, _httpPort));
        }
    }
}
