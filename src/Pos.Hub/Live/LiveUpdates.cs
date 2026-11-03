using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Hub.Api;
using Pos.Hub.Data;
using Pos.Hub.Security;

namespace Pos.Hub.Live;

/// <summary>Kinds of live event.</summary>
public static class LiveEventKinds
{
    /// <summary>A new order arrived.</summary>
    public const string OrderCreated = "OrderCreated";

    /// <summary>An order changed: status, items added or removed, sent to the kitchen, charges.</summary>
    public const string OrderUpdated = "OrderUpdated";

    /// <summary>The menu changed (price, name, sold out, import…). Screens reload the menu.</summary>
    public const string MenuChanged = "MenuChanged";

    /// <summary>A payment or refund was taken on an order (tills only).</summary>
    public const string PaymentChanged = "PaymentChanged";

    /// <summary>A till shift was opened, changed or closed (tills only).</summary>
    public const string ShiftChanged = "ShiftChanged";

    /// <summary>Something could not be printed after several tries (tills only).</summary>
    public const string PrintFailed = "PrintFailed";

    /// <summary>A backup, or its second copy, could not be made (tills only).</summary>
    public const string BackupFailed = "BackupFailed";
}

/// <summary>
/// One live event sent to screens. Every event has a number one higher than the last;
/// a screen that sees a gap (or has been offline) asks for the events it missed.
/// </summary>
/// <param name="Seq">Event number.</param>
/// <param name="At">When it happened (UTC).</param>
/// <param name="Kind">See <see cref="LiveEventKinds"/>.</param>
/// <param name="Order">The order as it is now, for order events.</param>
/// <param name="MenuVersion">New menu version, for menu events.</param>
/// <param name="Summary">Short description.</param>
public sealed record LiveEvent(long Seq, DateTimeOffset At, string Kind, OrderView? Order, long? MenuVersion, string? Summary);

/// <summary>Events missed while a screen was away.</summary>
/// <param name="Events">Events after the number asked for, oldest first.</param>
/// <param name="LastSeq">Number of the latest event the hub has.</param>
/// <param name="More">True when there are more to fetch (ask again from the last one received).</param>
public sealed record CatchUpResult(IReadOnlyList<LiveEvent> Events, long LastSeq, bool More);

/// <summary>A short-lived pass to open the live connection (browsers cannot send device keys on a WebSocket).</summary>
/// <param name="Ticket">Pass for the access_token parameter.</param>
/// <param name="ExpiresAt">Must connect before this.</param>
/// <param name="LastSeq">Latest event number now: catch up from your own last number to this.</param>
public sealed record LiveTicket(string Ticket, DateTimeOffset ExpiresAt, long LastSeq);

/// <summary>
/// Records live events in the database together with the change they describe, and sends them to
/// connected screens once that change is saved. Groups: "staff" (tills, back office) get everything;
/// "kitchen" gets accepted orders and menu changes; "display" gets menu changes.
/// </summary>
public sealed class LiveUpdates(HubDatabase db, IHubContext<LiveHub> hub, TimeProvider clock, ILogger<LiveUpdates> log)
{
    /// <summary>Largest number of events returned in one catch-up.</summary>
    public const int MaxCatchUp = 500;

