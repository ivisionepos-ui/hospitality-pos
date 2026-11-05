using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Tax;

namespace Pos.Hub.Api;

/// <summary>A new order from a till: what was picked, not prices (the hub prices it from the menu).</summary>
/// <param name="OrderId">Id made on the till (UUID v7).</param>
/// <param name="IdempotencyKey">Key the till reuses on every retry of this order.</param>
/// <param name="Source">Counter, Table, Phone…</param>
/// <param name="Type">EatIn, Takeaway or Delivery.</param>
/// <param name="CustomerId">Customer, if known.</param>
/// <param name="TableId">Table, for table service.</param>
/// <param name="Lines">Items and meal deals.</param>
/// <param name="Contact">Customer name, phone, address and notes (phone and delivery orders). With a phone, the customer is remembered.</param>
/// <param name="Delivery">Distance and any fee change (delivery orders).</param>
public sealed record CreateOrderRequest(
    Guid OrderId,
    string IdempotencyKey,
    OrderSource Source,
    OrderType Type,
    Guid? CustomerId,
    Guid? TableId,
    IReadOnlyList<OrderLineRequest>? Lines,
    OrderContact? Contact = null,
    DeliveryRequest? Delivery = null);

/// <summary>Delivery distance, and a changed fee if a manager agreed one.</summary>
/// <param name="DistanceMiles">Distance from the shop, from the postcode check or typed in.</param>
/// <param name="FeeOverridePence">A different fee in pence (needs a manager); empty = the normal charge.</param>
/// <param name="OverrideReason">Why the fee was changed.</param>
public sealed record DeliveryRequest(decimal DistanceMiles, long? FeeOverridePence = null, string? OverrideReason = null);

/// <summary>Delivery as shown on the till.</summary>
/// <param name="DistanceMiles">Distance used.</param>
/// <param name="Status">Ok, BelowMinimumOrder or BeyondMaximumDistance.</param>
/// <param name="Fee">Fee charged.</param>
/// <param name="MinimumOrder">Minimum order that applies, if any.</param>
/// <param name="IsFree">Free because the order is big enough.</param>
/// <param name="Overridden">A manager changed the fee.</param>
public sealed record DeliveryView(decimal DistanceMiles, Pos.Domain.Delivery.DeliveryQuoteStatus Status, Money Fee, Money? MinimumOrder, bool IsFree, bool Overridden);

/// <summary>One item or meal deal picked on the till. With Choices it is a meal deal.</summary>
/// <param name="ProductId">Item or deal.</param>
/// <param name="VariantId">Size.</param>
/// <param name="Quantity">How many.</param>
/// <param name="Extras">Extras chosen (normal items).</param>
/// <param name="Choices">Meal deal choices.</param>
/// <param name="Note">Special instructions.</param>
public sealed record OrderLineRequest(
    Guid ProductId,
    Guid VariantId,
    int Quantity = 1,
    IReadOnlyList<Guid>? Extras = null,
    IReadOnlyList<DealChoiceRequest>? Choices = null,
    string? Note = null);

/// <summary>A choice inside a meal deal.</summary>
/// <param name="SlotId">The deal's choice slot.</param>
/// <param name="ProductId">Item chosen.</param>
/// <param name="VariantId">Size chosen.</param>
/// <param name="Extras">Extras on it.</param>
public sealed record DealChoiceRequest(Guid SlotId, Guid ProductId, Guid VariantId, IReadOnlyList<Guid>? Extras = null);

/// <summary>Items to add to an open order.</summary>
/// <param name="Lines">Items and deals.</param>
/// <param name="RequestId">Id made on the till and reused on every retry, so the items are added only once.</param>
public sealed record AddLinesRequest(IReadOnlyList<OrderLineRequest> Lines, Guid? RequestId = null);

/// <summary>Move an order to a new status. A manager's PIN for approval goes in the X-Manager-Pin header.</summary>
/// <param name="Status">New status.</param>
/// <param name="Reason">Required for Cancelled and Rejected.</param>
public sealed record StatusRequest(OrderStatus Status, string? Reason = null);

