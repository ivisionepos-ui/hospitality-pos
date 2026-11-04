using System.Net;
using System.Text;
using System.Text.Json;
using Pos.Hub.Api;
using Pos.Hub.Delivery;

namespace Pos.Hub.Tests;

/// <summary>
/// Stand-ins for Google Places (New) and Royal Mail AddressNow (Loqate), answering in their documented formats,
/// so the hub's real code for both services is tested without the internet.
/// </summary>
internal sealed class FakeAddressServices : IDisposable
{
    public const string GoodGoogleKey = "google-good-key-1234";
    public const string GoodRoyalMailKey = "AA11-BB22-CC33-DD44";
    private const double Lat = 53.8 + (1 / 69.05); // 1 mile north of the test shop

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeAddressServices()
    {
        for (var attempt = 0; ; attempt++)
        {
            Port = Random.Shared.Next(20000, 60000);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
            }
        }

        _ = Task.Run(Serve);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}";

    public List<string> Calls { get; } = [];

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            var (status, body) = Answer(context.Request);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private (int Status, object Body) Answer(HttpListenerRequest request)
    {
        var path = request.Url!.AbsolutePath;
        var q = System.Web.HttpUtility.ParseQueryString(request.Url.Query);
        lock (Calls)
        {
            Calls.Add($"{request.HttpMethod} {path}");
        }

        if (path.StartsWith("/v1/places", StringComparison.Ordinal))
        {
            if (request.Headers["X-Goog-Api-Key"] != GoodGoogleKey)
            {
                return (403, new { error = new { code = 403, message = "API key not valid. Please pass a valid API key." } });
            }

            if (path == "/v1/places:autocomplete")
            {
                return (200, new
                {
                    suggestions = new object[]
                    {
                        new { placePrediction = new { placeId = "place-flat10", text = new { text = "Flat 10, Example Tower, Test Road, Leeds LS2 9JT, UK" } } },
                        new { placePrediction = new { placeId = "place-cafe", text = new { text = "Sample Cafe, 25 Test Road, Leeds LS2 9JT, UK" } } },
                    },
                });
            }

            object Component(string text, params string[] types) => new { longText = text, shortText = text, types };
            return path.EndsWith("place-cafe", StringComparison.Ordinal)
                ? (200, new
                {
                    types = new[] { "cafe", "food", "establishment" },
                    displayName = new { text = "Sample Cafe" },
                    location = new { latitude = Lat, longitude = -1.55 },
                    addressComponents = new[] { Component("25", "street_number"), Component("Test Road", "route"), Component("Leeds", "postal_town"), Component("LS2 9JT", "postal_code") },
                })
                : (200, new
                {
                    types = new[] { "subpremise" },
                    displayName = new { text = "Flat 10" },
                    location = new { latitude = Lat, longitude = -1.55 },
                    addressComponents = new[] { Component("Flat 10", "subpremise"), Component("Example Tower", "premise"), Component("Test Road", "route"), Component("Leeds", "postal_town"), Component("LS2 9JT", "postal_code") },
                });
        }

        if (q["Key"] != GoodRoyalMailKey)
        {
            return (200, new { Items = new[] { new { Error = "2", Description = "Unknown key", Cause = "The key you are using to access the service was not found.", Resolution = "Please check that the key is correct." } } });
        }

        if (path.EndsWith("/Find/v1.10/json3.ws", StringComparison.Ordinal))
        {
            return string.IsNullOrEmpty(q["Container"])
                ? (200, new { Items = new[] { new { Id = "GB|RM|ENG|9JT-LS2", Type = "Postcode", Text = "LS2 9JT", Highlight = "0-7", Description = "Test Road, Leeds - 2 Addresses" } } })
                : (200, new
                {
                    Items = new[]
                    {
                        new { Id = "GB|RM|A|1001", Type = "Address", Text = "Flat 10, Example Tower, Test Road", Highlight = string.Empty, Description = "Leeds, LS2 9JT" },
                        new { Id = "GB|RM|A|1002", Type = "Address", Text = "2 Test Road", Highlight = string.Empty, Description = "Leeds, LS2 9JT" },
                    },
                });
        }

        return (200, new
        {
            Items = new[]
            {
                new
                {
                    Id = q["Id"], Company = string.Empty, SubBuilding = "Flat 10", BuildingName = "Example Tower", BuildingNumber = string.Empty, SecondaryStreet = string.Empty,
                    Street = "Test Road", District = string.Empty, City = "Leeds", PostalCode = "LS2 9JT", Field1 = Lat.ToString(System.Globalization.CultureInfo.InvariantCulture), Field2 = "-1.55",
                },
            },
        });
    }
}

