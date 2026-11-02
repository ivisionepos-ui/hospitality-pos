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
public sealed record CreateOrderRequest(
    Guid OrderId,
    string IdempotencyKey,
    OrderSource Source,
    OrderType Type,
    Guid? CustomerId,
    Guid? TableId,
    IReadOnlyList<OrderLineRequest>? Lines);

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
public sealed record AddLinesRequest(IReadOnlyList<OrderLineRequest> Lines);

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
public sealed record ChangePinRequest(string Pin);

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
            t.OutsideScopeOfVat, duplicate);
    }
}