    /// <summary>How long a live ticket lasts.</summary>
    public static readonly TimeSpan TicketLife = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (DeviceRecord Device, DateTimeOffset Expires)> _tickets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Guid DeviceId, HubCallerContext Context)> _connections = new(StringComparer.Ordinal);

    /// <summary>Group for a kind of device.</summary>
    public static string GroupFor(DeviceType type) => type switch
    {
        DeviceType.KitchenScreen => "kitchen",
        DeviceType.CustomerDisplay => "display",
        _ => "staff",
    };

    /// <summary>Records that an order was created or changed. Call inside the transaction that saves the order.</summary>
    public void OrderChanged(Order order, bool created)
    {
        ArgumentNullException.ThrowIfNull(order);
        var forKitchen = order.Status is not (OrderStatus.Pending or OrderStatus.Received);
        Record(created ? LiveEventKinds.OrderCreated : LiveEventKinds.OrderUpdated, OrderView.From(order), null, $"{order.Number.Display} {order.Status}", forKitchen, forDisplay: false);
    }

    /// <summary>Records a payment or refund on an order (tills and back office only).</summary>
    public void PaymentChanged(OrderView order, string summary) =>
        Record(LiveEventKinds.PaymentChanged, order, null, summary, forKitchen: false, forDisplay: false);

    /// <summary>Records a shift change (tills and back office only).</summary>
    public void ShiftChanged(string summary) =>
        Record(LiveEventKinds.ShiftChanged, null, null, summary, forKitchen: false, forDisplay: false);

    /// <summary>Tells the tills that a printout failed.</summary>
    public void PrintFailed(string summary) =>
        Record(LiveEventKinds.PrintFailed, null, null, summary, forKitchen: false, forDisplay: false);

    /// <summary>Tells the tills that a backup failed.</summary>
    public void BackupFailed(string summary) =>
        Record(LiveEventKinds.BackupFailed, null, null, summary, forKitchen: false, forDisplay: false);

    /// <summary>Records a menu change. Call inside the transaction that saves the menu.</summary>
    public void MenuChanged(long version, string summary) =>
        Record(LiveEventKinds.MenuChanged, null, version, summary, forKitchen: true, forDisplay: true);

    /// <summary>Number of the latest event.</summary>
    public long LastSeq() => db.Run(() =>
        Convert.ToInt64(db.Scalar("SELECT COALESCE(MAX(seq), 0) FROM live_events;"), CultureInfo.InvariantCulture));

    /// <summary>Events after <paramref name="since"/> that a device of this type should see.</summary>
    public CatchUpResult Since(long since, DeviceType type) => db.Run(() =>
    {
        var filter = GroupFor(type) switch
        {
            "kitchen" => "AND for_kitchen = 1",
            "display" => "AND for_display = 1",
            _ => string.Empty,
        };
        using var command = db.Command($"SELECT payload_json FROM live_events WHERE seq > $s {filter} ORDER BY seq LIMIT $n;", ("$s", since), ("$n", MaxCatchUp + 1));
        using var reader = command.ExecuteReader();
        var events = new List<LiveEvent>();
        while (reader.Read())
        {
            events.Add(PosJson.Deserialize<LiveEvent>(reader.GetString(0)));
        }

        var more = events.Count > MaxCatchUp;
        return new CatchUpResult([.. events.Take(MaxCatchUp)], LastSeq(), more);
    });

    /// <summary>Gives a registered device a 60-second pass to open its live connection.</summary>
    public LiveTicket IssueTicket(DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var now = clock.GetUtcNow();
        foreach (var expired in _tickets.Where(t => t.Value.Expires < now).Select(t => t.Key).ToList())
        {
            _tickets.TryRemove(expired, out _);
        }

        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = now + TicketLife;
        _tickets[Hash(ticket)] = (device, expires);
        return new LiveTicket(ticket, expires, LastSeq());
    }

    /// <summary>The device for a valid, unexpired ticket, or null.</summary>
    public DeviceRecord? RedeemTicket(string? ticket) =>
        !string.IsNullOrEmpty(ticket) && _tickets.TryGetValue(Hash(ticket), out var entry) && entry.Expires >= clock.GetUtcNow()
            ? entry.Device
            : null;

    /// <summary>Remembers a connection so it can be closed if its device is removed.</summary>
    public void Connected(Guid deviceId, HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _connections[context.ConnectionId] = (deviceId, context);
    }

    /// <summary>Forgets a connection.</summary>
    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Closes every live connection of a device (used when a device is removed) and cancels its tickets.</summary>
    public void DisconnectDevice(Guid deviceId)
    {
        foreach (var (key, value) in _tickets.Where(t => t.Value.Device.Id == deviceId).ToList())
        {
            _tickets.TryRemove(key, out _);
        }

        foreach (var (id, entry) in _connections.Where(c => c.Value.DeviceId == deviceId).ToList())
        {
            entry.Context.Abort();
            _connections.TryRemove(id, out _);
        }
    }

    private void Record(string kind, OrderView? order, long? menuVersion, string summary, bool forKitchen, bool forDisplay)
    {
        var at = clock.GetUtcNow();
        var seq = Convert.ToInt64(
            db.Scalar(
                "INSERT INTO live_events (at, kind, for_kitchen, for_display, payload_json) VALUES ($at, $k, $fk, $fd, '') RETURNING seq;",
                ("$at", at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$k", kind), ("$fk", forKitchen ? 1 : 0), ("$fd", forDisplay ? 1 : 0)),
            CultureInfo.InvariantCulture);
        var live = new LiveEvent(seq, at, kind, order, menuVersion, summary);
        db.Execute("UPDATE live_events SET payload_json = $p WHERE seq = $s;", ("$p", PosJson.Serialize(live)), ("$s", seq));

        var groups = new List<string> { "staff" };
        if (forKitchen)
        {
            groups.Add("kitchen");
        }

        if (forDisplay)
        {
            groups.Add("display");
        }

        db.AfterCommit(() => _ = SendAsync(groups, live));
    }

    private async Task SendAsync(IReadOnlyList<string> groups, LiveEvent live)
    {
        try
        {
            await hub.Clients.Groups(groups).SendAsync("event", live).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            // A screen that misses an event fetches it with catch-up; the event is safely saved.
            HubLog.LiveSendFailed(log, live.Seq, ex);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// The live connection for tills and screens (SignalR over WebSockets, at /live).
/// Screens only listen: the hub sends "event" messages. Open it with a ticket from POST /api/live/ticket.
/// </summary>
public sealed class LiveHub(LiveUpdates live) : Microsoft.AspNetCore.SignalR.Hub
{
    /// <summary>Key used to pass the checked device from the ticket check to the hub.</summary>
    public const string DeviceItem = "pos.live.device";

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext()?.Items[DeviceItem] is not DeviceRecord device)
        {
            Context.Abort();
            return;
        }

        live.Connected(device.Id, Context);
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveUpdates.GroupFor(device.Type)).ConfigureAwait(false);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        live.Disconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
