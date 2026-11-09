using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Data;
using Pos.Hub.Delivery;
using Pos.Hub.Security;

namespace Pos.Hub.Online;

/// <summary>One opening time, e.g. 17:00 to 23:30 (a close at or before the open time runs past midnight).</summary>
/// <param name="Open">Opening time, HH:mm (UK time).</param>
/// <param name="Close">Closing time, HH:mm.</param>
public sealed record OpeningSlot(string Open, string Close);

/// <summary>The website settings (More → Website, owner).</summary>
/// <param name="Enabled">Connect to the online ordering service.</param>
/// <param name="ServiceUrl">The website address, e.g. https://order.myshop.co.uk.</param>
/// <param name="ShopKey">The secret shop key also set on the online service (never shown again once saved).</param>
/// <param name="TakeOrders">Take website orders now (a quick off switch for busy nights).</param>
/// <param name="Hours">Opening times by day: "mon" … "sun".</param>
/// <param name="Collection">Offer collection.</param>
/// <param name="CollectionMinutes">Usual time until a collection order is ready.</param>
/// <param name="Delivery">Offer delivery (uses the shop's delivery charges).</param>
/// <param name="DeliveryMinutes">Usual time until a delivery arrives.</param>
/// <param name="AllergenNote">Shown on the website, e.g. "Ask us about allergens before ordering".</param>
public sealed record OnlineSettings(
    bool Enabled,
    string? ServiceUrl,
    string? ShopKey,
    bool TakeOrders,
    IReadOnlyDictionary<string, IReadOnlyList<OpeningSlot>> Hours,
    bool Collection,
    int CollectionMinutes,
    bool Delivery,
    int DeliveryMinutes,
    string? AllergenNote)
{
    /// <summary>The days, Monday first.</summary>
    public static readonly IReadOnlyList<string> Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    /// <summary>A new shop: off, typical takeaway evening hours.</summary>
    public static readonly OnlineSettings Default = new(false, null, null, false,
        Days.ToDictionary(d => d, _ => (IReadOnlyList<OpeningSlot>)[new OpeningSlot("17:00", "23:00")]),
        true, 20, true, 45, "If you have a food allergy or intolerance, please phone us before you order.");

    /// <summary>Checked and tidied; throws <see cref="ArgumentException"/> with a clear reason.</summary>
    public OnlineSettings Checked(string? keptKey)
    {
        string? url = null;
        if (!string.IsNullOrWhiteSpace(ServiceUrl))
        {
            if (!Uri.TryCreate(ServiceUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback) || !string.IsNullOrEmpty(uri.Query))
            {
                throw new ArgumentException("The website address must start with https://, e.g. https://order.myshop.co.uk.");
            }

            url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        var key = string.IsNullOrWhiteSpace(ShopKey) ? keptKey : ShopKey.Trim();
        if (key is not null && (key.Length is < 32 or > 200 || key.Any(c => c <= ' ')))
        {
            throw new ArgumentException("The shop key must be at least 32 characters with no spaces. Use \"Make a new key\".");
        }

        if (Enabled && (url is null || key is null))
        {
            throw new ArgumentException("Give the website address and the shop key to connect.");
        }

        if (CollectionMinutes is < 5 or > 180 || DeliveryMinutes is < 10 or > 240)
        {
            throw new ArgumentException("Ready times: collection 5 to 180 minutes, delivery 10 to 240 minutes.");
        }

        var hours = new Dictionary<string, IReadOnlyList<OpeningSlot>>();
        foreach (var day in Days)
        {
            var slots = Hours is not null && Hours.TryGetValue(day, out var s) ? s ?? [] : [];
            if (slots.Count > 3)
            {
                throw new ArgumentException("At most 3 opening times a day.");
            }

            hours[day] = [.. slots.Select(x => new OpeningSlot(Time(x.Open), Time(x.Close)))];
        }

        var note = string.IsNullOrWhiteSpace(AllergenNote) ? null : AllergenNote.Trim();
        if (note?.Length > 300)
        {
            throw new ArgumentException("The allergen note is longer than 300 characters.");
        }

        return new OnlineSettings(Enabled, url, key, TakeOrders, hours, Collection, CollectionMinutes, Delivery, DeliveryMinutes, note);
    }

    /// <summary>A new random shop key (to paste into the online service too).</summary>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Time(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("HH:mm", CultureInfo.InvariantCulture)
            : throw new ArgumentException($"\"{text}\" is not a time. Write it like 17:30.");
}

/// <summary>The website settings as the till shows them: the key is never sent back, only whether one is set.</summary>
/// <param name="Settings">The settings, without the key.</param>
/// <param name="HasKey">A shop key is saved.</param>
/// <param name="Status">How the connection is going.</param>
public sealed record OnlineSettingsView(OnlineSettings Settings, bool HasKey, OnlineStatus Status);

/// <summary>How the connection to the online service is going.</summary>
/// <param name="Connected">The last check-in worked.</param>
/// <param name="LastContact">When the last check-in worked.</param>
/// <param name="LastPublished">When the menu was last sent.</param>
/// <param name="Problem">The last problem, if any.</param>
/// <param name="OrdersToday">Website orders received today.</param>
public sealed record OnlineStatus(bool Connected, DateTimeOffset? LastContact, DateTimeOffset? LastPublished, string? Problem, int OrdersToday);

/// <summary>
/// Connects the shop to the online ordering service: sends the menu and shop details when they change, collects
/// website orders every few seconds and puts them on the tills (waiting to be accepted), and tells the website what
/// happened to each order. The hub always calls out, so nothing on the shop's router needs opening, and the tills keep
/// working when the internet is down (orders wait online, and the website stops taking them after two minutes).
/// </summary>
public sealed class OnlineOrderService : IDisposable
{
    /// <summary>The "staff member" recorded for orders placed on the website.</summary>
    public static readonly Guid WebsiteUserId = new("7eb5e1e0-0000-4000-8000-00000000c0de");

    private const string Key = "online";
    private static readonly OrderStatus[] Finished = [OrderStatus.Completed, OrderStatus.Rejected, OrderStatus.Cancelled];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HubDatabase _db;
    private readonly OrderService _orders;
    private readonly MenuService _menu;
    private readonly ShopDetailsService _shop;
    private readonly DeliveryService _delivery;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _publishedHash;
    private DateTimeOffset? _lastContact;
    private DateTimeOffset? _lastPublished;
    private string? _problem;

    /// <summary>Creates the service.</summary>
    public OnlineOrderService(HubDatabase db, OrderService orders, MenuService menu, ShopDetailsService shop, DeliveryService delivery, TimeProvider clock, HttpMessageHandler? handler = null)
    {
        _db = db;
        _orders = orders;
        _menu = menu;
        _shop = shop;
        _delivery = delivery;
        _clock = clock;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>The settings in force (with the key, for the hub only).</summary>
    public OnlineSettings Settings()
    {
        var saved = _db.Run(() => _db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) as string);
        return saved is null ? OnlineSettings.Default : PosJson.Deserialize<OnlineSettings>(saved);
    }

    /// <summary>The settings for the till, without the key.</summary>
    public OnlineSettingsView View()
    {
        var s = Settings();
        return new OnlineSettingsView(s with { ShopKey = null }, s.ShopKey is not null, Status());
    }

    /// <summary>Saves the settings. An empty key keeps the saved one. The menu is sent again on the next check-in.</summary>
    public OnlineSettingsView Save(OnlineSettings settings, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = Settings();
        var checkedSettings = settings.Checked(current.ShopKey);
        _db.Run(() => _db.Execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
            ("$k", Key), ("$v", PosJson.Serialize(checkedSettings)), ("$at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString())));
        _publishedHash = null;
        return View();
    }

    /// <summary>How the connection is going.</summary>
    public OnlineStatus Status()
    {
        var today = _orders.ForDay(null, null).Count(o => o.Source == OrderSource.Website);
        var connected = _lastContact is { } at && _clock.GetUtcNow() - at < TimeSpan.FromMinutes(1);
        return new OnlineStatus(connected, _lastContact, _lastPublished, _problem, today);
    }

    /// <summary>
    /// One round: sends the menu if it changed, checks in (sending what happened to website orders), and puts new website
    /// orders on the tills. Returns the number of new orders. Problems are recorded for the Website screen, never thrown.
    /// </summary>
    public async Task<int> SyncAsync(CancellationToken cancellationToken)
    {
        var settings = Settings();
        if (!settings.Enabled || settings.ServiceUrl is null || settings.ShopKey is null)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PublishIfChangedAsync(settings, cancellationToken).ConfigureAwait(false);
            var created = await PollAsync(settings, cancellationToken).ConfigureAwait(false);
            if (created > 0)
            {
                await PollAsync(settings, cancellationToken).ConfigureAwait(false); // tell the customers straight away
            }

            _problem = null;
            return created;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OnlineServiceException or JsonException)
        {
            _problem = ex is TaskCanceledException && !cancellationToken.IsCancellationRequested ? "The online service did not answer in time." : ex.Message;
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends the menu and shop details now (after the owner asks, or a change).</summary>
    public async Task PublishNowAsync(CancellationToken cancellationToken)
    {
        _publishedHash = null;
        var settings = Settings();
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("Connect the website first (tick Connect and save).");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PublishIfChangedAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach the website: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- Publishing ----------

    /// <summary>What the website shows: shop details, opening times, delivery rules, and the menu (items, sizes, extras).</summary>
    internal async Task<object> PublicationAsync(OnlineSettings settings, CancellationToken cancellationToken)
    {
        var shop = _shop.Get();
        object? delivery = null;
        if (settings.Delivery && !string.IsNullOrWhiteSpace(_delivery.Settings.ShopPostcode))
        {
            try
            {
                var at = await _delivery.LocatePostcodeAsync(_delivery.Settings.ShopPostcode, cancellationToken).ConfigureAwait(false);
                var table = _delivery.Settings.Table().ToState();
                delivery = new
                {
                    enabled = true,
                    minutes = settings.DeliveryMinutes,
                    roadFactor = _delivery.Settings.RoadFactor,
                    shopLocation = new { latitude = at.Latitude, longitude = at.Longitude },
                    table = new
                    {
                        bands = table.Bands.Select(b => new { fromMiles = b.FromMiles, toMiles = b.ToMiles, feePence = b.Fee.MinorUnits, minimumOrderPence = b.MinimumOrder?.MinorUnits, freeOverPence = b.FreeOver?.MinorUnits }),
                        ratePerMilePence = table.RatePerMile?.MinorUnits,
                        minimumFeePence = table.MinimumFee?.MinorUnits,
                        maximumFeePence = table.MaximumFee?.MinorUnits,
                        maximumMiles = table.MaximumMiles,
                        minimumOrderPence = table.MinimumOrder?.MinorUnits,
                        freeOverPence = table.FreeOver?.MinorUnits,
                    },
                };
            }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException or PostcodeServiceUnavailableException)
            {
                _problem = $"Delivery is not offered on the website: {ex.Message}";
            }
        }

        var state = _menu.Read(m => m.ToState());
        var groups = state.ModifierGroups.ToDictionary(g => g.Id);
        var products = state.Products.Where(p => p.Type == ProductType.Standard && !p.IsHidden).OrderBy(p => p.SortOrder).ToList();
        var categories = state.Categories.Where(c => !c.IsHidden && products.Any(p => p.CategoryId == c.Id)).OrderBy(c => c.SortOrder).ToList();
        products = [.. products.Where(p => categories.Any(c => c.Id == p.CategoryId))];
        var usedGroups = products.SelectMany(p => p.ModifierGroupIds).Distinct().Where(groups.ContainsKey).Select(id => groups[id]).ToList();
        return new
        {
            info = new
            {
                name = shop.BusinessName,
                address = new[] { shop.AddressLine1, shop.AddressLine2, shop.Town }.Where(x => !string.IsNullOrWhiteSpace(x)),
                postcode = shop.Postcode,
                phone = shop.Phone,
                hours = settings.Hours,
                collection = new { enabled = settings.Collection, minutes = settings.CollectionMinutes },
                delivery = delivery ?? new { enabled = false },
                allergenNote = settings.AllergenNote,
            },
            menu = new
            {
                categories = categories.Select(c => new { id = c.Id, name = c.Name.En, colour = c.Colour }),
                groups = usedGroups.Select(g => new
                {
                    id = g.Id, name = g.Name.En, min = g.MinChoices, max = g.MaxChoices,
                    options = g.Options.Select(o => new { id = o.Id, name = o.Name.En, pricePence = o.Price.MinorUnits, soldOut = o.IsSoldOut }),
                }),
                products = products.Select(p => new
                {
                    id = p.Id, categoryId = p.CategoryId, name = p.Name.En, description = p.Description?.En, allergens = p.Allergens, soldOut = p.IsSoldOut,
                    groupIds = p.ModifierGroupIds.Where(groups.ContainsKey),
                    variants = p.Variants.Select(v => new { id = v.Id, name = v.Name.En, pricePence = v.Price.MinorUnits, soldOut = v.IsSoldOut }),
                }),
            },
        };
    }

    private async Task PublishIfChangedAsync(OnlineSettings settings, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(await PublicationAsync(settings, cancellationToken).ConfigureAwait(false), Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        if (hash == _publishedHash)
        {
            return;
        }

        using var request = Request(HttpMethod.Put, settings, "/hub/publish");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
        _publishedHash = hash;
        _lastPublished = _clock.GetUtcNow();
    }

    // ---------- Orders ----------

    private async Task<int> PollAsync(OnlineSettings settings, CancellationToken cancellationToken)
    {
        var updates = PendingUpdates(settings);
        using var request = Request(HttpMethod.Post, settings, "/hub/poll");
        request.Content = JsonContent.Create(new { accepting = settings.TakeOrders, updates = updates.Select(u => u.Update) }, options: Json);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var poll = await response.Content.ReadFromJsonAsync<PollResponse>(Json, cancellationToken).ConfigureAwait(false) ?? new PollResponse([]);
        _lastContact = _clock.GetUtcNow();

        // What was sent is now known online.
        _db.Run(() =>
        {
            foreach (var (cloudId, update) in updates)
            {
                _db.Execute("UPDATE online_orders SET sent_status = $s, sent_ready_at = $r WHERE cloud_id = $id;", ("$s", update.Status), ("$r", update.ReadyAt), ("$id", cloudId));
            }

            return 0;
        });

        var created = 0;
        foreach (var order in poll.Orders)
        {
            if (await TakeAsync(settings, order, cancellationToken).ConfigureAwait(false))
            {
                created++;
            }
        }

        return created;
    }

    /// <summary>Puts a website order on the tills, waiting to be accepted; a problem is sent back to the customer instead.</summary>
    private async Task<bool> TakeAsync(OnlineSettings settings, WebOrder web, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(web.Id, out var id) || _db.Run(() => _db.Scalar("SELECT 1 FROM online_orders WHERE cloud_id = $id;", ("$id", web.Id))) is not null)
        {
            return false; // already here (the website sends an order until it hears it arrived)
        }

        string? reason = null;
        try
        {
            var isDelivery = web.Type == "Delivery";
            DeliveryAddress? address = null;
            DeliveryRequest? delivery = null;
            if (isDelivery)
            {
                address = new DeliveryAddress(web.Customer.Address ?? string.Empty, null, web.Customer.Town, web.Customer.Postcode ?? string.Empty);
                delivery = new DeliveryRequest(await MilesAsync(web, cancellationToken).ConfigureAwait(false));
            }

            var request = new CreateOrderRequest(
                id, $"web:{web.Id}", OrderSource.Website, isDelivery ? OrderType.Delivery : OrderType.Takeaway, null, null,
                [.. web.Lines.Select(l => new OrderLineRequest(l.ProductId, l.VariantId, l.Quantity, l.OptionIds, null, l.Note))],
                new OrderContact(web.Customer.Name, web.Customer.Phone, address, web.Customer.Notes),
                delivery);
            _orders.Submit(request, new OrderActor(WebsiteUserId), feeApprover: WebsiteUserId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // E.g. an item sold out on the till a moment ago: the customer is told at once rather than waiting.
            reason = $"Sorry, we could not take this order: {ex.Message} Please order again or phone us.";
        }

        _db.Run(() => _db.Execute(
            "INSERT INTO online_orders (cloud_id, order_id, received_at, rejected_reason, ready_minutes) VALUES ($id, $o, $at, $r, $m);",
            ("$id", web.Id), ("$o", reason is null ? id.ToString() : null), ("$at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$r", reason),
            ("$m", web.Type == "Delivery" ? settings.DeliveryMinutes : settings.CollectionMinutes)));
        return reason is null;
    }

    private async Task<decimal> MilesAsync(WebOrder web, CancellationToken cancellationToken)
    {
        // The shop's own distance for the postcode; the website's figure if the postcode service is not answering.
        try
        {
            var check = await _delivery.CheckAsync(web.Customer.Postcode ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (check.DistanceMiles is { } miles)
            {
                return miles;
            }
        }
        catch (PostcodeServiceUnavailableException)
        {
        }

        return web.DeliveryMiles ?? throw new InvalidOperationException("The delivery distance is not known.");
    }

    /// <summary>What to tell the website about each order still going, if it changed since last time.</summary>
    private List<(string CloudId, StatusUpdate Update)> PendingUpdates(OnlineSettings settings) => _db.Run(() =>
    {
        var rows = new List<(string Cloud, string? Order, string? Sent, string? Reason, string Received, int Minutes)>();
        using (var command = _db.Command("""
            SELECT cloud_id, order_id, sent_status, rejected_reason, received_at, ready_minutes FROM online_orders
            WHERE sent_status IS NULL OR sent_status NOT IN ('Completed', 'Rejected', 'Cancelled') ORDER BY received_at LIMIT 200;
            """))
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
            }
        }

        var updates = new List<(string, StatusUpdate)>();
        foreach (var row in rows)
        {
            if (row.Order is null)
            {
                if (row.Sent != "Rejected")
                {
                    updates.Add((row.Cloud, new StatusUpdate(row.Cloud, "Rejected", null, null, row.Reason, null)));
                }

                continue;
            }

            Order order;
            try
            {
                order = _orders.Get(Guid.Parse(row.Order));
            }
            catch (KeyNotFoundException)
            {
                continue;
            }

            var update = UpdateFor(row.Cloud, order, row.Minutes);
            if (update.Status != row.Sent)
            {
                updates.Add((row.Cloud, update));
            }
        }

        return updates;
    });

    private static StatusUpdate UpdateFor(string cloudId, Order order, int minutes)
    {
        var status = order.Status switch
        {
            OrderStatus.Pending or OrderStatus.Received => "Received",
            OrderStatus.Accepted or OrderStatus.Preparing => "Accepted",
            OrderStatus.Ready => "Ready",
            OrderStatus.OutForDelivery => "OutForDelivery",
            OrderStatus.Completed => "Completed",
            OrderStatus.Rejected => "Rejected",
            _ => "Cancelled",
        };
        var acceptedEvent = order.History.FirstOrDefault(e => e.Kind == OrderEventKind.StatusChanged && e.ToStatus == OrderStatus.Accepted);
        var readyAt = status is "Accepted" or "Ready" or "OutForDelivery" && acceptedEvent is { } a
            ? a.At.AddMinutes(ReadyMinutes(a.Reason, minutes)).ToString("O", CultureInfo.InvariantCulture)
            : null;
        var reason = Finished.Contains(order.Status) && order.Status != OrderStatus.Completed
            ? order.History.LastOrDefault(e => e.Kind == OrderEventKind.StatusChanged)?.Reason
            : null;
        return new StatusUpdate(cloudId, status, order.Number.Display, readyAt, reason, (int)order.Totals.Total.MinorUnits);
    }

    /// <summary>The minutes staff chose when accepting ("Ready in 25 minutes", recorded on the order), else the usual time.</summary>
    internal static int ReadyMinutes(string? acceptedNote, int usual)
    {
        const string marker = "ready in ";
        var i = acceptedNote?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        if (i >= 0)
        {
            var digits = new string([.. acceptedNote![(i + marker.Length)..].TakeWhile(char.IsAsciiDigit)]);
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is > 0 and <= 600)
            {
                return m;
            }
        }

        return usual;
    }

    // ---------- HTTP ----------

    private static HttpRequestMessage Request(HttpMethod method, OnlineSettings settings, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(settings.ServiceUrl + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ShopKey);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var message = await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false);
            throw new OnlineServiceException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "The website refused the shop key. Check that it is the same on the website and in More → Website.",
                System.Net.HttpStatusCode.NotFound => "The website address does not lead to the online ordering service. Check it.",
                _ => $"The website answered: {message ?? response.ReasonPhrase}",
            });
        }
    }

    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private sealed record PollResponse(IReadOnlyList<WebOrder> Orders);

    private sealed record WebCustomer(string? Name, string? Phone, string? Notes, string? Address, string? Town, string? Postcode);

    private sealed record WebLine(Guid ProductId, Guid VariantId, int Quantity, IReadOnlyList<Guid>? OptionIds, string? Note);

    private sealed record WebOrder(string Id, string Type, WebCustomer Customer, IReadOnlyList<WebLine> Lines, decimal? DeliveryMiles);

    private sealed record StatusUpdate(string Id, string Status, string? Number, string? ReadyAt, string? Reason, int? TotalPence);
}

/// <summary>How the hub reaches the online service (tests put a stand-in here).</summary>
public sealed class OnlineHttp
{
    /// <summary>The HTTP handler to use, or null for the normal one.</summary>
    public HttpMessageHandler? Handler { get; set; }
}

/// <summary>The online service refused or failed a request (the message says what to do).</summary>
public sealed class OnlineServiceException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OnlineServiceException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public OnlineServiceException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public OnlineServiceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Checks in with the online service every few seconds while the website is connected.</summary>
public sealed class OnlineWorker(OnlineOrderService online, TimeProvider clock) : BackgroundService
{
    /// <summary>Seconds between check-ins.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await online.SyncAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(Interval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
