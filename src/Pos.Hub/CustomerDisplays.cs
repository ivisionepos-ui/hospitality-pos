using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Pos.Domain.Common;
using Pos.Hub.Data;
using Pos.Hub.Live;
using Pos.Hub.Security;

namespace Pos.Hub;

/// <summary>Which till a customer display follows.</summary>
/// <param name="DisplayId">The customer display.</param>
/// <param name="TillId">The till whose order it shows.</param>
public sealed record CustomerDisplayLink(Guid DisplayId, Guid TillId);

/// <summary>The owner's settings for customer displays (More → Customer displays).</summary>
/// <param name="Links">Which till each display follows.</param>
/// <param name="Welcome">Big line shown while no order is on the till, e.g. "Welcome to Ali's Kebabs".</param>
/// <param name="Messages">Up to five lines shown in turn under it, e.g. "Try our new mixed grill".</param>
public sealed record CustomerDisplaySettings(IReadOnlyList<CustomerDisplayLink> Links, string? Welcome, IReadOnlyList<string> Messages)
{
    /// <summary>Most messages that turn in the idle screen.</summary>
    public const int MaxMessages = 5;

    /// <summary>Longest welcome line or message.</summary>
    public const int MaxText = 80;

    /// <summary>Nothing linked, no messages: the display shows the shop's name.</summary>
    public static CustomerDisplaySettings Default { get; } = new([], null, []);
}

/// <summary>One line of the order as the till shows it.</summary>
/// <param name="Quantity">How many.</param>
/// <param name="Name">Item (with size), e.g. "Large Doner kebab".</param>
/// <param name="Details">Extras and meal-deal items.</param>
/// <param name="Price">The price as the till shows it, e.g. "£7.50".</param>
/// <param name="Removed">True for an item taken off a saved order (shown crossed out).</param>
public sealed record DisplayLine(int Quantity, string Name, IReadOnlyList<string>? Details, string? Price, bool Removed = false);

/// <summary>One total row as the till shows it ("Delivery (2.1 mi)", "£2.50").</summary>
/// <param name="Label">Label.</param>
/// <param name="Value">Amount as shown.</param>
/// <param name="Kind">"total", "due" or empty: how big it is shown.</param>
public sealed record DisplayTotal(string Label, string Value, string? Kind);

