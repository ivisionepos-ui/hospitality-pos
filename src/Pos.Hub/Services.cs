using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Hub.Api;
using Pos.Hub.Data;
using Pos.Hub.Live;

namespace Pos.Hub;

/// <summary>Keeps the current menu in memory and saves every change to the database straight away.</summary>
public sealed class MenuService
{
    private readonly HubDatabase _db;
    private readonly SqliteMenuStore _store;
    private readonly TimeProvider _clock;
    private readonly LiveUpdates _live;
    private Menu _menu;

    /// <summary>Loads the saved menu, or starts with an empty one.</summary>
    public MenuService(HubDatabase db, TimeProvider clock, LiveUpdates live)
    {
        _db = db;
        _clock = clock;
        _live = live;
        _store = new SqliteMenuStore(db);
        _menu = LoadSaved();
    }

    /// <summary>Reads from the current menu.</summary>
    public T Read<T>(Func<Menu, T> read) => _db.Run(() => read(_menu));

    /// <summary>Makes one change and saves it together with its log entry.</summary>
    public MenuChange Edit(Func<Menu, MenuChange> edit) => _db.RunInTransaction(() =>
    {
        var logged = _menu.ChangeLog.Count;
        var change = edit(_menu);
        try
        {
            _store.Save(_menu, _menu.ChangeLog.Skip(logged), _clock.GetUtcNow());
            _live.MenuChanged(_menu.Version, change.Summary);
        }
        catch
        {
            _menu = LoadSaved(); // the database did not take the change, so drop it from memory too
            throw;
        }

        return change;
    });

    /// <summary>Replaces the whole menu (set-up or import), checking everything.</summary>
    public MenuState Import(MenuState state, Guid userId) => _db.RunInTransaction(() =>
    {
        var imported = Menu.Import(state, userId, _clock, continueFromVersion: _menu.Version);
        _store.Save(imported, imported.ChangeLog, _clock.GetUtcNow());
        _live.MenuChanged(imported.Version, "Menu imported");
        _menu = imported;
        return imported.ToState();
    });

    /// <summary>Logged changes after a version.</summary>
    public IReadOnlyList<MenuChangeRecord> ChangesSince(long version) => _db.Run(() => _store.ChangesSince(version));

    private Menu LoadSaved() =>
        _store.Load() is { } saved ? Menu.FromState(saved, _clock) : new Menu(clock: _clock);
}

/// <summary>Takes orders from tills and screens, and saves every change to the database straight away.</summary>
public sealed class OrderService
{
    private readonly HubDatabase _db;
    private readonly HubSettings _settings;
    private readonly MenuService _menu;
    private readonly TimeProvider _clock;
    private readonly SqliteOrderStore _store;
    private readonly OrderIntake _intake;
    private readonly LiveUpdates _live;

    /// <summary>Creates the service.</summary>
    public OrderService(HubDatabase db, HubSettings settings, MenuService menu, TimeProvider clock, LiveUpdates live)
    {
        _live = live;
        ArgumentNullException.ThrowIfNull(settings);
        _db = db;
        _settings = settings;
        _menu = menu;
        _clock = clock;

        // Scheduled offers are not stored on the hub yet; they arrive with the back office in Phase 5.
        _store = new SqliteOrderStore(db, () => settings.PricingSetup([]), clock);
        _intake = new OrderIntake(_store, new SqliteOrderNumberSequence(db), settings.PricingSetup([]), settings.DayStartsAt, clock);
    }

    /// <summary>Creates an order from what was picked on the till. Safe to send again: a repeat returns the same order.</summary>
    public SubmitResult Submit(CreateOrderRequest request, OrderActor actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _db.RunInTransaction(() =>
        {
            var lines = _menu.Read(menu => BuildLines(menu, request.Lines ?? []));
            var result = _intake.Submit(new NewOrder
            {
                OrderId = request.OrderId,
                IdempotencyKey = request.IdempotencyKey,
                BranchId = _settings.BranchId,
                BranchCode = _settings.BranchCode,
                Source = request.Source,
                Type = request.Type,
                CreatedBy = actor,
                CustomerId = request.CustomerId,
                TableId = request.TableId,
                Lines = lines,
            });
            if (!result.WasDuplicate)
            {
                _live.OrderChanged(result.Order, created: true);
            }

            return result;
        });
    }

