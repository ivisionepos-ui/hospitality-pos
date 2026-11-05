using System.Globalization;
using System.Text;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Reports;
using Pos.Hub.Security;

namespace Pos.Hub;

/// <summary>Back-office sales reports for any range of trading days, and their spreadsheet (CSV) files.</summary>
public sealed class SalesReportService(OrderService orders, TillService tills, MenuService menu, AccessService access, HubSettings settings)
{
    /// <summary>Longest range in one report.</summary>
    public const int MaxDays = 366;

    /// <summary>Tables that can be saved as a spreadsheet.</summary>
    public static readonly IReadOnlyList<string> Tables = ["summary", "items", "categories", "hours", "days", "types", "staff", "payments", "vat"];

    /// <summary>Builds the report for the trading days from–to.</summary>
    public SalesReport Build(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ArgumentException("The end date is before the start date.");
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxDays)
        {
            throw new ArgumentException($"Choose at most {MaxDays} days.");
        }

        var dayOrders = orders.ForDays(from, to);
        var payments = tills.PaymentsForDays(from, to);
        // Refunds can be for orders of earlier days; cancelled or rejected orders were never sales.
        var refunded = payments.Where(p => p.IsRefund).Select(p => p.OrderId).Distinct().Select(orders.Get)
            .Where(o => o.Status is not (OrderStatus.Cancelled or OrderStatus.Rejected))
            .ToDictionary(o => o.Id, o => o.Totals);
        var categories = menu.Read(m => m.Categories.ToDictionary(c => c.Id, c => c.Name.En));
        var names = new Dictionary<Guid, string>();
        return SalesReport.Build(from, to, dayOrders, payments, refunded, settings.TimeZone,
            id => categories.GetValueOrDefault(id, "Other"),
            id => names.TryGetValue(id, out var name) ? name : names[id] = access.StaffName(id));
    }

    /// <summary>One table of the report as CSV (opens in Excel), with a safe file name.</summary>
    public (string FileName, string Csv) Csv(SalesReport report, string table)
    {
        ArgumentNullException.ThrowIfNull(report);
        static string M(Money m) => (m.MinorUnits / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        IEnumerable<string[]> rows = (table ?? string.Empty).ToLowerInvariant() switch
        {
            "summary" => Summary(report.Summary),
            "items" => Rows("Item", "Quantity", report.Items),
            "categories" => Rows("Category", "Quantity", report.Categories),
            "hours" => Rows("Hour", "Orders", report.Hours),
            "days" => Rows("Day", "Orders", report.Days),
            "types" => Rows("Type", "Orders", report.Types),
            "staff" => [["Staff", "Orders", "Sales", "Discounts", "Discount value", "Items removed", "Refunds", "Refunded"],
                .. report.Staff.Select(s => new[] { s.Name, N(s.Orders), M(s.Sales), N(s.Discounts), M(s.DiscountValue), N(s.ItemsRemoved), N(s.Refunds), M(s.RefundedValue) })],
            "payments" => [["Method", "Taken", "Refunded", "Net", "Tips"],
                .. report.Payments.Select(p => new[] { p.Method.ToString(), M(p.Taken), M(p.Refunded), M(p.Net), M(p.Tips) })],
            "vat" => [["VAT rate", "Percent", "Gross", "Net", "VAT"],
                .. report.Vat.Select(v => new[] { v.Code, v.Percent.ToString(CultureInfo.InvariantCulture), M(v.Gross), M(v.Net), M(v.Vat) })],
            _ => throw new ArgumentException($"Unknown table. Choose one of: {string.Join(", ", Tables)}."),
        };

        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(string.Join(',', row.Select(Cell))).Append("\r\n");
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"sales-{table!.ToLowerInvariant()}-{report.From:yyyy-MM-dd}-to-{report.To:yyyy-MM-dd}.csv");
        return (name, text.ToString());

        static IEnumerable<string[]> Rows(string what, string quantity, IEnumerable<SalesRow> rows) =>
            [[what, quantity, "Sales"], .. rows.Select(r => new[] { r.Name, N(r.Quantity), M(r.Sales) })];

        static IEnumerable<string[]> Summary(SalesSummary s) =>
        [
            ["Figure", "Value"], ["Orders", N(s.Orders)], ["Items sold", N(s.ItemsSold)], ["Sales", M(s.Sales)], ["Average order", M(s.AverageOrder)],
            ["Offer savings", M(s.OfferSavings)], ["Discounts", M(s.Discounts)], ["Delivery charges", M(s.DeliveryFees)], ["Service charges", M(s.ServiceCharges)],
            ["Refunds", M(s.Refunds)], ["Net sales", M(s.NetSales)], ["Cancelled orders", N(s.CancelledOrders)], ["Cancelled value", M(s.CancelledValue)],
            ["Items removed", N(s.VoidedItems)],
        ];
    }

    /// <summary>One CSV cell. A cell starting with = + - @ (or a tab or return) could run as a formula in Excel, so it is made plain text.</summary>
    // A cell starting with = + - @ (or a tab or return) could run as a formula in Excel: it is made plain text.
    // Commas, quotes and line breaks are quoted.
    public static string Cell(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0], StringComparison.Ordinal) && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            v = "'" + v;
        }

        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : v;
    }
}
