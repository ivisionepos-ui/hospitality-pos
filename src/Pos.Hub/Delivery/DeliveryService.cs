using System.Globalization;
using System.Net;
using System.Text.Json;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Hub.Data;

namespace Pos.Hub.Delivery;

/// <summary>Where a postcode is.</summary>
/// <param name="Postcode">Postcode in standard form.</param>
/// <param name="Latitude">Latitude.</param>
/// <param name="Longitude">Longitude.</param>
/// <param name="Town">Town or district, if known.</param>
public sealed record PostcodeLocation(string Postcode, double Latitude, double Longitude, string? Town);

/// <summary>A checked delivery postcode with its distance from the shop.</summary>
/// <param name="Postcode">Postcode in standard form.</param>
/// <param name="Town">Town or district, if known.</param>
/// <param name="DistanceMiles">Estimated road distance from the shop, in miles (one decimal); empty when it could not be worked out.</param>
/// <param name="Addresses">Every address in the postcode, from the shop's address files (empty if none loaded for it).</param>
/// <param name="Problem">Why the distance is missing (e.g. no internet), for the till to show.</param>
public sealed record PostcodeCheck(string Postcode, string? Town, decimal? DistanceMiles, IReadOnlyList<AddressOption> Addresses, string? Problem = null);

/// <summary>Finds where a postcode is.</summary>
public interface IPostcodeLookup
{
    /// <summary>The postcode's location, or null if no such postcode exists. Throws <see cref="PostcodeServiceUnavailableException"/> when the service cannot be reached.</summary>
    Task<PostcodeLocation?> FindAsync(string postcode, CancellationToken cancellationToken);
}