/// <summary>The three address services the owner can choose: Google, Royal Mail online, and the local files.</summary>
public sealed class AddressLookupTests : HubTestBase, IDisposable
{
    private readonly FakeAddressServices _services = new();

    internal override IReadOnlyDictionary<string, string> ExtraSettings => new Dictionary<string, string>
    {
        ["Delivery:GoogleEndpoint"] = _services.Url,
        ["Delivery:RoyalMailEndpoint"] = _services.Url,
    };

    public void Dispose() => _services.Dispose();

    private async Task UploadSample()
    {
        using var body = new StringContent(AddressFileTests.Sample, Encoding.Latin1, "text/csv");
        await Owner.PostAsync(new Uri("/api/addresses/files?name=LS2.csv", UriKind.Relative), body);
    }

    private Task<HttpResponseMessage> Choose(AddressProvider provider, string? google = null, string? royalMail = null, bool fallback = true) =>
        Owner.Put("/api/settings/address-lookup", new AddressLookupUpdate(provider, google, royalMail, fallback));

    private async Task<AddressSearchResult> Search(string q, string? group = null) =>
        await Cashier.GetJson<AddressSearchResult>($"/api/addresses/search?q={Uri.EscapeDataString(q)}&session=s1{(group is null ? string.Empty : $"&group={Uri.EscapeDataString(group)}")}");

    [Fact]
    public async Task Local_files_are_the_default_and_take_a_postcode_or_part_of_an_address()
    {
        await UploadSample();

        Assert.Equal(AddressProvider.LocalFile, (await Owner.GetJson<AddressLookupView>("/api/settings/address-lookup")).Provider);
        var flat = Assert.Single((await Search("ls29jt flat 10")).Suggestions);
        Assert.Equal("Flat 10, Example Tower, Test Road", flat.Label);
        Assert.Equal("Test Road", flat.Address!.Line1);
        var street = await Search("10 test");
        Assert.Contains(street.Suggestions, s => s.Label == "10 Test Road, LS2 9JT");
        Assert.Contains(street.Suggestions, s => s.Label == "Flat 10, Example Tower, Test Road, LS2 9JT");
        Assert.Empty(_services.Calls); // nothing went online
    }

