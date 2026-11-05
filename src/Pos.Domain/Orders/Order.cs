using Pos.Domain.Delivery;
using Pos.Domain.Menus;
using Pos.Domain.Pricing;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;

namespace Pos.Domain.Orders;

/// <summary>
/// What is printed and shown for a line, copied from the menu when the item was added,
/// so later menu changes never alter an order or its receipt.
/// </summary>
/// <param name="Name">Item name at the time of sale.</param>
/// <param name="Size">Size name, or null for single-size items.</param>
/// <param name="Extras">Names of extras chosen.</param>
/// <param name="CourseSortOrder">Food-type group position (Starters first, Drinks last); receipts sort by it.</param>
/// <param name="KitchenStationId">Station the item prints to, or null.</param>
/// <param name="DealItems">For a meal deal: what was chosen in it.</param>
public sealed record LineDisplay(
    LocalizedText Name,
    LocalizedText? Size,
    IReadOnlyList<LocalizedText> Extras,
    int CourseSortOrder,
    Guid? KitchenStationId,
    IReadOnlyList<LineDisplay> DealItems);

/// <summary>A line on an order.</summary>
public sealed record OrderLine
{
    /// <summary>Line id (made on the device).</summary>
    public required Guid Id { get; init; }

    /// <summary>The priced line, with the price at the time it was added.</summary>
    public required OrderLineInput Pricing { get; init; }

    /// <summary>Names and print details at the time it was added.</summary>
    public required LineDisplay Display { get; init; }

    /// <summary>Special instructions, e.g. "no onions".</summary>
    public string? Note { get; init; }

    /// <summary>Position in the order of entry (1, 2, 3…).</summary>
    public int EntryNumber { get; init; }

    /// <summary>Already sent to the kitchen.</summary>
    public bool SentToKitchen { get; init; }

    /// <summary>Removed from the order (kept for the audit trail).</summary>
    public bool IsVoided { get; init; }

    /// <summary>Why it was removed.</summary>
    public string? VoidReason { get; init; }
}

/// <summary>What happened to an order.</summary>
public enum OrderEventKind
{
    /// <summary>The order was created.</summary>
    Created,

    /// <summary>The status changed.</summary>
    StatusChanged,

    /// <summary>A line was added.</summary>
    LineAdded,

    /// <summary>A line was removed.</summary>
    LineVoided,

    /// <summary>Lines were sent to the kitchen.</summary>
    SentToKitchen,

    /// <summary>An order discount was applied or removed.</summary>
    DiscountChanged,

    /// <summary>The delivery details or fee changed.</summary>
    DeliveryChanged,

    /// <summary>A service charge was added or removed.</summary>
    ServiceChargeChanged,

    /// <summary>The customer's name, phone, address or notes changed.</summary>
    ContactChanged,
}

/// <summary>One entry in an order's history. Nothing is ever deleted from it.</summary>
/// <param name="At">When (UTC).</param>
/// <param name="UserId">Who.</param>
/// <param name="DeviceId">Which till or screen.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Description">Plain-English description.</param>
/// <param name="FromStatus">Status before, for status changes.</param>
/// <param name="ToStatus">Status after, for status changes.</param>
/// <param name="LineId">The line involved, if any.</param>
/// <param name="Reason">Reason given, if any.</param>
/// <param name="ApprovedByUserId">Manager who approved, if needed.</param>
public sealed record OrderEvent(
    DateTimeOffset At,
    Guid UserId,
    Guid? DeviceId,
    OrderEventKind Kind,
    string Description,
    OrderStatus? FromStatus = null,
    OrderStatus? ToStatus = null,
    Guid? LineId = null,
    string? Reason = null,
    Guid? ApprovedByUserId = null);

/// <summary>Who is acting on the order, and from which device.</summary>
/// <param name="UserId">Staff member.</param>
/// <param name="DeviceId">Till or screen.</param>
public sealed record OrderActor(Guid UserId, Guid? DeviceId = null);

/// <summary>The branch's pricing settings, captured when the order is created.</summary>
/// <param name="VatPolicy">VAT rules.</param>
/// <param name="Promotions">Offers running at the branch.</param>
/// <param name="TimeZone">Branch time zone.</param>
/// <param name="DiscountPolicy">Staff discount limits.</param>
public sealed record OrderPricingSetup(IVatPolicy VatPolicy, IReadOnlyList<Promotion> Promotions, TimeZoneInfo TimeZone, DiscountPolicy DiscountPolicy);