/// <summary>The postcode service could not be reached (no internet, or the service is down).</summary>
public sealed class PostcodeServiceUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PostcodeServiceUnavailableException()
        : base("The postcode service cannot be reached (check the internet). Type the distance in miles instead.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PostcodeServiceUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PostcodeServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>postcodes.io: free UK postcode data (Office for National Statistics), no account needed.</summary>
public sealed class PostcodesIoLookup(HttpClient http, DeliverySettings settings) : IPostcodeLookup
{
    /// <inheritdoc />
    public async Task<PostcodeLocation?> FindAsync(string postcode, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(new Uri($"{settings.PostcodeService.TrimEnd('/')}/postcodes/{Uri.EscapeDataString(postcode)}"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new PostcodeServiceUnavailableException("The postcode service cannot be reached (check the internet). Type the distance in miles instead.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PostcodeServiceUnavailableException($"The postcode service answered {(int)response.StatusCode}. Type the distance in miles instead.");
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = json.RootElement.GetProperty("result");
            if (result.GetProperty("latitude").ValueKind != JsonValueKind.Number)
            {
                return null; // a postcode with no location (e.g. a PO box)
            }

            string? Text(string name) => result.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new PostcodeLocation(Text("postcode") ?? postcode, result.GetProperty("latitude").GetDouble(), result.GetProperty("longitude").GetDouble(),
                Text("admin_district") ?? Text("parish"));
        }
    }
}

/// <summary>
/// Checks delivery postcodes and works out the distance from the shop. Every postcode looked up is
/// remembered in the hub database, so repeat customers work without internet and nothing is paid twice.
/// </summary>
public sealed class DeliveryService(HubDatabase db, DeliverySettings settings, IPostcodeLookup lookup, TimeProvider clock, AddressBook addresses)
{
    private const double EarthRadiusMiles = 3958.8;

    /// <summary>The delivery settings.</summary>
    public DeliverySettings Settings => settings;

    /// <summary>Checks a postcode and returns its distance from the shop.</summary>
    public async Task<PostcodeCheck> CheckAsync(string postcode, CancellationToken cancellationToken)
    {
        if (!UkPostcode.TryNormalise(postcode, out var normalised))
        {
            throw new ArgumentException($"\"{postcode}\" is not a UK postcode.");
        }

        if (!UkPostcode.TryNormalise(settings.ShopPostcode, out var shopPostcode))
        {
            throw new InvalidOperationException("The shop's postcode is not set. Add it as ShopPostcode in the Delivery settings.");
        }

        var houses = addresses.Find(normalised);
        try
        {
            var shop = await LocateAsync(shopPostcode, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The shop's postcode {shopPostcode} was not found. Check ShopPostcode in the Delivery settings.");
            var place = await LocateAsync(normalised, cancellationToken).ConfigureAwait(false);
            if (place is null)
            {
                return houses.Count > 0
                    ? new PostcodeCheck(normalised, houses[0].Address.Town, null, houses, "The distance for this postcode is not known yet. Type the miles.")
                    : throw new KeyNotFoundException($"Postcode {normalised} was not found. Check it with the customer.");
            }

            return new PostcodeCheck(place.Postcode, place.Town ?? (houses.Count > 0 ? houses[0].Address.Town : null), Miles(shop, place), houses);
        }
        catch (PostcodeServiceUnavailableException ex) when (houses.Count > 0)
        {
            // No internet: the address list still works from the shop's own files; the miles are typed in.
            return new PostcodeCheck(normalised, houses[0].Address.Town, null, houses, ex.Message);
        }
    }

    /// <summary>
    /// Distance from the shop to a point (e.g. from Google or Royal Mail), or null when the shop's own location
    /// cannot be found right now.
    /// </summary>
    public async Task<decimal?> MilesFromShopAsync(double latitude, double longitude, CancellationToken cancellationToken)
    {
        if (!UkPostcode.TryNormalise(settings.ShopPostcode, out var shopPostcode))
        {
            return null;
        }

        try
        {
            var shop = await LocateAsync(shopPostcode, cancellationToken).ConfigureAwait(false);
            return shop is null ? null : Miles(shop, new PostcodeLocation(string.Empty, latitude, longitude, null));
        }
        catch (PostcodeServiceUnavailableException)
        {
            return null;
        }
    }

    /// <summary>Estimated road distance in miles, to one decimal.</summary>
    public decimal Miles(PostcodeLocation from, PostcodeLocation to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        static double Rad(double degrees) => degrees * Math.PI / 180;
        var dLat = Rad(to.Latitude - from.Latitude);
        var dLon = Rad(to.Longitude - from.Longitude);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) + (Math.Cos(Rad(from.Latitude)) * Math.Cos(Rad(to.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        var straight = 2 * EarthRadiusMiles * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        return Math.Round((decimal)straight * settings.RoadFactor, 1, MidpointRounding.AwayFromZero);
    }

    private async Task<PostcodeLocation?> LocateAsync(string postcode, CancellationToken cancellationToken)
    {
        var cached = db.Run(() =>
        {
            using var command = db.Command("SELECT latitude, longitude, town FROM postcode_cache WHERE postcode = $p;", ("$p", postcode));
            using var reader = command.ExecuteReader();
            return reader.Read() ? new PostcodeLocation(postcode, reader.GetDouble(0), reader.GetDouble(1), reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
        });
        if (cached is not null)
        {
            return cached;
        }

        var found = await lookup.FindAsync(postcode, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        found = found with { Postcode = postcode };
        db.Run(() => db.Execute(
            "INSERT OR REPLACE INTO postcode_cache (postcode, latitude, longitude, town, fetched_at) VALUES ($p, $lat, $lon, $t, $at);",
            ("$p", postcode), ("$lat", found.Latitude), ("$lon", found.Longitude), ("$t", found.Town),
            ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture))));
        return found;
    }
}

/// <summary>A customer remembered by phone number.</summary>
/// <param name="Id">Id.</param>
/// <param name="Phone">Phone, digits only.</param>
/// <param name="Name">Name.</param>
/// <param name="Address">Last delivery address.</param>
/// <param name="Notes">Notes for the driver or shop.</param>
/// <param name="LastOrderAt">Last order.</param>
/// <param name="OrderCount">Orders so far.</param>
public sealed record CustomerRecord(Guid Id, string Phone, string? Name, DeliveryAddress? Address, string? Notes, DateTimeOffset? LastOrderAt, int OrderCount);

/// <summary>Customers remembered by phone number, so the next order fills in their name and address.</summary>
public sealed class CustomerService(HubDatabase db, TimeProvider clock)
{
    /// <summary>Customers whose phone number contains these digits (at least 4), most recent first.</summary>
    public IReadOnlyList<CustomerRecord> Search(string digits)
    {
        var wanted = new string([.. (digits ?? string.Empty).Where(char.IsAsciiDigit)]);
        if (wanted.StartsWith("44", StringComparison.Ordinal) && wanted.Length > 10)
        {
            wanted = "0" + wanted[2..];
        }

        if (wanted.Length < 4)
        {
            return [];
        }

        return db.Run(() =>
        {
            using var command = db.Command(
                """
                SELECT id, phone, name, address_json, notes, last_order_at, order_count FROM customers
                WHERE instr(phone, $d) > 0 ORDER BY (phone = $d) DESC, last_order_at DESC LIMIT 10;
                """, ("$d", wanted));
            using var reader = command.ExecuteReader();
            var found = new List<CustomerRecord>();
            while (reader.Read())
            {
                found.Add(new CustomerRecord(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : PosJson.Deserialize<DeliveryAddress>(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    reader.GetInt32(6)));
            }

            return found;
        });
    }

    /// <summary>Saves the customer from an order's details (keyed by phone) and returns their id. Call inside the order's transaction.</summary>
    public Guid Remember(OrderContact contact, bool countOrder)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var phone = contact.Phone ?? throw new ArgumentException("A phone number is needed to remember a customer.");
        var now = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        db.Execute(
            """
            INSERT INTO customers (id, phone, name, address_json, notes, created_at, updated_at, last_order_at, order_count)
            VALUES ($id, $phone, $name, $address, $notes, $now, $now, $last, $count)
            ON CONFLICT (phone) DO UPDATE SET
                name = COALESCE($name, name),
                address_json = COALESCE($address, address_json),
                notes = $notes,
                updated_at = $now,
                last_order_at = COALESCE($last, last_order_at),
                order_count = order_count + $count;
            """,
            ("$id", Guid.CreateVersion7().ToString()), ("$phone", phone), ("$name", contact.Name),
            ("$address", contact.Address is { } a ? PosJson.Serialize(a) : null), ("$notes", contact.Notes), ("$now", now),
            ("$last", countOrder ? now : null), ("$count", countOrder ? 1 : 0));
        return Guid.Parse((string)db.Scalar("SELECT id FROM customers WHERE phone = $phone;", ("$phone", phone))!);
    }
}
