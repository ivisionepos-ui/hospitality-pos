using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Domain.Orders;
using Pos.Hub.Data;

namespace Pos.Hub.Delivery;

/// <summary>Where the till's "Find address" looks.</summary>
public enum AddressProvider
{
    /// <summary>Google Places (address search by any text; paid after Google's free monthly amount).</summary>
    Google,

    /// <summary>Royal Mail online (AddressNow, run by Loqate): official PAF data by postcode or text; paid per address.</summary>
    RoyalMail,

    /// <summary>The shop's own Royal Mail address files on the hub: no internet, no cost per search.</summary>
    LocalFile,
}

/// <summary>One line in the "Find address" list.</summary>
/// <param name="Id">What to send back to get the full address (online services), or null when <paramref name="Address"/> is already known.</param>
/// <param name="Label">What staff see.</param>
/// <param name="Address">The address, when already known (local files).</param>
/// <param name="IsGroup">A postcode or street holding several addresses: tapping it lists them.</param>
public sealed record AddressSuggestion(string? Id, string Label, DeliveryAddress? Address, bool IsGroup = false);

/// <summary>Result of a search.</summary>
/// <param name="Provider">Who answered.</param>
/// <param name="Suggestions">Addresses or groups.</param>
/// <param name="Problem">If the chosen service failed and the local files answered instead, why.</param>
/// <param name="Attribution">Text the provider requires next to its results.</param>
public sealed record AddressSearchResult(AddressProvider Provider, IReadOnlyList<AddressSuggestion> Suggestions, string? Problem = null, string? Attribution = null);

/// <summary>A chosen address, with its distance from the shop when the service gave a location.</summary>
/// <param name="Address">The address.</param>
/// <param name="DistanceMiles">Distance, or null (then the postcode check works it out).</param>
public sealed record ResolvedAddress(DeliveryAddress Address, decimal? DistanceMiles);

/// <summary>Address lookup settings as shown to the owner (keys are never sent back, only whether one is set).</summary>
/// <param name="Provider">The chosen service.</param>
/// <param name="GoogleKeySet">A Google key is saved.</param>
/// <param name="GoogleKeyEnding">Last 4 characters of the saved Google key.</param>
/// <param name="RoyalMailKeySet">A Royal Mail (AddressNow/Loqate) key is saved.</param>
/// <param name="RoyalMailKeyEnding">Last 4 characters of the saved Royal Mail key.</param>
/// <param name="FallbackToLocalFile">Use the local files when the online service cannot be reached.</param>
/// <param name="LocalAddresses">Addresses in the local files.</param>
public sealed record AddressLookupView(AddressProvider Provider, bool GoogleKeySet, string? GoogleKeyEnding, bool RoyalMailKeySet, string? RoyalMailKeyEnding, bool FallbackToLocalFile, int LocalAddresses);

/// <summary>Change the address lookup settings. A key left empty (null) is kept; an empty string removes it.</summary>
/// <param name="Provider">The service to use.</param>
/// <param name="GoogleApiKey">Google Maps Platform API key (Places API (New) enabled).</param>
/// <param name="RoyalMailApiKey">Royal Mail AddressNow / Loqate key.</param>
/// <param name="FallbackToLocalFile">Use the local files when the online service fails.</param>
public sealed record AddressLookupUpdate(AddressProvider Provider, string? GoogleApiKey, string? RoyalMailApiKey, bool FallbackToLocalFile = true);

/// <summary>An online address service failed (no internet, wrong key, out of credit).</summary>
public sealed class AddressServiceException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AddressServiceException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public AddressServiceException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public AddressServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The address lookup settings, kept in the hub database so the owner can change them on a till.</summary>
public sealed class AddressLookupSettings(HubDatabase db, DeliverySettings defaults, TimeProvider clock)
{
    private const string ProviderKey = "address.provider";
    private const string GoogleKey = "address.google.key";
    private const string RoyalMailKey = "address.royalmail.key";
    private const string FallbackKey = "address.fallback";

    /// <summary>Base address of Google Places (New); a setting only so tests can point it at a stand-in.</summary>
    public string GoogleEndpoint => defaults.GoogleEndpoint;

    /// <summary>Base address of the Royal Mail AddressNow / Loqate service.</summary>
    public string RoyalMailEndpoint => defaults.RoyalMailEndpoint;

