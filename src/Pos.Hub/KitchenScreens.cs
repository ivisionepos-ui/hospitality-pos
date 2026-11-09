using System.Globalization;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Hub.Api;
using Pos.Hub.Data;
using Pos.Hub.Printing;
using Pos.Hub.Security;

namespace Pos.Hub;

/// <summary>What one kitchen screen shows: all food, or only the food of some kitchen sections (e.g. Pizza).</summary>
/// <param name="DeviceId">The kitchen screen.</param>
/// <param name="StationIds">The kitchen sections it shows (the kitchen printers, e.g. Pizza); empty = all food.</param>
public sealed record KitchenScreenChoice(Guid DeviceId, IReadOnlyList<Guid> StationIds);

/// <summary>A kitchen section a screen can show (the kitchen printers set up in More → Printers).</summary>
/// <param name="Id">Id (the kitchen printer's).</param>
/// <param name="Name">Name, e.g. "Pizza".</param>
public sealed record KitchenSectionView(Guid Id, string Name);

/// <summary>Kitchen screens and what each shows, for the owner's screen.</summary>
/// <param name="Screens">The choices saved.</param>
/// <param name="Sections">The kitchen sections there are.</param>
public sealed record KitchenScreensSetup(IReadOnlyList<KitchenScreenChoice> Screens, IReadOnlyList<KitchenSectionView> Sections);

/// <summary>What this kitchen screen shows.</summary>
/// <param name="AllFood">True when it shows every item.</param>
/// <param name="Sections">The sections it shows (empty for all food).</param>
public sealed record KitchenScreenView(bool AllFood, IReadOnlyList<KitchenSectionView> Sections);

/// <summary>An order as one kitchen screen sees it: only its items, and whether its part is done.</summary>
/// <param name="Order">The order, with only this screen's items.</param>
/// <param name="DoneHere">True when this screen's part is ready (the order waits for other sections).</param>
/// <param name="WaitingFor">Sections still making their part, once this screen's part is done.</param>
public sealed record KitchenOrderView(OrderView Order, bool DoneHere, IReadOnlyList<string> WaitingFor);

/// <summary>
/// Kitchen screens per section: a screen can show only its section's food (pizza on the pizza screen), chosen with the
/// same sections and rules as the kitchen printers, so screen and ticket always agree. Each section marks its own part
/// ready; the order becomes Ready when every section with a screen has finished its part.
/// </summary>
public sealed class KitchenScreenService(HubDatabase db, PrintSettings print, OrderService orders, AccessService access, TimeProvider clock)
{
    private const string Key = "kitchen-screens";

    private static readonly OrderStatus[] Cooking = [OrderStatus.Accepted, OrderStatus.Preparing];

    /// <summary>The saved choices and the sections there are.</summary>
    public KitchenScreensSetup Get() => new(Saved(), Sections());