/// <summary>
/// What the customer sees, sent by the till whenever its screen changes. The till already shows these figures, so the
/// display shows them as they are: it always matches the till. Never contains the customer's phone or address.
/// </summary>
/// <param name="Stage">"idle" (no order), "order" (items being added), "pay" (taking payment) or "thanks" (paid).</param>
/// <param name="OrderNumber">Order number once saved, e.g. "015".</param>
/// <param name="OrderType">"Takeaway", "Eat in · Table 4", "Delivery", …</param>
/// <param name="Lines">The items.</param>
/// <param name="Totals">Totals as on the till.</param>
/// <param name="ToPay">Amount still to pay while paying.</param>
/// <param name="Change">Change to give after a cash payment.</param>
public sealed record DisplayState(
    string Stage,
    string? OrderNumber,
    string? OrderType,
    IReadOnlyList<DisplayLine>? Lines,
    IReadOnlyList<DisplayTotal>? Totals,
    string? ToPay,
    string? Change)
{
    /// <summary>Most lines shown (a very long order shows its last ones).</summary>
    public const int MaxLines = 200;

    private static readonly string[] Stages = ["idle", "order", "pay", "thanks"];

    /// <summary>Nothing on the till.</summary>
    public static DisplayState Idle { get; } = new("idle", null, null, [], [], null, null);

    /// <summary>Checks and trims what a till sent; throws <see cref="ArgumentException"/> if it is not a display state.</summary>
    public DisplayState Checked()
    {
        if (!Stages.Contains(Stage))
        {
            throw new ArgumentException("Unknown display stage.");
        }

        static string? Cut(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim() is var t && t.Length > max ? t[..max] : t;
        var lines = (Lines ?? []).TakeLast(MaxLines).Select(l => new DisplayLine(
            Math.Clamp(l.Quantity, 0, 9999),
            Cut(l.Name, 120) ?? "Item",
            [.. (l.Details ?? []).Take(20).Select(d => Cut(d, 120)).OfType<string>()],
            Cut(l.Price, 20),
            l.Removed)).ToList();
        var totals = (Totals ?? []).Take(10).Select(t => new DisplayTotal(Cut(t.Label, 60) ?? string.Empty, Cut(t.Value, 20) ?? string.Empty, Cut(t.Kind, 10))).ToList();
        return new DisplayState(Stage, Cut(OrderNumber, 10), Cut(OrderType, 60), lines, totals, Cut(ToPay, 20), Cut(Change, 20));
    }
}

/// <summary>Everything a customer display needs: the shop, the owner's messages, and its till's order now.</summary>
/// <param name="ShopName">Shop name (from Shop details).</param>
/// <param name="Welcome">Welcome line.</param>
/// <param name="Messages">Messages shown in turn.</param>
/// <param name="TillName">The till it follows, or null if not linked yet.</param>
/// <param name="State">What that till shows now.</param>
public sealed record CustomerDisplayView(string ShopName, string Welcome, IReadOnlyList<string> Messages, string? TillName, DisplayState State);

/// <summary>A customer display as the owner's screen lists it.</summary>
/// <param name="Id">Display device id.</param>
/// <param name="Name">Its name.</param>
/// <param name="TillId">The till it follows, if any.</param>
public sealed record CustomerDisplayDevice(Guid Id, string Name, Guid? TillId);

/// <summary>The owner's screen: the settings, the displays and the tills they can follow.</summary>
/// <param name="Welcome">Welcome line.</param>
/// <param name="Messages">Messages.</param>
/// <param name="Displays">Registered customer displays.</param>
/// <param name="Tills">Tills a display can follow.</param>
public sealed record CustomerDisplaysSetup(string? Welcome, IReadOnlyList<string> Messages, IReadOnlyList<CustomerDisplayDevice> Displays, IReadOnlyList<DeviceRecord> Tills);

/// <summary>What the owner saves on More → Customer displays.</summary>
/// <param name="Welcome">Welcome line.</param>
/// <param name="Messages">Messages.</param>
/// <param name="Links">Which till each display follows.</param>
public sealed record SaveCustomerDisplaysRequest(string? Welcome, IReadOnlyList<string>? Messages, IReadOnlyList<CustomerDisplayLink>? Links);

/// <summary>
/// Customer displays: a second screen facing the customer (on the till PC, or a tablet on the counter) showing the order
/// as it is rung up, the total, what is left to pay and the change, then a welcome screen with the owner's messages.
/// The till sends what it shows; the hub passes it straight to the displays that follow that till (nothing is saved:
/// it is what is on the till right now). Displays can do nothing else: no staff log-in, no other orders, no customers.
/// </summary>
public sealed class CustomerDisplayService(HubDatabase db, AccessService access, ShopDetailsService shop, IHubContext<LiveHub> hub, TimeProvider clock, ILogger<CustomerDisplayService> log)
{
    /// <summary>Live message with a display's new state.</summary>
    public const string StateMessage = "display";

    /// <summary>Live message telling displays to fetch their settings again.</summary>
    public const string ReloadMessage = "display-reload";

    private const string Key = "customer-displays";

    // What each till shows now. Kept in memory only: it is the till's screen, not a record.
    private readonly ConcurrentDictionary<Guid, DisplayState> _states = new();

    /// <summary>The live group of one device (each device joins its own group).</summary>
    public static string DeviceGroup(Guid deviceId) => $"device:{deviceId:N}";

    /// <summary>The settings.</summary>
    public CustomerDisplaySettings Settings() => db.Run(() =>
        db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) is string json
            ? PosJson.Deserialize<CustomerDisplaySettings>(json)
            : CustomerDisplaySettings.Default);

    /// <summary>The owner's screen.</summary>
    public CustomerDisplaysSetup Setup()
    {
        var settings = Settings();
        var devices = access.ListDevices().Where(d => d.IsActive).ToList();
        var displays = devices.Where(d => d.Type == DeviceType.CustomerDisplay)
            .Select(d => new CustomerDisplayDevice(d.Id, d.Name, TillFor(settings, d.Id, devices)?.Id)).ToList();
        return new CustomerDisplaysSetup(settings.Welcome, settings.Messages, displays, [.. devices.Where(IsTill)]);
    }

    /// <summary>Saves the owner's settings and tells the displays to show them.</summary>
    public CustomerDisplaysSetup Save(SaveCustomerDisplaysRequest request, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(request);
        var devices = access.ListDevices().Where(d => d.IsActive).ToList();
        var links = request.Links ?? [];
        if (links.GroupBy(l => l.DisplayId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A display is listed twice.");
        }

        foreach (var link in links)
        {
            if (!devices.Any(d => d.Id == link.DisplayId && d.Type == DeviceType.CustomerDisplay))
            {
                throw new ArgumentException("That is not a customer display. Open the screen again.");
            }

            if (!devices.Any(d => d.Id == link.TillId && IsTill(d)))
            {
                throw new ArgumentException("A display must follow a till. Open the screen again.");
            }
        }

        static string? Text(string? v, string what) => string.IsNullOrWhiteSpace(v) ? null
            : v.Trim() is var t && t.Length <= CustomerDisplaySettings.MaxText ? t
            : throw new ArgumentException($"The {what} is too long (at most {CustomerDisplaySettings.MaxText} characters).");
        var messages = (request.Messages ?? []).Select(m => Text(m, "message")).OfType<string>().ToList();
        if (messages.Count > CustomerDisplaySettings.MaxMessages)
        {
            throw new ArgumentException($"At most {CustomerDisplaySettings.MaxMessages} messages.");
        }

        var settings = new CustomerDisplaySettings([.. links], Text(request.Welcome, "welcome line"), messages);
        db.Run(() => db.Execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
            ("$k", Key), ("$v", PosJson.Serialize(settings)), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString())));
        _ = SendAsync(devices.Where(d => d.Type == DeviceType.CustomerDisplay).Select(d => DeviceGroup(d.Id)).ToList(), ReloadMessage, null);
        return Setup();
    }

    /// <summary>What a display shows now.</summary>
    public CustomerDisplayView For(Guid displayId)
    {
        var settings = Settings();
        var till = TillFor(settings, displayId, access.ListDevices().Where(d => d.IsActive).ToList());
        var name = shop.Get().BusinessName;
        var state = till is not null && _states.TryGetValue(till.Id, out var now) ? now : DisplayState.Idle;
        return new CustomerDisplayView(name, settings.Welcome ?? $"Welcome to {name}", settings.Messages, till?.Name, state);
    }

    /// <summary>A till's screen changed: remember it and show it on the displays that follow that till.</summary>
    public void Show(Guid tillId, DisplayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var checkedState = state.Checked();
        _states[tillId] = checkedState;
        var settings = Settings();
        var devices = access.ListDevices().Where(d => d.IsActive).ToList();
        var groups = devices.Where(d => d.Type == DeviceType.CustomerDisplay && TillFor(settings, d.Id, devices)?.Id == tillId)
            .Select(d => DeviceGroup(d.Id)).ToList();
        if (groups.Count > 0)
        {
            _ = SendAsync(groups, StateMessage, checkedState);
        }
    }

    private static bool IsTill(DeviceRecord d) => d.Type is DeviceType.Till or DeviceType.BackOffice;

    /// <summary>The till a display follows: the one the owner chose, or the only till when the shop has just one.</summary>
    private static DeviceRecord? TillFor(CustomerDisplaySettings settings, Guid displayId, IReadOnlyList<DeviceRecord> devices)
    {
        if (settings.Links.FirstOrDefault(l => l.DisplayId == displayId) is { } link)
        {
            return devices.FirstOrDefault(d => d.Id == link.TillId && IsTill(d));
        }

        var tills = devices.Where(IsTill).ToList();
        return tills.Count == 1 ? tills[0] : null;
    }

    private async Task SendAsync(IReadOnlyList<string> groups, string message, DisplayState? state)
    {
        try
        {
            await hub.Clients.Groups(groups).SendAsync(message, state).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            // A display that misses a change catches up when it reconnects (it asks for the current state).
            HubLog.DisplaySendFailed(log, ex);
        }
    }
}
