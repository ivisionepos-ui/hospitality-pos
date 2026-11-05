using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Pricing;
using Pos.Domain.Tax;

namespace Pos.Domain.Reports;

/// <summary>The headline figures for a period.</summary>
/// <param name="Orders">Completed orders.</param>
/// <param name="ItemsSold">Items sold (a meal deal counts as one).</param>
/// <param name="Sales">Total of completed orders (after offers and discounts, with delivery and service charges).</param>
/// <param name="AverageOrder">Sales ÷ orders.</param>
/// <param name="OfferSavings">Saved through scheduled offers.</param>
/// <param name="Discounts">Order discounts given.</param>
/// <param name="DeliveryFees">Delivery charges.</param>
/// <param name="ServiceCharges">Service charges.</param>
/// <param name="Refunds">Money given back for what was sold, in the period (lowers the sales).</param>
/// <param name="NetSales">Sales less those refunds.</param>
/// <param name="CancelledOrders">Orders cancelled.</param>
/// <param name="CancelledValue">Their value.</param>
/// <param name="VoidedItems">Items removed from orders.</param>
public sealed record SalesSummary(
    int Orders, int ItemsSold, Money Sales, Money AverageOrder, Money OfferSavings, Money Discounts, Money DeliveryFees, Money ServiceCharges,
    Money Refunds, Money NetSales, int CancelledOrders, Money CancelledValue, int VoidedItems);

/// <summary>One row of a "sales by …" table.</summary>
/// <param name="Name">What the row is (an item, a category, an hour "18:00", a date, a type).</param>
/// <param name="Quantity">Items (for items and categories) or orders (for hours, days, types).</param>
/// <param name="Sales">Sales: for items and categories after their share of discounts, before delivery charges.</param>
public sealed record SalesRow(string Name, int Quantity, Money Sales);

/// <summary>What one staff member did in the period.</summary>
/// <param name="Name">Name.</param>
/// <param name="Orders">Completed orders they took.</param>
/// <param name="Sales">Their total.</param>
/// <param name="Discounts">Discounts they gave.</param>
/// <param name="DiscountValue">Value of those discounts.</param>
/// <param name="ItemsRemoved">Items they removed from orders.</param>
/// <param name="Refunds">Refunds they made.</param>
/// <param name="RefundedValue">Amount refunded.</param>
public sealed record StaffRow(string Name, int Orders, Money Sales, int Discounts, Money DiscountValue, int ItemsRemoved, int Refunds, Money RefundedValue);

/// <summary>Money by payment method in the period.</summary>
/// <param name="Method">Cash, card…</param>
/// <param name="Taken">Payments taken.</param>
/// <param name="Refunded">Refunds given.</param>
/// <param name="Net">Taken less refunded.</param>
/// <param name="Tips">Card tips (not part of sales).</param>
public sealed record MethodRow(PaymentMethod Method, Money Taken, Money Refunded, Money Net, Money Tips);

/// <summary>
/// Sales for any range of trading days (back-office reports): totals, items, categories, hours, days, order types,
/// staff, payments and VAT. Sales are from orders completed in the range (by trading day); refunds and payments
/// by the day they were made. The Z reports stay the record for each day.
/// </summary>
public sealed record SalesReport
{
    /// <summary>First trading day.</summary>
    public required DateOnly From { get; init; }

    /// <summary>Last trading day.</summary>
    public required DateOnly To { get; init; }

    /// <summary>Headline figures.</summary>
    public required SalesSummary Summary { get; init; }

    /// <summary>Items, best-selling (by sales) first.</summary>
    public required IReadOnlyList<SalesRow> Items { get; init; }

    /// <summary>Categories, by sales.</summary>
    public required IReadOnlyList<SalesRow> Categories { get; init; }

    /// <summary>Orders and sales by hour of the day the order was placed (local time).</summary>
    public required IReadOnlyList<SalesRow> Hours { get; init; }

    /// <summary>Orders and sales by trading day.</summary>
    public required IReadOnlyList<SalesRow> Days { get; init; }

    /// <summary>Orders and sales by takeaway, eat in, delivery, and by where they came from.</summary>
    public required IReadOnlyList<SalesRow> Types { get; init; }

    /// <summary>What each staff member did.</summary>
    public required IReadOnlyList<StaffRow> Staff { get; init; }

    /// <summary>Money by payment method.</summary>
    public required IReadOnlyList<MethodRow> Payments { get; init; }