/// <summary>Remove a line. A manager's PIN for approval goes in the X-Manager-Pin header.</summary>
/// <param name="Reason">Why.</param>
public sealed record VoidRequest(string Reason);

/// <summary>Change a price.</summary>
/// <param name="VariantId">Size.</param>
/// <param name="PricePence">New price in pence.</param>
public sealed record PriceRequest(Guid VariantId, long PricePence);

/// <summary>Sold out or back in stock.</summary>
/// <param name="SoldOut">True = sold out.</param>
public sealed record SoldOutRequest(bool SoldOut);

/// <summary>Rename an item.</summary>
/// <param name="Name">New name.</param>
/// <param name="ButtonName">Short button name, or null to use the name.</param>
public sealed record RenameRequest(LocalizedText Name, LocalizedText? ButtonName = null);

/// <summary>First-time set-up from the hub PC itself.</summary>
/// <param name="OwnerName">Owner's name.</param>
/// <param name="Pin">Owner's PIN (4–8 digits).</param>
/// <param name="DeviceName">Name for this PC, e.g. "Office PC".</param>
public sealed record SetupRequest(string OwnerName, string Pin, string DeviceName);

/// <summary>Result of first-time set-up. The device key is shown only once.</summary>
/// <param name="Owner">The owner.</param>
/// <param name="Device">This PC's credentials.</param>
/// <param name="Session">The owner's login on this PC.</param>
public sealed record SetupResult(Security.StaffMember Owner, Security.PairedDevice Device, Security.LoginResult Session);

/// <summary>Register a till or screen with a pairing code.</summary>
/// <param name="Code">6-digit code from the owner.</param>
/// <param name="Name">Name, e.g. "Till 2".</param>
/// <param name="Type">Kind of device.</param>
public sealed record PairRequest(string Code, string Name, Security.DeviceType Type);

/// <summary>A new pairing code.</summary>
/// <param name="Code">The code to type on the new device.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record PairingCodeView(string Code, DateTimeOffset ExpiresAt);

/// <summary>Staff PIN login on a registered device.</summary>
/// <param name="Pin">The PIN.</param>
public sealed record LoginRequest(string Pin);

/// <summary>Add a staff member.</summary>
/// <param name="Name">Name.</param>
/// <param name="Role">Role.</param>
/// <param name="Pin">PIN (4–8 digits, unique).</param>
public sealed record AddStaffRequest(string Name, Pos.Domain.Staff.StaffRole Role, string Pin);

/// <summary>Change a PIN.</summary>
/// <param name="Pin">New PIN.</param>
/// <param name="CurrentPin">The person's current PIN; needed when changing your own.</param>
public sealed record ChangePinRequest(string Pin, string? CurrentPin = null);

/// <summary>Change a staff member's name or role.</summary>
/// <param name="Name">Name.</param>
/// <param name="Role">Role.</param>
public sealed record UpdateStaffRequest(string Name, Pos.Domain.Staff.StaffRole Role);

/// <summary>Bring back a removed staff member.</summary>
/// <param name="Pin">Their new PIN.</param>
public sealed record ReactivateStaffRequest(string Pin);

/// <summary>A menu change as returned to the till.</summary>
/// <param name="Version">New menu version.</param>
/// <param name="Summary">What changed.</param>
public sealed record MenuChangeView(long Version, string Summary);

/// <summary>A reason a request was refused.</summary>
/// <param name="Code">Stable code for the screen.</param>
/// <param name="Message">English explanation.</param>
/// <param name="Problems">Details for refused selections.</param>
public sealed record ErrorView(string Code, string Message, IReadOnlyList<SelectionProblem>? Problems = null);

/// <summary>An order line as shown on tills and kitchen screens.</summary>
public sealed record LineView(
    Guid Id,
    LocalizedText Name,
    LocalizedText? Size,
    IReadOnlyList<LocalizedText> Extras,
    IReadOnlyList<LineDisplay> DealItems,
    int Quantity,
    Money? UnitTotal,
    Money? LineTotal,
    string? Note,
    bool SentToKitchen,
    bool IsVoided,
    string? VoidReason);