    /// <summary>The chosen service (default: the local files).</summary>
    public AddressProvider Provider => Enum.TryParse<AddressProvider>(Get(ProviderKey), out var p) ? p : AddressProvider.LocalFile;

    /// <summary>Saved Google key, or null.</summary>
    public string? GoogleApiKey => Get(GoogleKey);

    /// <summary>Saved Royal Mail key, or null.</summary>
    public string? RoyalMailApiKey => Get(RoyalMailKey);

    /// <summary>Fall back to the local files when the online service fails (default: yes).</summary>
    public bool FallbackToLocalFile => Get(FallbackKey) != "false";

    /// <summary>Saves new settings. Choosing an online service without its key is refused.</summary>
    public void Save(AddressLookupUpdate update, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(update);
        db.RunInTransaction(() =>
        {
            if (update.GoogleApiKey is { } g)
            {
                Set(GoogleKey, g.Trim(), byUserId);
            }

            if (update.RoyalMailApiKey is { } r)
            {
                Set(RoyalMailKey, r.Trim(), byUserId);
            }

            if (update.Provider == AddressProvider.Google && string.IsNullOrEmpty(GoogleApiKey))
            {
                throw new ArgumentException("Enter the Google API key to use Google.");
            }

            if (update.Provider == AddressProvider.RoyalMail && string.IsNullOrEmpty(RoyalMailApiKey))
            {
                throw new ArgumentException("Enter the Royal Mail (AddressNow) key to use Royal Mail online.");
            }

            Set(ProviderKey, update.Provider.ToString(), byUserId);
            Set(FallbackKey, update.FallbackToLocalFile ? "true" : "false", byUserId);
            return 0;
        });
    }

    private string? Get(string key) => db.Run(() => db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", key)) as string) is { Length: > 0 } v ? v : null;

    private void Set(string key, string value, Guid byUserId) => db.Execute(
        "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
        ("$k", key), ("$v", value), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString()));
}

/// <summary>
/// "Find address" on the till: asks the service the owner chose (Google, Royal Mail online or the local files),
/// and falls back to the local files if an online service cannot answer.
/// </summary>
public sealed class AddressLookupService(AddressLookupSettings settings, AddressBook book, DeliveryService delivery, HttpClient http)
{
    private static readonly string[] UkOnly = ["gb"];

    /// <summary>The settings, for the owner's screen.</summary>
    public AddressLookupView View()
    {
        static string? Ending(string? key) => key is { Length: >= 4 } k ? k[^4..] : null;
        return new AddressLookupView(settings.Provider, settings.GoogleApiKey is not null, Ending(settings.GoogleApiKey),
            settings.RoyalMailApiKey is not null, Ending(settings.RoyalMailApiKey), settings.FallbackToLocalFile, book.Count());
    }

    /// <summary>Finds addresses for what was typed (a postcode or part of an address). <paramref name="group"/> opens a postcode or street.</summary>
    public async Task<AddressSearchResult> SearchAsync(string text, string? group, string? session, CancellationToken cancellationToken, AddressProvider? only = null)
    {
        var provider = only ?? settings.Provider;
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(group))
        {
            return new AddressSearchResult(provider, []);
        }

        if (provider == AddressProvider.LocalFile)
        {
            return Local(text);
        }