/// <summary>
/// An order from any source: counter, table, phone, website or delivery app.
/// Every change is checked against the status rules, recorded in the history, and the totals are recalculated.
/// </summary>
public sealed class Order
{
    private readonly List<OrderLine> _lines = [];
    private readonly List<OrderEvent> _history = [];
    private readonly OrderPricingSetup _pricing;
    private readonly TimeProvider _clock;

    internal Order(
        Guid id,
        string idempotencyKey,
        string fingerprint,
        Guid branchId,
        OrderNumber number,
        OrderSource source,
        OrderType type,
        OrderStatus status,
        OrderPricingSetup pricing,
        TimeProvider clock,
        OrderActor createdBy,
        Guid? customerId,
        Guid? tableId)
    {
        Id = id;
        IdempotencyKey = idempotencyKey;
        Fingerprint = fingerprint;
        BranchId = branchId;
        Number = number;
        Source = source;
        Type = type;
        Status = status;
        _pricing = pricing;
        _clock = clock;
        CreatedAt = clock.GetUtcNow();
        CustomerId = customerId;
        TableId = tableId;
        Totals = Recalculate(_lines, null, null, null);
        Record(createdBy, OrderEventKind.Created, $"Order {number.Display} created ({source}, {type})", toStatus: status);
    }

    private Order(OrderState state, OrderPricingSetup pricing, TimeProvider clock)
    {
        Id = state.Id;
        IdempotencyKey = state.IdempotencyKey;
        Fingerprint = state.Fingerprint;
        BranchId = state.BranchId;
        Number = state.Number;
        Source = state.Source;
        Type = state.Type;
        Status = state.Status;
        CreatedAt = state.CreatedAt;
        CustomerId = state.CustomerId;
        TableId = state.TableId;
        _pricing = pricing;
        _clock = clock;
        _lines.AddRange(state.Lines);
        _history.AddRange(state.History);
        Discount = state.Discount;
        Delivery = state.Delivery is { } d
            ? new DeliveryInput(d.DistanceMiles, DeliveryChargeTable.FromState(d.Table), d.Override)
            : null;
        ServiceCharge = state.ServiceCharge;
        Contact = state.Contact;
        Totals = Recalculate(_lines, Discount, Delivery, ServiceCharge);
    }