    /// <summary>Prices an order still being built on the till, without saving it or using an order number.</summary>
    public Order Quote(CreateOrderRequest request, OrderActor actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lines = _menu.Read(menu => BuildLines(menu, request.Lines ?? []));
        return _intake.Quote(new NewOrder
        {
            OrderId = request.OrderId,
            IdempotencyKey = request.IdempotencyKey,
            BranchId = _settings.BranchId,
            BranchCode = _settings.BranchCode,
            Source = request.Source,
            Type = request.Type,
            CreatedBy = actor,
            CustomerId = request.CustomerId,
            TableId = request.TableId,
            Lines = lines,
        });
    }

    /// <summary>Finds an order.</summary>
    public Order Get(Guid orderId) =>
        _db.Run(() => _store.FindById(orderId)) ?? throw new KeyNotFoundException("Order not found.");

    /// <summary>Orders for a trading day (default: today's).</summary>
    public IReadOnlyList<Order> ForDay(DateOnly? businessDate, OrderStatus? status)
    {
        var date = businessDate ?? BusinessDay.For(_clock.GetUtcNow(), _settings.TimeZone, _settings.DayStartsAt);
        return _db.Run(() => _store.ForDay(_settings.BranchId, date, status));
    }

    /// <summary>Changes an order and saves it; nothing is saved if the change is refused.</summary>
    public T Change<T>(Guid orderId, Func<Order, T> change) => _db.RunInTransaction(() =>
    {
        var order = _store.FindById(orderId) ?? throw new KeyNotFoundException("Order not found.");
        var result = change(order);
        _store.Save(order);
        _live.OrderChanged(order, created: false);
        return result;
    });

    /// <summary>
    /// Adds items to an open order (e.g. more drinks for a table). With a request id, sending the same request
    /// again (after a dropped connection) adds nothing and returns the order as it is.
    /// </summary>
    public (Order Order, bool Duplicate) AddLines(Guid orderId, IReadOnlyList<OrderLineRequest> lines, OrderActor actor, Guid? requestId = null) =>
        _db.RunInTransaction(() =>
        {
            if (requestId is { } id)
            {
                var seen = _db.Scalar("SELECT order_id FROM order_line_requests WHERE request_id = $r;", ("$r", id.ToString()));
                if (seen is string forOrder)
                {
                    return forOrder == orderId.ToString()
                        ? (Get(orderId), true)
                        : throw new IdempotencyConflictException("This request id was already used for another order.");
                }

                _db.Execute("INSERT INTO order_line_requests (request_id, order_id, at) VALUES ($r, $o, $at);",
                    ("$r", id.ToString()), ("$o", orderId.ToString()), ("$at", _clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            }

            var order = Change(orderId, order =>
            {
                foreach (var line in _menu.Read(menu => BuildLines(menu, lines)))
                {
                    order.AddLine(line.Pricing, line.Display, actor, line.Note);
                }

                return order;
            });
            return (order, false);
        });

    private static List<NewOrderLine> BuildLines(Menu menu, IReadOnlyList<OrderLineRequest> requests)
    {
        var builder = new MenuOrderBuilder(menu);
        var problems = new List<SelectionProblem>();
        var lines = new List<NewOrderLine>();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (request.Choices is { } choices)
            {
                var selection = new DealSelection(request.ProductId, request.VariantId,
                    [.. choices.Select(c => new SlotChoice(c.SlotId, c.ProductId, c.VariantId, c.Extras ?? []))], request.Quantity);
                var found = builder.Check(selection);
                if (found.Count > 0)
                {
                    problems.AddRange(found.Select(p => p with { Message = $"Line {i + 1}: {p.Message}" }));
                    continue;
                }

                lines.Add(new NewOrderLine(builder.ToLine(selection), builder.DisplayFor(selection), request.Note));
            }
            else
            {
                var selection = new ItemSelection(request.ProductId, request.VariantId, request.Extras ?? [], request.Quantity);
                var found = builder.Check(selection);
                if (found.Count > 0)
                {
                    problems.AddRange(found.Select(p => p with { Message = $"Line {i + 1}: {p.Message}" }));
                    continue;
                }

                lines.Add(new NewOrderLine(builder.ToLine(selection), builder.DisplayFor(selection), request.Note));
            }
        }

        return problems.Count > 0 ? throw new InvalidSelectionException(problems) : lines;
    }
}
