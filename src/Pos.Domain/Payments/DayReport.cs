using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Pricing;
using Pos.Domain.Tax;

namespace Pos.Domain.Payments;

/// <summary>Sales from orders completed in the day.</summary>
/// <param name="Orders">Number of completed orders.</param>
/// <param name="ItemsAtMenuPrice">Food and drink before offers.</param>
/// <param name="OfferSavings">Saved through scheduled offers.</param>
/// <param name="Discounts">Order discounts given.</param>
/// <param name="DeliveryFees">Delivery fees charged.</param>
/// <param name="ServiceCharges">Service charges.</param>
/// <param name="Total">Total of completed orders.</param>
public sealed record SalesTotals(int Orders, Money ItemsAtMenuPrice, Money OfferSavings, Money Discounts, Money DeliveryFees, Money ServiceCharges, Money Total);

/// <summary>Orders by where they came from.</summary>
/// <param name="Source">Counter, phone, Just Eat…</param>
/// <param name="Orders">Completed orders.</param>
/// <param name="Total">Their total.</param>
public sealed record SourceTotals(OrderSource Source, int Orders, Money Total);

/// <summary>VAT for one rate, after refunds.</summary>
/// <param name="Code">Rate code (S, Z).</param>
/// <param name="Percent">Rate.</param>
/// <param name="Gross">Sales including VAT, less refunds.</param>
/// <param name="Net">Gross less VAT.</param>
/// <param name="Vat">VAT.</param>
public sealed record VatTotals(string Code, decimal Percent, Money Gross, Money Net, Money Vat);

/// <summary>Things a manager should look at.</summary>
/// <param name="CancelledOrders">Orders cancelled.</param>
/// <param name="CancelledValue">Their value.</param>
/// <param name="RejectedOrders">Online orders refused.</param>
/// <param name="VoidedItems">Items removed from orders.</param>
/// <param name="VoidedValue">Their value at menu price.</param>
/// <param name="Refunds">Refunds made.</param>
/// <param name="RefundedValue">Amount refunded.</param>
/// <param name="OpenOrders">Orders of the day not finished when the report was taken.</param>
public sealed record ExceptionTotals(int CancelledOrders, Money CancelledValue, int RejectedOrders, int VoidedItems, Money VoidedValue, int Refunds, Money RefundedValue, int OpenOrders);

/// <summary>Card tips by staff member (for the Tipping Act record).</summary>
/// <param name="UserId">Staff member who took the payments.</param>
/// <param name="Tips">Tips.</param>
public sealed record StaffTips(Guid UserId, Money Tips);

/// <summary>
/// The end-of-day (Z) report for one trading day. Once taken it is kept and never changes.
/// </summary>
public sealed record DayReport
{
    /// <summary>Trading day.</summary>
    public required DateOnly BusinessDate { get; init; }

    /// <summary>Running Z number.</summary>
    public required int ZNumber { get; init; }

    /// <summary>When it was taken.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Who took it.</summary>
    public required Guid CreatedBy { get; init; }

    /// <summary>Sales from completed orders.</summary>
    public required SalesTotals Sales { get; init; }

    /// <summary>Completed orders by source.</summary>
    public required IReadOnlyList<SourceTotals> BySource { get; init; }

    /// <summary>Money taken and refunded, by method.</summary>
    public required IReadOnlyList<MethodTotals> Payments { get; init; }

    /// <summary>VAT by rate, after refunds.</summary>
    public required IReadOnlyList<VatTotals> Vat { get; init; }

    /// <summary>Cancels, voids, refunds, open orders.</summary>
    public required ExceptionTotals Exceptions { get; init; }

    /// <summary>Each till shift with its cash count.</summary>
    public required IReadOnlyList<ShiftSummary> Shifts { get; init; }

    /// <summary>Card tips by staff member.</summary>
    public required IReadOnlyList<StaffTips> TipsByStaff { get; init; }

    /// <summary>Total cash over (+) or short (−) across all shifts.</summary>
    public required Money CashVariance { get; init; }

    /// <summary>
    /// Builds the report. Every shift of the day must be closed.
    /// <paramref name="refundedOrders"/> gives the totals of orders refunded today (which may be from earlier days),
    /// so the VAT on refunds can be taken off at the right rates.
    /// </summary>
    public static DayReport Build(
        DateOnly businessDate,
        int zNumber,
        DateTimeOffset createdAt,
        Guid createdBy,
        IReadOnlyList<Order> orders,
        IReadOnlyList<Payment> payments,
        IReadOnlyList<Shift> shifts,
        IReadOnlyDictionary<Guid, PricedOrder> refundedOrders,
        string currency = "GBP")
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(shifts);
        ArgumentNullException.ThrowIfNull(refundedOrders);
        if (shifts.Any(s => s.IsOpen))
        {
            throw new InvalidOperationException("Close every till shift before taking the Z report.");
        }

        var zero = Money.Zero(currency);
        Money Sum(IEnumerable<Money> values) => values.Aggregate(zero, (a, b) => a + b);