    /// <summary>
    /// Loads an order saved with <see cref="ToState"/>. Totals are worked out again from the saved lines,
    /// which keep their original prices, so they come out the same.
    /// </summary>
    public static Order Restore(OrderState state, OrderPricingSetup pricing, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pricing);
        return new Order(state, pricing, clock ?? TimeProvider.System);
    }

    /// <summary>The order as plain data, for saving.</summary>
    public OrderState ToState() => new()
    {
        Id = Id,
        IdempotencyKey = IdempotencyKey,
        Fingerprint = Fingerprint,
        BranchId = BranchId,
        Number = Number,
        Source = Source,
        Type = Type,
        Status = Status,
        CreatedAt = CreatedAt,
        CustomerId = CustomerId,
        TableId = TableId,
        Lines = [.. _lines],
        History = [.. _history],
        Discount = Discount,
        Delivery = Delivery is { } d ? new DeliveryState(d.DistanceMiles, d.Table.ToState(), d.Override) : null,
        ServiceCharge = ServiceCharge,
        Contact = Contact,
    };

    /// <summary>Order id (made on the device that created it).</summary>
    public Guid Id { get; }

    /// <summary>Key sent by the device so a repeated submission is recognised.</summary>
    public string IdempotencyKey { get; }

    /// <summary>Summary of the order's content when submitted, used to spot a reused key.</summary>
    public string Fingerprint { get; }

    /// <summary>Branch.</summary>
    public Guid BranchId { get; }

    /// <summary>Order number.</summary>
    public OrderNumber Number { get; }

    /// <summary>Where it came from.</summary>
    public OrderSource Source { get; }

    /// <summary>Eat-in, takeaway or delivery.</summary>
    public OrderType Type { get; }

    /// <summary>When it was created; offers use this time.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Customer, if known.</summary>
    public Guid? CustomerId { get; }

    /// <summary>Table, for table service.</summary>
    public Guid? TableId { get; }

    /// <summary>Current status.</summary>
    public OrderStatus Status { get; private set; }

    /// <summary>All lines, including removed ones.</summary>
    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>Lines still on the order.</summary>
    public IReadOnlyList<OrderLine> ActiveLines => [.. _lines.Where(l => !l.IsVoided)];

    /// <summary>
    /// Active lines in receipt order: by food-type group (Starters first, Drinks last), then in the order entered.
    /// </summary>
    public IReadOnlyList<OrderLine> LinesForReceipt =>
        [.. _lines.Where(l => !l.IsVoided).OrderBy(l => l.Display.CourseSortOrder).ThenBy(l => l.EntryNumber)];

    /// <summary>Everything that has happened to the order, oldest first.</summary>
    public IReadOnlyList<OrderEvent> History => _history;

    /// <summary>Order discount, or null.</summary>
    public OrderDiscountInput? Discount { get; private set; }

    /// <summary>Delivery details, or null.</summary>
    public DeliveryInput? Delivery { get; private set; }

    /// <summary>Customer name, phone, delivery address and notes, or null.</summary>
    public OrderContact? Contact { get; private set; }

    /// <summary>Service charge, or null.</summary>
    public ServiceChargeInput? ServiceCharge { get; private set; }

    /// <summary>Current totals, VAT and charges.</summary>
    public PricedOrder Totals { get; private set; }

    /// <summary>True when nothing more can happen to the order.</summary>
    public bool IsFinished => OrderStatusRules.IsFinished(Status);

    // ---------- Lines ----------

    /// <summary>Adds a line. Not allowed once the order is finished.</summary>
    public OrderLine AddLine(OrderLineInput pricing, LineDisplay display, OrderActor actor, string? note = null, Guid? lineId = null)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(display);
        EnsureOpen();

        var line = new OrderLine
        {
            Id = lineId ?? DeviceIds.New(_clock),
            Pricing = pricing,
            Display = display,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            EntryNumber = _lines.Count + 1,
        };

        _lines.Add(line);
        Totals = Recalculate(_lines, Discount, Delivery, ServiceCharge);
        Record(actor, OrderEventKind.LineAdded, $"Added {pricing.Quantity} x {display.Name}", lineId: line.Id);
        return line;
    }

    /// <summary>
    /// Removes a line. A reason is always required; once the line has gone to the kitchen
    /// a manager's approval is also required, and the kitchen will be told.
    /// </summary>
    public void VoidLine(Guid lineId, string reason, OrderActor actor, Guid? approvedByUserId = null)
    {
        EnsureOpen();
        RequireReason(reason);
        var index = _lines.FindIndex(l => l.Id == lineId);
        if (index < 0)
        {
            throw new KeyNotFoundException("Line not found on this order.");
        }

        var line = _lines[index];
        if (line.IsVoided)
        {
            throw new InvalidOperationException("This line has already been removed.");
        }

        if (line.SentToKitchen && approvedByUserId is null)
        {
            throw new InvalidOperationException("This item has gone to the kitchen; a manager must approve removing it.");
        }

        var updated = _lines.Select((l, i) => i == index ? l with { IsVoided = true, VoidReason = reason.Trim() } : l).ToList();
        var totals = Recalculate(updated, Discount, Delivery, ServiceCharge);
        _lines[index] = updated[index];
        Totals = totals;
        Record(actor, OrderEventKind.LineVoided, $"Removed {line.Pricing.Quantity} x {line.Display.Name}", lineId: lineId, reason: reason.Trim(), approvedBy: approvedByUserId);
    }

    /// <summary>
    /// Marks every line not yet sent as sent, and returns them for the kitchen ticket and kitchen screens.
    /// Online orders must be accepted first.
    /// </summary>
    public IReadOnlyList<OrderLine> SendToKitchen(OrderActor actor)
    {
        EnsureOpen();
        if (Status is OrderStatus.Pending or OrderStatus.Received)
        {
            throw new InvalidOperationException("Accept the order before sending it to the kitchen.");
        }

        var toSend = new List<OrderLine>();
        for (var i = 0; i < _lines.Count; i++)
        {
            if (!_lines[i].IsVoided && !_lines[i].SentToKitchen)
            {
                _lines[i] = _lines[i] with { SentToKitchen = true };
                toSend.Add(_lines[i]);
            }
        }

        if (toSend.Count > 0)
        {
            Record(actor, OrderEventKind.SentToKitchen, $"Sent {toSend.Count} line(s) to the kitchen");
        }

        return toSend;
    }

    // ---------- Charges ----------

    /// <summary>
    /// Applies or replaces the order discount. Refused if it is above the staff limit and has no manager's approval.
    /// The limit is checked now, when the discount is given (with <paramref name="policy"/>, or the order's own).
    /// </summary>
    public void ApplyDiscount(OrderDiscountInput discount, OrderActor actor, DiscountPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(discount);
        EnsureOpen();
        Totals = Recalculate(_lines, discount, Delivery, ServiceCharge, policy ?? _pricing.DiscountPolicy);
        Discount = discount;
        Record(actor, OrderEventKind.DiscountChanged, $"Discount {Totals.Discount!.AmountOff} applied", reason: discount.Reason, approvedBy: discount.ApprovedByUserId);
    }

    /// <summary>Removes the order discount.</summary>
    public void RemoveDiscount(OrderActor actor)
    {
        EnsureOpen();
        if (Discount is null)
        {
            return;
        }

        Totals = Recalculate(_lines, null, Delivery, ServiceCharge);
        Discount = null;
        Record(actor, OrderEventKind.DiscountChanged, "Discount removed");
    }

    /// <summary>Sets or changes the customer's details (recorded in the history).</summary>
    public void SetContact(OrderContact contact, OrderActor actor)
    {
        ArgumentNullException.ThrowIfNull(contact);
        EnsureOpen();
        Contact = contact.Checked();
        Record(actor, OrderEventKind.ContactChanged, $"Customer details: {Describe(Contact)}");
    }

    /// <summary>Customer details given when the order is created (part of the "created" entry).</summary>
    internal void SetContactAtCreation(OrderContact contact) => Contact = contact.Checked();

    private static string Describe(OrderContact contact) =>
        string.Join(", ", new[] { contact.Name, contact.Phone, contact.Address?.ToString() }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Sets the delivery distance and charges (delivery orders only).</summary>
    public void SetDelivery(DeliveryInput delivery, OrderActor actor)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        EnsureOpen();
        Totals = Recalculate(_lines, Discount, delivery, ServiceCharge);
        Delivery = delivery;
        var description = delivery.Override is { } o
            ? $"Delivery fee changed to {o.Fee}"
            : $"Delivery {delivery.DistanceMiles} miles, fee {Totals.Delivery!.Fee}";
        Record(actor, OrderEventKind.DeliveryChanged, description, reason: delivery.Override?.Reason, approvedBy: delivery.Override?.ByUserId);
    }

    /// <summary>Adds, changes or (with null) removes the service charge.</summary>
    public void SetServiceCharge(ServiceChargeInput? serviceCharge, OrderActor actor)
    {
        EnsureOpen();
        Totals = Recalculate(_lines, Discount, Delivery, serviceCharge);
        ServiceCharge = serviceCharge;
        Record(actor, OrderEventKind.ServiceChargeChanged, serviceCharge is null ? "Service charge removed" : $"Service charge {serviceCharge.BasisPoints / 100m}%");
    }

    // ---------- Status ----------

    /// <summary>Accepts an online or delivery-app order.</summary>
    public void Accept(OrderActor actor) => MoveTo(OrderStatus.Accepted, actor);

    /// <summary>Marks the order as being prepared.</summary>
    public void StartPreparing(OrderActor actor) => MoveTo(OrderStatus.Preparing, actor);

    /// <summary>Marks the order as ready.</summary>
    public void MarkReady(OrderActor actor) => MoveTo(OrderStatus.Ready, actor);

    /// <summary>Marks the order as completed (handed over).</summary>
    public void Complete(OrderActor actor) => MoveTo(OrderStatus.Completed, actor);

    /// <summary>Refuses an order that has not been accepted. A reason is required.</summary>
    public void Reject(string reason, OrderActor actor) => MoveTo(OrderStatus.Rejected, actor, reason);

    /// <summary>
    /// Cancels the order. A reason is required; if anything has gone to the kitchen, a manager must approve.
    /// </summary>
    public void Cancel(string reason, OrderActor actor, Guid? approvedByUserId = null)
    {
        if (_lines.Any(l => l.SentToKitchen && !l.IsVoided) && approvedByUserId is null)
        {
            throw new InvalidOperationException("Food has gone to the kitchen; a manager must approve cancelling.");
        }

        MoveTo(OrderStatus.Cancelled, actor, reason, approvedByUserId);
    }

    /// <summary>Moves to a new status if the rules allow it.</summary>
    public void MoveTo(OrderStatus newStatus, OrderActor actor, string? reason = null, Guid? approvedByUserId = null)
    {
        if (!OrderStatusRules.CanMove(Status, newStatus))
        {
            throw new InvalidOperationException($"An order that is {Status} cannot become {newStatus}.");
        }

        if (newStatus is OrderStatus.Cancelled or OrderStatus.Rejected)
        {
            RequireReason(reason);
        }

        var from = Status;
        Status = newStatus;
        Record(actor, OrderEventKind.StatusChanged, $"{from} → {newStatus}", fromStatus: from, toStatus: newStatus, reason: reason?.Trim(), approvedBy: approvedByUserId);
    }

    // ---------- Internals ----------

    // The discount limit is only checked when a discount is given (policy passed in); re-pricing afterwards never refuses it.
    private PricedOrder Recalculate(IReadOnlyList<OrderLine> lines, OrderDiscountInput? discount, DeliveryInput? delivery, ServiceChargeInput? service, DiscountPolicy? policy = null)
    {
        var fromApp = Source is OrderSource.JustEat or OrderSource.UberEats or OrderSource.Deliveroo;
        var channel = Source switch
        {
            OrderSource.Phone => SalesChannel.Phone,
            OrderSource.Website or OrderSource.Marketplace => SalesChannel.Website,
            _ => SalesChannel.Pos,
        };

        // Delivery apps send their own prices and run their own offers, so ours are not applied.
        var pricer = new OrderPricer(_pricing.VatPolicy, fromApp ? [] : _pricing.Promotions);
        return pricer.Price(new OrderPricingRequest
        {
            OrderType = Type,
            Context = new PricingContext(CreatedAt, _pricing.TimeZone, BranchId, channel),
            Lines = [.. lines.Where(l => !l.IsVoided).Select(l => l.Pricing)],
            Discount = discount,
            DiscountPolicy = policy ?? DiscountPolicy.NoLimit(_pricing.DiscountPolicy.StaffMaxAmount.Currency),
            Delivery = delivery,
            ServiceCharge = service,
        });
    }

    private void EnsureOpen()
    {
        if (IsFinished)
        {
            throw new InvalidOperationException($"This order is {Status} and cannot be changed.");
        }
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required.", nameof(reason));
        }
    }

    private void Record(
        OrderActor actor,
        OrderEventKind kind,
        string description,
        OrderStatus? fromStatus = null,
        OrderStatus? toStatus = null,
        Guid? lineId = null,
        string? reason = null,
        Guid? approvedBy = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _history.Add(new OrderEvent(_clock.GetUtcNow(), actor.UserId, actor.DeviceId, kind, description, fromStatus, toStatus, lineId, reason, approvedBy));
    }
}