/// <summary>A VAT line.</summary>
public sealed record VatView(string Code, decimal Percent, Money Gross, Money Net, Money Vat);

/// <summary>An order as returned by the hub.</summary>
public sealed record OrderView(
    Guid Id,
    string Number,
    string Reference,
    DateOnly BusinessDate,
    OrderStatus Status,
    OrderSource Source,
    OrderType Type,
    DateTimeOffset CreatedAt,
    IReadOnlyList<LineView> Lines,
    Money GoodsTotal,
    Money? DiscountAmount,
    Money? DeliveryFee,
    Money? ServiceCharge,
    Money Total,
    IReadOnlyList<VatView> Vat,
    Money OutsideScopeOfVat,
    bool Duplicate)
{
    /// <summary>Paid so far (after refunds), when known.</summary>
    public Money? Paid { get; init; }

    /// <summary>Still to pay (negative = refund due), when known.</summary>
    public Money? Balance { get; init; }

    /// <summary>Builds the view of an order.</summary>
    public static OrderView From(Order order, bool duplicate = false)
    {
        ArgumentNullException.ThrowIfNull(order);
        var priced = order.ActiveLines.Zip(order.Totals.Lines).ToDictionary(x => x.First.Id, x => x.Second);
        var lines = order.Lines.Select(l =>
        {
            priced.TryGetValue(l.Id, out var p);
            return new LineView(l.Id, l.Display.Name, l.Display.Size, l.Display.Extras, l.Display.DealItems, l.Pricing.Quantity,
                p?.UnitTotal, p?.LineTotal, l.Note, l.SentToKitchen, l.IsVoided, l.VoidReason);
        }).ToList();

        var t = order.Totals;
        return new OrderView(
            order.Id, order.Number.Display, order.Number.Reference, order.Number.BusinessDate, order.Status, order.Source, order.Type,
            order.CreatedAt, lines, t.GoodsTotal, t.Discount?.AmountOff, t.Delivery?.Fee, t.ServiceCharge?.Amount, t.Total,
            [.. t.VatSummary.Select(v => new VatView(v.Rate.Code, v.Rate.Percent, v.Gross, v.Net, v.Vat))],
            t.OutsideScopeOfVat, duplicate)
        {
            Contact = order.Contact,
            Delivery = t.Delivery is { } d
                ? new DeliveryView(d.Quote.DistanceMiles, d.Override is null ? d.Quote.Status : Pos.Domain.Delivery.DeliveryQuoteStatus.Ok, d.Fee, d.Quote.MinimumOrder, d.Quote.IsFreeDelivery, d.Override is not null)
                : null,
        };
    }

    /// <summary>Customer details, if any.</summary>
    public OrderContact? Contact { get; init; }

    /// <summary>Delivery distance, fee and whether it is allowed, for delivery orders.</summary>
    public DeliveryView? Delivery { get; init; }
}

/// <summary>Open a shift.</summary>
/// <param name="OpeningFloatPence">Cash in the drawer at the start, in pence.</param>
public sealed record OpenShiftRequest(long OpeningFloatPence);

/// <summary>Paid in or paid out. A manager's PIN goes in X-Manager-Pin unless the logged-in user is a manager.</summary>
/// <param name="AmountPence">Amount in pence.</param>
/// <param name="Reason">Why.</param>
public sealed record CashMovementRequest(long AmountPence, string Reason);

/// <summary>Close a shift with the cash counted in the drawer.</summary>
/// <param name="CountedCashPence">Counted cash in pence.</param>
public sealed record CloseShiftRequest(long CountedCashPence);

/// <summary>Take cash.</summary>
/// <param name="TenderedPence">Cash handed over, in pence.</param>
/// <param name="AmountPence">Amount to put towards the order (a share of a split bill); empty = as much of the balance as the cash covers.</param>
/// <param name="ForLineIds">Items this payment is for (split by items).</param>
/// <param name="PaymentId">Id made on the till, reused on retries.</param>
public sealed record CashPaymentRequest(long TenderedPence, long? AmountPence = null, IReadOnlyList<Guid>? ForLineIds = null, Guid? PaymentId = null);