        try
        {
            return provider == AddressProvider.Google
                ? new AddressSearchResult(provider, await GoogleSearchAsync(text, session, cancellationToken).ConfigureAwait(false), Attribution: "Results from Google")
                : new AddressSearchResult(provider, await RoyalMailSearchAsync(text, group, cancellationToken).ConfigureAwait(false), Attribution: "Royal Mail AddressNow");
        }
        catch (AddressServiceException ex) when (only is null && settings.FallbackToLocalFile && book.HasAddresses)
        {
            return Local(text) with { Problem = $"{ex.Message} Showing the shop's own address file instead." };
        }
    }

    /// <summary>The full address (and distance, when known) for a suggestion from an online service.</summary>
    public async Task<ResolvedAddress> ResolveAsync(string id, string? session, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var (address, latitude, longitude) = settings.Provider switch
        {
            AddressProvider.Google => await GoogleDetailsAsync(id, session, cancellationToken).ConfigureAwait(false),
            AddressProvider.RoyalMail => await RoyalMailRetrieveAsync(id, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("The local address files give the full address straight away."),
        };
        var miles = latitude is { } lat && longitude is { } lon ? await delivery.MilesFromShopAsync(lat, lon, cancellationToken).ConfigureAwait(false) : null;
        return new ResolvedAddress(address.Checked(), miles);
    }

    private AddressSearchResult Local(string text) =>
        new(AddressProvider.LocalFile, [.. book.Search(text).Select(o => new AddressSuggestion(null, o.Label, o.Address))]);

    // ---------- Google Places (New) ----------

    private async Task<List<AddressSuggestion>> GoogleSearchAsync(string text, string? session, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{settings.GoogleEndpoint.TrimEnd('/')}/v1/places:autocomplete"))
        {
            Content = JsonContent.Create(new { input = text, includedRegionCodes = UkOnly, sessionToken = session }),
        };
        request.Headers.Add("X-Goog-Api-Key", settings.GoogleApiKey ?? throw new AddressServiceException("No Google key is saved."));
        request.Headers.Add("X-Goog-FieldMask", "suggestions.placePrediction.placeId,suggestions.placePrediction.text.text");
        using var json = await SendAsync(request, "Google", cancellationToken).ConfigureAwait(false);
        var list = new List<AddressSuggestion>();
        if (json.RootElement.TryGetProperty("suggestions", out var suggestions))
        {
            foreach (var s in suggestions.EnumerateArray())
            {
                if (s.TryGetProperty("placePrediction", out var p))
                {
                    list.Add(new AddressSuggestion(p.GetProperty("placeId").GetString(), p.GetProperty("text").GetProperty("text").GetString() ?? string.Empty, null));
                }
            }
        }

        return list;
    }

    private async Task<(DeliveryAddress Address, double? Latitude, double? Longitude)> GoogleDetailsAsync(string placeId, string? session, CancellationToken cancellationToken)
    {
        var query = session is null ? string.Empty : $"?sessionToken={Uri.EscapeDataString(session)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{settings.GoogleEndpoint.TrimEnd('/')}/v1/places/{Uri.EscapeDataString(placeId)}{query}"));
        request.Headers.Add("X-Goog-Api-Key", settings.GoogleApiKey ?? throw new AddressServiceException("No Google key is saved."));
        request.Headers.Add("X-Goog-FieldMask", "addressComponents,location,displayName,types");
        using var json = await SendAsync(request, "Google", cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("addressComponents", out var components))
        {
            foreach (var c in components.EnumerateArray())
            {
                var text = c.GetProperty("longText").GetString() ?? string.Empty;
                foreach (var type in c.GetProperty("types").EnumerateArray())
                {
                    parts.TryAdd(type.GetString() ?? string.Empty, text);
                }
            }
        }

        string? P(string type) => parts.TryGetValue(type, out var v) && v.Length > 0 ? v : null;
        var isBusiness = root.TryGetProperty("types", out var types) && types.EnumerateArray().Any(t => t.GetString() == "establishment");
        var business = isBusiness && root.TryGetProperty("displayName", out var name) ? name.GetProperty("text").GetString() : null;
        var street = string.Join(' ', new[] { P("street_number"), P("route") }.Where(p => p is not null));
        var line1 = street.Length > 0 ? street : P("premise") ?? business ?? string.Empty;
        var line2 = string.Join(", ", new[] { business, P("subpremise"), street.Length > 0 ? P("premise") : null, P("neighborhood") ?? P("sublocality") }
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != line1).Distinct(StringComparer.OrdinalIgnoreCase));
        var postcode = P("postal_code") ?? throw new AddressServiceException("Google did not give a full postcode for that place. Choose a house, or type the address.");
        double? lat = null;
        double? lon = null;
        if (root.TryGetProperty("location", out var loc))
        {
            lat = loc.GetProperty("latitude").GetDouble();
            lon = loc.GetProperty("longitude").GetDouble();
        }

        return (new DeliveryAddress(line1, line2.Length == 0 ? null : line2, P("postal_town") ?? P("locality"), postcode), lat, lon);
    }

    // ---------- Royal Mail AddressNow (Loqate Capture) ----------

    private async Task<List<AddressSuggestion>> RoyalMailSearchAsync(string text, string? group, CancellationToken cancellationToken)
    {
        var key = settings.RoyalMailApiKey ?? throw new AddressServiceException("No Royal Mail key is saved.");
        var url = $"{settings.RoyalMailEndpoint.TrimEnd('/')}/Capture/Interactive/Find/v1.10/json3.ws?Key={Uri.EscapeDataString(key)}"
            + $"&Text={Uri.EscapeDataString(text ?? string.Empty)}&Countries=GB&Limit=60&Language=en"
            + (string.IsNullOrWhiteSpace(group) ? string.Empty : $"&Container={Uri.EscapeDataString(group)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        using var json = await SendAsync(request, "Royal Mail", cancellationToken).ConfigureAwait(false);
        var items = LoqateItems(json);
        return [.. items.Select(i =>
        {
            var type = Text(i, "Type");
            var description = Text(i, "Description");
            var label = string.IsNullOrEmpty(description) ? Text(i, "Text") ?? string.Empty : $"{Text(i, "Text")}, {description}";
            return new AddressSuggestion(Text(i, "Id"), label, null, IsGroup: !string.Equals(type, "Address", StringComparison.OrdinalIgnoreCase));
        })];
    }

    private async Task<(DeliveryAddress Address, double? Latitude, double? Longitude)> RoyalMailRetrieveAsync(string id, CancellationToken cancellationToken)
    {
        var key = settings.RoyalMailApiKey ?? throw new AddressServiceException("No Royal Mail key is saved.");
        var url = $"{settings.RoyalMailEndpoint.TrimEnd('/')}/Capture/Interactive/Retrieve/v1.20/json3.ws?Key={Uri.EscapeDataString(key)}&Id={Uri.EscapeDataString(id)}"
            + "&Field1Format=%7BLatitude%7D&Field2Format=%7BLongitude%7D";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        using var json = await SendAsync(request, "Royal Mail", cancellationToken).ConfigureAwait(false);
        var item = LoqateItems(json).FirstOrDefault();
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new AddressServiceException("Royal Mail did not return that address.");
        }

        string? V(string name) => Text(item, name) is { Length: > 0 } v ? v : null;
        var dependent = V("SecondaryStreet");
        var street = string.Join(' ', new[] { V("BuildingNumber"), dependent ?? V("Street") }.Where(p => p is not null));
        var line1 = street.Length > 0 ? street : V("BuildingName") ?? V("SubBuilding") ?? V("Line1") ?? string.Empty;
        var line2 = string.Join(", ", new[] { V("Company"), V("SubBuilding"), street.Length > 0 ? V("BuildingName") : null, dependent is null ? null : V("Street"), V("District") }
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != line1).Distinct(StringComparer.OrdinalIgnoreCase));
        var postcode = V("PostalCode") ?? throw new AddressServiceException("Royal Mail did not give a postcode for that address.");
        double? lat = double.TryParse(V("Field1"), NumberStyles.Float, CultureInfo.InvariantCulture, out var la) ? la : null;
        double? lon = double.TryParse(V("Field2"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) ? lo : null;
        return (new DeliveryAddress(line1, line2.Length == 0 ? null : line2, V("City"), postcode), lat, lon);
    }

    /// <summary>The "Items" of a Loqate answer; an item with "Error" is turned into a clear message.</summary>
    private static List<JsonElement> LoqateItems(JsonDocument json)
    {
        var items = json.RootElement.TryGetProperty("Items", out var array) ? array.EnumerateArray().ToList() : [];
        if (items.Count > 0 && items[0].TryGetProperty("Error", out _))
        {
            var description = Text(items[0], "Description");
            var resolution = Text(items[0], "Resolution");
            throw new AddressServiceException($"Royal Mail refused the search: {description}. {resolution}".Trim());
        }

        return items;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, string service, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new AddressServiceException($"{service} cannot be reached (check the internet).", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AddressServiceException($"{service} refused the key ({(int)response.StatusCode}). Check the key in More → Address lookup.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AddressServiceException($"{service} answered {(int)response.StatusCode}{GoogleMessage(body)}.");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new AddressServiceException($"{service} sent an answer the hub could not read.", ex);
            }
        }
    }

    /// <summary>Google puts the reason in error.message.</summary>
    private static string GoogleMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? $": {m.GetString()}" : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