    /// <summary>VAT by rate, after refunds.</summary>
    public required IReadOnlyList<VatTotals> Vat { get; init; }

    /// <summary>Builds the report.</summary>
    /// <param name="from">First trading day.</param>
    /// <param name="to">Last trading day.</param>
    /// <param name="orders">Every order of those trading days.</param>
    /// <param name="payments">Every payment and refund made on those trading days.</param>
    /// <param name="refundedOrders">Totals of the (not cancelled) orders refunded in the period, for VAT on refunds.</param>
    /// <param name="timeZone">The shop's time zone (for hours).</param>
    /// <param name="categoryName">Name of a category (by id).</param>
    /// <param name="staffName">Name of a staff member (by id).</param>
    /// <param name="currency">Currency.</param>
    public static SalesReport Build(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<Order> orders,
        IReadOnlyList<Payment> payments,
        IReadOnlyDictionary<Guid, PricedOrder> refundedOrders,
        TimeZoneInfo timeZone,
        Func<Guid, string> categoryName,
        Func<Guid, string> staffName,
        string currency = "GBP")
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(refundedOrders);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(categoryName);
        ArgumentNullException.ThrowIfNull(staffName);
        if (to < from)
        {
            throw new ArgumentException("The end date is before the start date.");
        }

        var zero = Money.Zero(currency);
        Money Sum(IEnumerable<Money> values) => values.Aggregate(zero, (a, b) => a + b);

        var completed = orders.Where(o => o.Status == OrderStatus.Completed).ToList();
        var sales = Sum(completed.Select(o => o.Totals.Total));

        // Items and categories: each active line with its price after its share of the order discount.
        var lines = completed.SelectMany(o => o.ActiveLines.Zip(o.Totals.Lines, (line, priced) => (line, priced))).ToList();
        static string ItemName(OrderLine line) => line.Display.Size is { } size ? $"{line.Display.Name.En} ({size.En})" : line.Display.Name.En;
        static Guid CategoryOf(OrderLineInput input) => input switch
        {
            ItemLineInput item => item.Item.CategoryId,
            MealDealLineInput deal => deal.Deal.CategoryId,
            _ => Guid.Empty,
        };
        Money LineSales((OrderLine Line, PricedLine Priced) x) => x.Priced.LineTotal - x.Priced.DiscountShare;

        static IReadOnlyList<SalesRow> BySales(IEnumerable<SalesRow> rows) =>
            [.. rows.OrderByDescending(r => r.Sales.MinorUnits).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];

        var items = BySales(lines.GroupBy(x => ItemName(x.line), StringComparer.OrdinalIgnoreCase)
            .Select(g => new SalesRow(g.Key, g.Sum(x => x.line.Pricing.Quantity), Sum(g.Select(LineSales)))));
        var categories = BySales(lines.GroupBy(x => CategoryOf(x.line.Pricing))
            .Select(g => new SalesRow(categoryName(g.Key), g.Sum(x => x.line.Pricing.Quantity), Sum(g.Select(LineSales)))));