        var completed = orders.Where(o => o.Status == OrderStatus.Completed).ToList();
        var totals = completed.Select(o => o.Totals).ToList();
        var sales = new SalesTotals(
            completed.Count,
            Sum(totals.Select(t => t.GoodsTotal + t.TotalSaving)),
            Sum(totals.Select(t => t.TotalSaving)),
            Sum(totals.Select(t => t.Discount?.AmountOff ?? zero)),
            Sum(totals.Select(t => t.Delivery?.Fee ?? zero)),
            Sum(totals.Select(t => t.ServiceCharge?.Amount ?? zero)),
            Sum(totals.Select(t => t.Total)));

        var bySource = completed
            .GroupBy(o => o.Source)
            .Select(g => new SourceTotals(g.Key, g.Count(), Sum(g.Select(o => o.Totals.Total))))
            .OrderBy(s => s.Source)
            .ToList();

        // Payments by method across all of the day's shifts.
        var shiftIds = shifts.Select(s => s.Id).ToHashSet();
        var dayPayments = payments.Where(p => shiftIds.Contains(p.ShiftId)).ToList();
        var byMethod = Enum.GetValues<PaymentMethod>()
            .Select(m => new MethodTotals(
                m,
                dayPayments.Count(p => p.Method == m && !p.IsRefund),
                Sum(dayPayments.Where(p => p.Method == m && !p.IsRefund).Select(p => p.Amount)),
                dayPayments.Count(p => p.Method == m && p.IsRefund),
                Sum(dayPayments.Where(p => p.Method == m && p.IsRefund).Select(p => zero - p.Amount)),
                Sum(dayPayments.Where(p => p.Method == m).Select(p => p.Tip))))
            .Where(t => t.Payments + t.Refunds > 0)
            .ToList();

        // VAT: completed sales by rate, less each refund spread over its order's rates.
        var vatGross = new Dictionary<VatRate, Money>();
        void AddGross(VatRate rate, Money amount) => vatGross[rate] = vatGross.GetValueOrDefault(rate, zero) + amount;
        foreach (var line in totals.SelectMany(t => t.VatSummary))
        {
            AddGross(line.Rate, line.Gross);
        }

        var refunds = dayPayments.Where(p => p.IsRefund).ToList();
        foreach (var refund in refunds)
        {
            if (!refundedOrders.TryGetValue(refund.OrderId, out var refundedOrder) || refundedOrder.VatSummary.Count == 0)
            {
                continue;
            }

            var shares = ProportionalAllocator.Allocate(zero - refund.Amount, [.. refundedOrder.VatSummary.Select(v => v.Gross)]);
            for (var i = 0; i < shares.Count; i++)
            {
                AddGross(refundedOrder.VatSummary[i].Rate, zero - shares[i]);
            }
        }

        var vat = vatGross
            .OrderByDescending(v => v.Key.BasisPoints)
            .Select(v =>
            {
                var tax = VatCalculator.VatInGross(v.Value, v.Key);
                return new VatTotals(v.Key.Code, v.Key.Percent, v.Value, v.Value - tax, tax);
            })
            .ToList();

        var cancelled = orders.Where(o => o.Status == OrderStatus.Cancelled).ToList();
        var voided = orders.SelectMany(o => o.Lines.Where(l => l.IsVoided)).ToList();
        var exceptions = new ExceptionTotals(
            cancelled.Count,
            Sum(cancelled.Select(o => o.Totals.Total)),
            orders.Count(o => o.Status == OrderStatus.Rejected),
            voided.Sum(l => l.Pricing.Quantity),
            Sum(voided.Select(l => MenuValue(l.Pricing, currency))),
            refunds.Count,
            Sum(refunds.Select(r => zero - r.Amount)),
            orders.Count(o => !o.IsFinished));

        var shiftSummaries = shifts.OrderBy(s => s.OpenedAt).Select(s => s.Summarise(payments)).ToList();
        var tips = dayPayments
            .Where(p => p.Tip.MinorUnits != 0)
            .GroupBy(p => p.UserId)
            .Select(g => new StaffTips(g.Key, Sum(g.Select(p => p.Tip))))
            .ToList();

        return new DayReport
        {
            BusinessDate = businessDate,
            ZNumber = zNumber,
            CreatedAt = createdAt,
            CreatedBy = createdBy,
            Sales = sales,
            BySource = bySource,
            Payments = byMethod,
            Vat = vat,
            Exceptions = exceptions,
            Shifts = shiftSummaries,
            TipsByStaff = tips,
            CashVariance = Sum(shiftSummaries.Select(s => s.Variance ?? zero)),
        };
    }

    /// <summary>Value of a removed line at menu prices (before offers and discounts).</summary>
    private static Money MenuValue(OrderLineInput line, string currency)
    {
        var zero = Money.Zero(currency);
        var unit = line switch
        {
            ItemLineInput item => item.Modifiers.Aggregate(item.Item.NormalUnitPrice, (s, m) => s + m.UnitPrice),
            MealDealLineInput deal => deal.Components.Aggregate(
                deal.Deal.NormalUnitPrice,
                (s, c) => c.Modifiers.Aggregate(s + (c.Upcharge ?? zero), (t, m) => t + m.UnitPrice)),
            _ => zero,
        };
        return unit * line.Quantity;
    }
}