/// <summary>Delivery details as plain data.</summary>
/// <param name="DistanceMiles">Distance used.</param>
/// <param name="Table">Charge table in force when the order was taken.</param>
/// <param name="Override">Staff override, if any.</param>
public sealed record DeliveryState(decimal DistanceMiles, DeliveryChargeTableState Table, DeliveryFeeOverride? Override);

/// <summary>An order as plain data: what is saved in the database and synced to the cloud.</summary>
public sealed record OrderState
{
    /// <summary>Order id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Sending device's key.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Content summary at submission.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Branch.</summary>
    public required Guid BranchId { get; init; }

    /// <summary>Order number.</summary>
    public required OrderNumber Number { get; init; }

    /// <summary>Where it came from.</summary>
    public required OrderSource Source { get; init; }

    /// <summary>Eat-in, takeaway or delivery.</summary>
    public required OrderType Type { get; init; }

    /// <summary>Current status.</summary>
    public required OrderStatus Status { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Customer.</summary>
    public Guid? CustomerId { get; init; }

    /// <summary>Table.</summary>
    public Guid? TableId { get; init; }

    /// <summary>All lines, including removed ones.</summary>
    public required IReadOnlyList<OrderLine> Lines { get; init; }

    /// <summary>Full history.</summary>
    public required IReadOnlyList<OrderEvent> History { get; init; }

    /// <summary>Order discount.</summary>
    public OrderDiscountInput? Discount { get; init; }

    /// <summary>Delivery details.</summary>
    public DeliveryState? Delivery { get; init; }

    /// <summary>Customer details.</summary>
    public OrderContact? Contact { get; init; }

    /// <summary>Service charge.</summary>
    public ServiceChargeInput? ServiceCharge { get; init; }
}
