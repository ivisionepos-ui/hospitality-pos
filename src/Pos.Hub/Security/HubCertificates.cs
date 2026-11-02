using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Pos.Hub.Security;

/// <summary>
/// The shop's own small certificate authority (CA) and the hub's HTTPS certificate.
/// The CA is created once per shop and installed on each till, so tills trust the hub without warnings.
/// The hub's certificate is issued by that CA for this PC's names and addresses, and is renewed automatically
/// (before it expires, or when the PC's address changes) without the tills needing anything new.
/// Both are saved as password-protected files next to the database; the password comes from the hub key.
/// </summary>
public sealed class HubCertificates : IDisposable
{
    /// <summary>How long the shop CA lasts.</summary>
    public static readonly TimeSpan CaLife = TimeSpan.FromDays(3650);

    /// <summary>How long a hub certificate lasts (kept under the 398-day limit browsers enforce).</summary>
    public static readonly TimeSpan ServerLife = TimeSpan.FromDays(397);

    /// <summary>Renew the hub certificate when it has less than this left.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private readonly string _caPath;
    private readonly string _serverPath;
    private readonly string _password;
    private readonly string _branchCode;
    private readonly TimeProvider _clock;
    private readonly Func<IReadOnlyList<string>> _names;
    private readonly Func<IReadOnlyList<IPAddress>> _addresses;
    private readonly Lock _lock = new();
    private X509Certificate2 _server;

    /// <summary>Loads or creates the CA and the hub certificate.</summary>
    public HubCertificates(HubSettings settings, TimeProvider clock)
        : this(settings, clock, CurrentNames, CurrentAddresses)
    {
    }

    /// <summary>Loads or creates the certificates, with the PC's names and addresses supplied (for tests).</summary>
    public HubCertificates(HubSettings settings, TimeProvider clock, Func<IReadOnlyList<string>> names, Func<IReadOnlyList<IPAddress>> addresses)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _names = names;
        _addresses = addresses;
        _branchCode = settings.BranchCode;
        var folder = Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath)) ?? ".";
        _caPath = Path.Combine(folder, "hub-ca.pfx");
        _serverPath = Path.Combine(folder, "hub-tls.pfx");
        var key = AccessService.LoadOrCreateKey(settings.KeyFilePath);
        _password = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("pos-hub-certificate-files")));

        Ca = File.Exists(_caPath) ? Load(_caPath) : CreateCa();
        _server = File.Exists(_serverPath) ? Load(_serverPath) : IssueServer();
        EnsureCurrent();
    }

    /// <summary>The shop CA (with its private key). Tills install its public part.</summary>
    public X509Certificate2 Ca { get; }

    /// <summary>The hub's current HTTPS certificate.</summary>
    public X509Certificate2 Server
    {
        get
        {
            lock (_lock)
            {
                return _server;
            }
        }
    }

    /// <summary>SHA-256 fingerprint of the CA, for checking on a till that the right CA was installed.</summary>
    public string CaFingerprint => Fingerprint(Ca);

    /// <summary>Short form of the fingerprint to read out: first 16 characters in groups of 4.</summary>
    public string CaCheckCode => string.Join('-', Enumerable.Range(0, 4).Select(i => CaFingerprint.Substring(i * 4, 4)));

    /// <summary>The CA's public certificate in PEM format, for downloading onto tills.</summary>
    public string CaPem => Ca.ExportCertificatePem();

    /// <summary>
    /// Renews the hub certificate if it is near its end, or if this PC's names or addresses are not all on it.
    /// Returns true when a new one was issued.
    /// </summary>
    public bool EnsureCurrent()
    {
        lock (_lock)
        {
            var nearEnd = _server.NotAfter.ToUniversalTime() - _clock.GetUtcNow().UtcDateTime < RenewBefore;
            var covers = CoversCurrentNamesAndAddresses(_server);
            var issuedByOurCa = _server.Issuer == Ca.Subject;
            if (!nearEnd && covers && issuedByOurCa)
            {
                return false;
            }

            var old = _server;
            _server = IssueServer();
            old.Dispose();
            return true;
        }
    }

    /// <summary>SHA-256 fingerprint of a certificate, as upper-case hex.</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Ca.Dispose();
        _server.Dispose();
    }

    private X509Certificate2 CreateCa()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=POS Hub CA {_branchCode}, O=Hospitality POS", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var now = _clock.GetUtcNow();
        using var created = request.CreateSelfSigned(now.AddDays(-1), now + CaLife);
        Save(created, _caPath);
        return Load(_caPath);
    }

    private X509Certificate2 IssueServer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=POS Hub {_branchCode}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server authentication
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in _names().Append("localhost").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            san.AddDnsName(name);
        }

        foreach (var address in _addresses().Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback).Distinct())
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());

        var now = _clock.GetUtcNow();
        var notAfter = now + ServerLife;
        if (notAfter > Ca.NotAfter.ToUniversalTime())
        {
            notAfter = Ca.NotAfter.ToUniversalTime().AddDays(-1);
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // keep the serial number positive
        using var issued = request.Create(Ca, now.AddDays(-1), notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        Save(withKey, _serverPath);
        return Load(_serverPath);
    }

    private bool CoversCurrentNamesAndAddresses(X509Certificate2 certificate)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
        {
            return false;
        }

        var dns = san.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ips = san.EnumerateIPAddresses().ToHashSet();
        return _names().All(dns.Contains) && _addresses().All(ips.Contains);
    }

    private void Save(X509Certificate2 certificate, string path)
    {
        var temp = path + ".new";
        File.WriteAllBytes(temp, certificate.Export(X509ContentType.Pfx, _password));
        File.Move(temp, path, overwrite: true);
    }

    private X509Certificate2 Load(string path) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, _password, X509KeyStorageFlags.Exportable);

    private static IReadOnlyList<string> CurrentNames()
    {
        var host = Dns.GetHostName();
        return [host, host + ".local"];
    }

    private static IReadOnlyList<IPAddress> CurrentAddresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)];

    /// <summary>Text for logs: when the hub certificate ends and what it covers.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"CA {CaCheckCode}; hub certificate valid to {Server.NotAfter:yyyy-MM-dd}");
}

/// <summary>Checks the hub certificate every hour and renews it when needed (near expiry, or the PC's address changed).</summary>
public sealed class CertificateRenewal(HubCertificates certificates, ILogger<CertificateRenewal> log) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (certificates.EnsureCurrent())
                {
                    HubLog.CertificateRenewed(log, certificates);
                }
            }
            catch (CryptographicException ex)
            {
                HubLog.CertificateRenewalFailed(log, ex);
            }
            catch (IOException ex)
            {
                HubLog.CertificateRenewalFailed(log, ex);
            }
        }
    }
}