        var hours = completed.GroupBy(o => TimeZoneInfo.ConvertTime(o.CreatedAt, timeZone).Hour).OrderBy(g => g.Key)
            .Select(g => new SalesRow($"{g.Key:00}:00", g.Count(), Sum(g.Select(o => o.Totals.Total)))).ToList();
        var days = completed.GroupBy(o => o.Number.BusinessDate).OrderBy(g => g.Key)
            .Select(g => new SalesRow(g.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), g.Count(), Sum(g.Select(o => o.Totals.Total)))).ToList();
        static string TypeName(OrderType t) => t switch { OrderType.EatIn => "Eat in", OrderType.Takeaway => "Takeaway", OrderType.Delivery => "Delivery", _ => t.ToString() };
        var types = completed.GroupBy(o => TypeName(o.Type)).Select(g => new SalesRow(g.Key, g.Count(), Sum(g.Select(o => o.Totals.Total))))
            .Concat(completed.GroupBy(o => $"From {o.Source}").Select(g => new SalesRow(g.Key, g.Count(), Sum(g.Select(o => o.Totals.Total)))))
            .ToList();

        // Refunds and payments made in the period. Only money given back for what was sold lowers the sales.
        var refunds = payments.Where(p => p.IsRefund).ToList();
        var refundsForSale = Sum(refunds.Where(r => refundedOrders.ContainsKey(r.OrderId)).Select(r => r.SaleReductionOrAll));

        var methods = payments.GroupBy(p => p.Method).OrderBy(g => g.Key).Select(g =>
        {
            var taken = Sum(g.Where(p => !p.IsRefund).Select(p => p.Amount));
            var refunded = Sum(g.Where(p => p.IsRefund).Select(p => zero - p.Amount));
            return new MethodRow(g.Key, taken, refunded, taken - refunded, Sum(g.Select(p => p.Tip)));
        }).ToList();

        // Staff: who took each order (the person who created it), discounts given, items removed, refunds made.
        var staffIds = completed.Select(o => o.History[0].UserId)
            .Concat(completed.Where(o => o.Discount is not null).Select(o => o.Discount!.GivenByUserId))
            .Concat(orders.SelectMany(o => o.History.Where(h => h.Kind == OrderEventKind.LineVoided).Select(h => h.UserId)))
            .Concat(refunds.Select(r => r.UserId))
            .Distinct();
        var staff = staffIds.Select(id =>
        {
            var taken = completed.Where(o => o.History[0].UserId == id).ToList();
            var discounted = completed.Where(o => o.Discount?.GivenByUserId == id).ToList();
            var theirRefunds = refunds.Where(r => r.UserId == id).ToList();
            return new StaffRow(
                staffName(id), taken.Count, Sum(taken.Select(o => o.Totals.Total)),
                discounted.Count, Sum(discounted.Select(o => o.Totals.Discount?.AmountOff ?? zero)),
                orders.Sum(o => o.History.Count(h => h.Kind == OrderEventKind.LineVoided && h.UserId == id)),
                theirRefunds.Count, Sum(theirRefunds.Select(r => zero - r.Amount)));
        }).OrderByDescending(s => s.Sales.MinorUnits).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // VAT: completed sales by rate, less each refund for what was sold, spread over its order's rates.
        var vatGross = new Dictionary<VatRate, Money>();
        void AddGross(VatRate rate, Money amount) => vatGross[rate] = vatGross.GetValueOrDefault(rate, zero) + amount;
        foreach (var line in completed.SelectMany(o => o.Totals.VatSummary))
        {
            AddGross(line.Rate, line.Gross);
        }

        foreach (var refund in refunds)
        {
            var reduction = refund.SaleReductionOrAll;
            if (reduction.MinorUnits == 0 || !refundedOrders.TryGetValue(refund.OrderId, out var refunded) || refunded.VatSummary.Count == 0)
            {
                continue;
            }

            var shares = ProportionalAllocator.Allocate(reduction, [.. refunded.VatSummary.Select(v => v.Gross)]);
            for (var i = 0; i < shares.Count; i++)
            {
                AddGross(refunded.VatSummary[i].Rate, zero - shares[i]);
            }
        }

        var vat = vatGross.OrderByDescending(v => v.Key.BasisPoints).Select(v =>
        {
            var tax = VatCalculator.VatInGross(v.Value, v.Key);
            return new VatTotals(v.Key.Code, v.Key.Percent, v.Value, v.Value - tax, tax);
        }).ToList();

        var cancelled = orders.Where(o => o.Status == OrderStatus.Cancelled).ToList();
        var summary = new SalesSummary(
            completed.Count,
            lines.Sum(x => x.line.Pricing.Quantity),
            sales,
            completed.Count == 0 ? zero : new Money((long)Math.Round((decimal)sales.MinorUnits / completed.Count, MidpointRounding.AwayFromZero), currency),
            Sum(completed.Select(o => o.Totals.TotalSaving)),
            Sum(completed.Select(o => o.Totals.Discount?.AmountOff ?? zero)),
            Sum(completed.Select(o => o.Totals.Delivery?.Fee ?? zero)),
            Sum(completed.Select(o => o.Totals.ServiceCharge?.Amount ?? zero)),
            refundsForSale,
            sales - refundsForSale,
            cancelled.Count,
            Sum(cancelled.Select(o => o.Totals.Total)),
            orders.Sum(o => o.Lines.Where(l => l.IsVoided).Sum(l => l.Pricing.Quantity)));

        return new SalesReport
        {
            From = from, To = to, Summary = summary, Items = items, Categories = categories, Hours = hours, Days = days,
            Types = types, Staff = staff, Payments = methods, Vat = vat,
        };
    }
}