/// <summary>Record a card payment taken on the terminal.</summary>
/// <param name="AmountPence">Amount towards the order, in pence.</param>
/// <param name="TipPence">Tip added on the terminal, in pence.</param>
/// <param name="Reference">Terminal receipt number.</param>
/// <param name="ForLineIds">Items this payment is for (split by items).</param>
/// <param name="PaymentId">Id made on the till, reused on retries.</param>
public sealed record CardPaymentRequest(long AmountPence, long? TipPence = null, string? Reference = null, IReadOnlyList<Guid>? ForLineIds = null, Guid? PaymentId = null);

/// <summary>Refund a payment. A manager's PIN goes in X-Manager-Pin unless the logged-in user is a manager.</summary>
/// <param name="AmountPence">Amount to refund, in pence.</param>
/// <param name="Reason">Why.</param>
/// <param name="RefundId">Id made on the till, reused on retries.</param>
public sealed record RefundRequest(long AmountPence, string Reason, Guid? RefundId = null);

/// <summary>Amount for chosen items.</summary>
/// <param name="LineIds">Order lines.</param>
public sealed record SplitByItemsRequest(IReadOnlyList<Guid> LineIds);

/// <summary>Take the Z report.</summary>
/// <param name="BusinessDate">Trading day; empty = today.</param>
public sealed record ZReportRequest(DateOnly? BusinessDate);

/// <summary>An order's money.</summary>
public sealed record PaymentsView(Money Total, Money Paid, Money Balance, Money Tips, IReadOnlyList<Pos.Domain.Payments.Payment> Payments);

/// <summary>Result of a payment or refund.</summary>
/// <param name="Payment">The payment.</param>
/// <param name="Order">The order's money after it.</param>
/// <param name="Duplicate">True when the till re-sent a payment already taken.</param>
public sealed record PaymentResponse(Pos.Domain.Payments.Payment Payment, PaymentsView Order, bool Duplicate);

/// <summary>Shares of a split bill.</summary>
/// <param name="Shares">Amount for each share.</param>
public sealed record SplitView(IReadOnlyList<Money> Shares);

/// <summary>Save an item's edit panel.</summary>
/// <param name="Product">The item as it should be.</param>
/// <param name="Original">The item as the till loaded it; if it has changed since (on another till), the save is refused.</param>
public sealed record SaveProductRequest(Product Product, Product? Original = null);

/// <summary>Add an item button.</summary>
/// <param name="Product">The new item (id made on the till).</param>
/// <param name="Position">Place in its category (default: the end).</param>
public sealed record AddProductRequest(Product Product, int? Position = null);

/// <summary>Move an item button or category tab.</summary>
/// <param name="Position">New place, counting from 0.</param>
/// <param name="CategoryId">Category to move an item into (default: its own).</param>
public sealed record MoveRequest(int Position, Guid? CategoryId = null);

/// <summary>Save a category's edit panel.</summary>
/// <param name="Category">The category as it should be.</param>
/// <param name="Original">As the till loaded it; refused if changed since.</param>
public sealed record SaveCategoryRequest(Category Category, Category? Original = null);

/// <summary>Save a group of extras (e.g. sauces, toppings).</summary>
/// <param name="Group">The group as it should be.</param>
/// <param name="Original">As the till loaded it; refused if changed since. Empty for a new group.</param>
public sealed record SaveModifierGroupRequest(ModifierGroup Group, ModifierGroup? Original = null);

/// <summary>Raised when an edit was based on an item that has changed since it was loaded.</summary>
public sealed class ChangedElsewhereException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public ChangedElsewhereException()
        : base("Someone else changed this on another till while you were editing. Open it again to see the latest, then make your change.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ChangedElsewhereException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public ChangedElsewhereException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Refuses the edit if the item no longer matches what the till loaded.</summary>
    public static void ThrowIfChanged<T>(T current, T? original)
        where T : class
    {
        if (original is not null && PosJson.Serialize(current) != PosJson.Serialize(original))
        {
            throw new ChangedElsewhereException();
        }
    }
}
