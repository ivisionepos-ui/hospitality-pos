using System.Net;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Reports;
using Pos.Domain.Tax;
using Pos.Hub.Api;

namespace Pos.Hub.Tests;

/// <summary>Back-office sales reports through the hub.</summary>
public sealed class SalesReportHubTests : HubTestBase
{
    private static OrderLineRequest Doner => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]); // £8.99

    private static OrderLineRequest Cans(int quantity) => new(TestMenu.Can, TestMenu.CanRegular, quantity);       // £1.20 each

    private static readonly DateOnly Day1 = new(2026, 11, 5); // the test clock starts at 18:00 on 5 November

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Manager.Put("/api/menu", TestMenu.State());
        await Cashier.Post("/api/shifts/open", new OpenShiftRequest(5000));
    }

    private async Task<OrderView> Sold(params OrderLineRequest[] lines)
    {
        var order = await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, lines))).Read<OrderView>();
        await Cashier.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(order.Total.MinorUnits));
        await Cashier.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Completed));
        return order;
    }

    [Fact]
    public async Task A_range_of_days_shows_each_day_with_staff_names_and_categories()
    {
        await Sold(Doner, Cans(2));                  // day 1: £11.39
        Clock.Now += TimeSpan.FromHours(11);         // 05:00 next morning: a new trading day (logins last 12 hours)
        await Sold(Doner);                           // day 2: £8.99

        var both = await Manager.GetJson<SalesReport>("/api/reports/sales?from=2026-11-05&to=2026-11-06");
        var second = await Manager.GetJson<SalesReport>("/api/reports/sales?from=2026-11-06&to=2026-11-06");

        Assert.Equal([new SalesRow("2026-11-05", 1, Money.Gbp(1139)), new SalesRow("2026-11-06", 1, Money.Gbp(899))], both.Days);
        Assert.Equal((2, Money.Gbp(2038)), (both.Summary.Orders, both.Summary.Sales));
        Assert.Equal("Sam Cashier", Assert.Single(both.Staff).Name);
        Assert.Equal(Money.Gbp(2038), both.Payments.Single(p => p.Method == PaymentMethod.Cash).Net);
        Assert.Contains(both.Categories, c => c.Name == "Kebabs" && c.Quantity == 2);
        Assert.Equal(1, second.Summary.Orders);
    }

    [Fact]
    public async Task Refunds_and_vat_match_the_money_kept()
    {
        var order = await Sold(Doner, Cans(1)); // £10.19
        var cash = (await Cashier.GetJson<PaymentsView>($"/api/orders/{order.Id}/payments")).Payments[0];
        await Manager.Post($"/api/orders/{order.Id}/payments/{cash.Id}/refund", new RefundRequest(219, "Cold chips"));

        var report = await Manager.GetJson<SalesReport>($"/api/reports/sales?from={Day1:yyyy-MM-dd}");

        Assert.Equal((Money.Gbp(1019), Money.Gbp(219), Money.Gbp(800)), (report.Summary.Sales, report.Summary.Refunds, report.Summary.NetSales));
        Assert.Equal(Money.Gbp(800), Assert.Single(report.Vat).Gross);
    }

    [Fact]
    public async Task Only_managers_and_owners_see_reports_and_silly_ranges_are_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.GetAsync(new Uri("/api/reports/sales", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Manager.GetAsync(new Uri("/api/reports/sales", UriKind.Relative))).StatusCode);

        var backwards = await Manager.GetAsync(new Uri("/api/reports/sales?from=2026-11-05&to=2026-11-01", UriKind.Relative));
        var tooLong = await Manager.GetAsync(new Uri("/api/reports/sales?from=2025-01-01&to=2026-11-05", UriKind.Relative));
        Assert.Equal("The end date is before the start date.", (await backwards.Read<ErrorView>()).Message);
        Assert.Equal("Choose at most 366 days.", (await tooLong.Read<ErrorView>()).Message);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manager.GetAsync(new Uri("/api/reports/sales.csv?table=passwords", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task A_table_saves_as_a_spreadsheet_file()
    {
        await Sold(Doner, Cans(2));

        var response = await Manager.GetAsync(new Uri($"/api/reports/sales.csv?from={Day1:yyyy-MM-dd}&table=items", UriKind.Relative));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("sales-items-2026-11-05-to-2026-11-05.csv", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // so Excel shows £ correctly
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Item,Quantity,Sales", lines[0]);
        Assert.Contains("Can,2,2.40", lines);
    }

    [Theory]
    [InlineData("Chips", "Chips")]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+44 7700", "'+44 7700")]
    [InlineData("-2.50", "-2.50")]            // a real negative number stays a number
    [InlineData("Fish, chips", "\"Fish, chips\"")]
    public void Spreadsheet_cells_never_run_as_formulas(string value, string cell)
    {
        Assert.Equal(cell, SalesReportService.Cell(value));
    }
}
