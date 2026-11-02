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
/// <param name="UserId">Staff member.</param>
/// <param name="DeviceId">Till.</param>
/// <param name="CustomerId">Customer, if known.</param>
/// <param name="TableId">Table, for table service.</param>
/// <param name="Lines">Items and meal deals.</param>
public sealed record CreateOrderRequest(
    Guid OrderId,
    string IdempotencyKey,
    OrderSource Source,
    OrderType Type,
    Guid UserId,
    Guid? DeviceId,
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

/// <summary>Who is acting.</summary>
/// <param name="UserId">Staff member.</param>
/// <param name="DeviceId">Till or screen.</param>
public sealed record ActorRequest(Guid UserId, Guid? DeviceId = null);

/// <summary>Items to add to an open order.</summary>
/// <param name="UserId">Staff member.</param>
/// <param name="DeviceId">Till.</param>
/// <param name="Lines">Items and deals.</param>
public sealed record AddLinesRequest(Guid UserId, Guid? DeviceId, IReadOnlyList<OrderLineRequest> Lines);

/// <summary>Move an order to a new status.</summary>
/// <param name="Status">New status.</param>
/// <param name="UserId">Staff member.</param>
/// <param name="DeviceId">Till or screen.</param>
/// <param name="Reason">Required for Cancelled and Rejected.</param>
/// <param name="ApprovedByUserId">Manager, when approval is needed.</param>
public sealed record StatusRequest(OrderStatus Status, Guid UserId, Guid? DeviceId = null, string? Reason = null, Guid? ApprovedByUserId = null);

/// <summary>Remove a line.</summary>
/// <param name="UserId">Staff member.</param>
/// <param name="Reason">Why.</param>
/// <param name="DeviceId">Till.</param>
/// <param name="ApprovedByUserId">Manager, if the item went to the kitchen.</param>
public sealed record VoidRequest(Guid UserId, string Reason, Guid? DeviceId = null, Guid? ApprovedByUserId = null);

/// <summary>Change a price.</summary>
/// <param name="VariantId">Size.</param>
/// <param name="PricePence">New price in pence.</param>
/// <param name="UserId">Who.</param>
public sealed record PriceRequest(Guid VariantId, long PricePence, Guid UserId);

/// <summary>Sold out or back in stock.</summary>
/// <param name="SoldOut">True = sold out.</param>
/// <param name="UserId">Who.</param>
public sealed record SoldOutRequest(bool SoldOut, Guid UserId);

/// <summary>Rename an item.</summary>
/// <param name="Name">New name.</param>
/// <param name="UserId">Who.</param>
/// <param name="ButtonName">Short button name, or null to use the name.</param>
public sealed record RenameRequest(LocalizedText Name, Guid UserId, LocalizedText? ButtonName = null);

/// <summary>Import a whole menu.</summary>
/// <param name="UserId">Who.</param>
/// <param name="Menu">The menu.</param>
public sealed record ImportMenuRequest(Guid UserId, MenuState Menu);

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