    [Fact]
    public async Task Owner_chooses_the_service_keys_are_kept_on_the_hub_and_never_shown_again()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Choose(AddressProvider.Google)).StatusCode); // no key yet
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put("/api/settings/address-lookup", new AddressLookupUpdate(AddressProvider.LocalFile, null, null))).StatusCode);

        var view = await (await Choose(AddressProvider.Google, google: FakeAddressServices.GoodGoogleKey)).Read<AddressLookupView>();
        Assert.Equal(AddressProvider.Google, view.Provider);
        Assert.True(view.GoogleKeySet);
        Assert.Equal("1234", view.GoogleKeyEnding);
        Assert.DoesNotContain(FakeAddressServices.GoodGoogleKey, await Owner.GetStringAsync(new Uri("/api/settings/address-lookup", UriKind.Relative)), StringComparison.Ordinal);

        // Switching back keeps the key; an empty key removes it.
        await Choose(AddressProvider.LocalFile);
        Assert.True((await Owner.GetJson<AddressLookupView>("/api/settings/address-lookup")).GoogleKeySet);
        await Choose(AddressProvider.LocalFile, google: string.Empty);
        Assert.False((await Owner.GetJson<AddressLookupView>("/api/settings/address-lookup")).GoogleKeySet);
    }

    [Fact]
    public async Task Google_finds_by_any_text_and_gives_the_full_address_with_its_distance()
    {
        await Choose(AddressProvider.Google, google: FakeAddressServices.GoodGoogleKey);

        var result = await Search("10 example tower");
        Assert.Equal(AddressProvider.Google, result.Provider);
        Assert.Equal("Results from Google", result.Attribution);
        Assert.Equal(2, result.Suggestions.Count);

        var flat = await Cashier.GetJson<ResolvedAddress>("/api/addresses/resolve?id=place-flat10&session=s1");
        Assert.Equal("Test Road", flat.Address.Line1);
        Assert.Equal("Flat 10, Example Tower", flat.Address.Line2);
        Assert.Equal("Leeds", flat.Address.Town);
        Assert.Equal("LS2 9JT", flat.Address.Postcode);
        Assert.Equal(1.3m, flat.DistanceMiles);

        var cafe = await Cashier.GetJson<ResolvedAddress>("/api/addresses/resolve?id=place-cafe&session=s1");
        Assert.Equal("25 Test Road", cafe.Address.Line1);
        Assert.Equal("Sample Cafe", cafe.Address.Line2); // the business name is kept for the driver
    }

    [Fact]
    public async Task Royal_mail_online_opens_a_postcode_into_its_addresses()
    {
        await Choose(AddressProvider.RoyalMail, royalMail: FakeAddressServices.GoodRoyalMailKey);

        var postcode = Assert.Single((await Search("LS2 9JT")).Suggestions);
        Assert.True(postcode.IsGroup);
        Assert.Equal("LS2 9JT, Test Road, Leeds - 2 Addresses", postcode.Label);

        var houses = await Search("LS2 9JT", postcode.Id);
        Assert.Equal(["Flat 10, Example Tower, Test Road, Leeds, LS2 9JT", "2 Test Road, Leeds, LS2 9JT"], houses.Suggestions.Select(s => s.Label));

        var flat = await Cashier.GetJson<ResolvedAddress>($"/api/addresses/resolve?id={Uri.EscapeDataString(houses.Suggestions[0].Id!)}");
        Assert.Equal("Test Road", flat.Address.Line1);
        Assert.Equal("Flat 10, Example Tower", flat.Address.Line2);
        Assert.Equal(1.3m, flat.DistanceMiles);
    }

    [Fact]
    public async Task A_wrong_key_is_explained_by_test_and_the_till_falls_back_to_the_local_files()
    {
        await UploadSample();
        await Choose(AddressProvider.RoyalMail, royalMail: "wrong-key");

        var test = await Owner.PostAsync(new Uri("/api/settings/address-lookup/test?provider=RoyalMail&q=LS2 9JT", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.BadGateway, test.StatusCode);
        Assert.Contains("Unknown key", (await test.Read<ErrorView>()).Message, StringComparison.Ordinal);

        var fallback = await Search("LS2 9JT flat 2");
        Assert.Equal(AddressProvider.LocalFile, fallback.Provider);
        Assert.Contains("Showing the shop's own address file", fallback.Problem, StringComparison.Ordinal);
        Assert.Equal("Flat 2, Example Tower, Test Road", Assert.Single(fallback.Suggestions).Label);

        // With the fallback switched off, the till sees the reason instead.
        await Choose(AddressProvider.RoyalMail, fallback: false);
        var refused = await Cashier.GetAsync(new Uri("/api/addresses/search?q=LS2 9JT", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Equal("AddressServiceFailed", (await refused.Read<ErrorView>()).Code);
    }

    [Fact]
    public async Task Google_wrong_key_and_no_internet_are_explained()
    {
        await Choose(AddressProvider.Google, google: "not-the-key", fallback: false);
        var wrong = await Cashier.GetAsync(new Uri("/api/addresses/search?q=test", UriKind.Relative));
        Assert.Contains("refused the key", (await wrong.Read<ErrorView>()).Message, StringComparison.Ordinal);

        _services.Dispose(); // "no internet"
        var offline = await Cashier.GetAsync(new Uri("/api/addresses/search?q=test", UriKind.Relative));
        Assert.Contains("cannot be reached", (await offline.Read<ErrorView>()).Message, StringComparison.Ordinal);
    }
}