    /// <summary>Saves what each kitchen screen shows.</summary>
    public KitchenScreensSetup Save(IReadOnlyList<KitchenScreenChoice> screens, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var kitchenScreens = access.ListDevices().Where(d => d.Type == DeviceType.KitchenScreen).Select(d => d.Id).ToHashSet();
        var sections = print.Kitchens.Select(k => k.Id).ToHashSet();
        if (screens.GroupBy(s => s.DeviceId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A kitchen screen is listed twice.");
        }

        if (screens.FirstOrDefault(s => !kitchenScreens.Contains(s.DeviceId)) is not null)
        {
            throw new ArgumentException("That screen is not a kitchen screen.");
        }

        if (screens.SelectMany(s => s.StationIds ?? []).FirstOrDefault(id => !sections.Contains(id)) is { } unknown && unknown != Guid.Empty)
        {
            throw new ArgumentException("A kitchen section no longer exists. Open the screen again.");
        }

        var tidy = screens.Select(s => new KitchenScreenChoice(s.DeviceId, [.. (s.StationIds ?? []).Distinct()])).ToList();
        db.Run(() => db.Execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
            ("$k", Key), ("$v", PosJson.Serialize(tidy)), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString())));
        return Get();
    }

    /// <summary>What a screen shows. Sections removed from the printers since are dropped; none left = all food.</summary>
    public KitchenScreenView For(Guid deviceId)
    {
        var ids = Saved().FirstOrDefault(s => s.DeviceId == deviceId)?.StationIds ?? [];
        var sections = Sections().Where(s => ids.Contains(s.Id)).ToList();
        return new KitchenScreenView(sections.Count == 0, sections);
    }

    /// <summary>Today's orders as this screen sees them (orders with nothing for it are left out).</summary>
    public IReadOnlyList<KitchenOrderView> Orders(Guid deviceId)
    {
        var screen = For(deviceId);
        var marks = Marks(null);
        return [.. orders.ForDay(null, null).Select(o => View(o, screen, marks)).OfType<KitchenOrderView>()];
    }

    /// <summary>One order as this screen sees it; null when it has nothing for this screen.</summary>
    public KitchenOrderView? Order(Guid deviceId, Guid orderId) => View(orders.Get(orderId), For(deviceId), Marks(orderId));

    /// <summary>
    /// This screen's part of the order is ready. On a screen showing all food, the whole order is ready. Otherwise the
    /// order becomes ready once every section that has a screen and items on this order has finished its part.
    /// </summary>
    public KitchenOrderView? PartReady(Guid deviceId, Guid orderId, OrderActor actor)
    {
        var screen = For(deviceId);
        if (screen.AllFood)
        {
            orders.Change(orderId, o =>
            {
                o.MarkReady(actor);
                return 0;
            });
            return Order(deviceId, orderId);
        }

        orders.Change(orderId, order =>
        {
            if (!Cooking.Contains(order.Status))
            {
                throw new InvalidOperationException($"Order {order.Number.Display} is not being made now.");
            }

            var now = clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            foreach (var section in screen.Sections)
            {
                db.Execute("INSERT OR REPLACE INTO kitchen_part_ready (order_id, station_id, at) VALUES ($o, $s, $at);",
                    ("$o", orderId.ToString()), ("$s", section.Id.ToString()), ("$at", now));
            }

            order.NoteKitchenPart(string.Join(" + ", screen.Sections.Select(s => s.Name)), ready: true, actor);
            if (StillMaking(order, Marks(orderId)).Count == 0)
            {
                order.MarkReady(actor);
            }

            return 0;
        });
        return Order(deviceId, orderId);
    }

    /// <summary>Puts this screen's part back on its screen (marked ready by mistake). A ready order goes back to preparing.</summary>
    public KitchenOrderView? Recall(Guid deviceId, Guid orderId, OrderActor actor)
    {
        var screen = For(deviceId);
        orders.Change(orderId, order =>
        {
            foreach (var section in screen.Sections)
            {
                db.Execute("DELETE FROM kitchen_part_ready WHERE order_id = $o AND station_id = $s;", ("$o", orderId.ToString()), ("$s", section.Id.ToString()));
            }

            if (order.Status == OrderStatus.Ready)
            {
                order.StartPreparing(actor);
            }

            if (!screen.AllFood)
            {
                order.NoteKitchenPart(string.Join(" + ", screen.Sections.Select(s => s.Name)), ready: false, actor);
            }

            return 0;
        });
        return Order(deviceId, orderId);
    }

    /// <summary>
    /// More items went to the kitchen: the sections that got them have work again, and an order already marked ready
    /// goes back to preparing so the new items show on the kitchen screens. Call in the same transaction.
    /// </summary>
    public void ItemsSent(Order order, IReadOnlyList<OrderLine> lines, OrderActor actor)
    {
        ArgumentNullException.ThrowIfNull(order);
        foreach (var (station, _) in KitchenRouting.Route(lines, print.Kitchens))
        {
            db.Execute("DELETE FROM kitchen_part_ready WHERE order_id = $o AND station_id = $s;", ("$o", order.Id.ToString()), ("$s", station.Id.ToString()));
        }

        if (order.Status == OrderStatus.Ready && lines.Any(l => !l.IsVoided))
        {
            order.StartPreparing(actor);
        }
    }

    // Sections with a screen that have items on this order and have not finished their part.
    private List<KitchenStation> StillMaking(Order order, Dictionary<(Guid Order, Guid Station), DateTimeOffset> marks)
    {
        var shown = Saved().SelectMany(s => s.StationIds).ToHashSet();
        var sent = order.Lines.Where(l => l.SentToKitchen && !l.IsVoided).ToList();
        return [.. KitchenRouting.Route(sent, print.Kitchens)
            .Select(r => r.Station)
            .Where(s => shown.Contains(s.Id) && !marks.ContainsKey((order.Id, s.Id)))];
    }

    private KitchenOrderView? View(Order order, KitchenScreenView screen, Dictionary<(Guid Order, Guid Station), DateTimeOffset> marks)
    {
        var full = OrderView.From(order);
        if (screen.AllFood)
        {
            return new KitchenOrderView(full, false, []);
        }

        // The same routing as the kitchen printers, over every item sent (removed ones too, so the cook sees REMOVED).
        var sent = order.Lines.Where(l => l.SentToKitchen).ToList();
        var mine = new Dictionary<Guid, OrderLine>();
        foreach (var (station, lines) in KitchenRouting.Route(sent, print.Kitchens))
        {
            if (screen.Sections.All(s => s.Id != station.Id))
            {
                continue;
            }

            foreach (var line in lines)
            {
                // A meal deal split between sections: keep the fullest version this screen gets.
                if (!mine.TryGetValue(line.Id, out var had) || line.Display.DealItems.Count > had.Display.DealItems.Count)
                {
                    mine[line.Id] = line;
                }
            }
        }

        if (mine.Count == 0)
        {
            return null;
        }

        var lineViews = full.Lines.Where(l => mine.ContainsKey(l.Id)).Select(l =>
        {
            var routed = mine[l.Id];
            return l with { Name = routed.Display.Name, DealItems = routed.Display.DealItems };
        }).ToList();
        var done = screen.Sections.All(s => marks.ContainsKey((order.Id, s.Id)) || !KitchenRouting.Route(sent, print.Kitchens).Any(r => r.Station.Id == s.Id));
        var waiting = done ? StillMaking(order, marks).Select(s => s.Name).ToList() : [];
        return new KitchenOrderView(full with { Lines = lineViews }, done, waiting);
    }

    private Dictionary<(Guid Order, Guid Station), DateTimeOffset> Marks(Guid? orderId) => db.Run(() =>
    {
        using var command = db.Command(
            "SELECT order_id, station_id, at FROM kitchen_part_ready WHERE ($o IS NULL OR order_id = $o);", ("$o", orderId?.ToString()));
        using var reader = command.ExecuteReader();
        var marks = new Dictionary<(Guid, Guid), DateTimeOffset>();
        while (reader.Read())
        {
            marks[(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)))] = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
        }

        return marks;
    });

    private List<KitchenScreenChoice> Saved()
    {
        var saved = db.Run(() => db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) as string);
        return saved is null ? [] : PosJson.Deserialize<List<KitchenScreenChoice>>(saved);
    }

    private List<KitchenSectionView> Sections() => [.. print.Kitchens.Select(k => new KitchenSectionView(k.Id, k.Name))];
}
