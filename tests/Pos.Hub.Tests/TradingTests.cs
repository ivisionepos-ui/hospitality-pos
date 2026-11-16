using System.Net;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Reports;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Delivery;
using Pos.Hub.Live;

namespace Pos.Hub.Tests;

/// <summary>Clearing test sales before going live, and the one-way switch to live trading.</summary>
public sealed class TradingTests : HubTestBase
{
    private static OrderLineRequest Doner => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]); // £8.99

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Manager.Put("/api/menu", TestMenu.State());
        await Cashier.Post("/api/shifts/open", new OpenShiftRequest(5000));
    }

    private async Task<OrderView> Sold(OrderContact? contact = null)
    {
        var order = await (await Cashier.Post("/api/orders", new CreateOrderRequest(
            DeviceIds.New(), Guid.NewGuid().ToString(), contact is null ? OrderSource.Counter : OrderSource.Phone, OrderType.Takeaway, null, null, [Doner], contact))).Read<OrderView>();
        await Cashier.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(order.Total.MinorUnits));
        await Cashier.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Completed));
        return order;
    }

    private static Task<HttpResponseMessage> Clear(HttpClient who, string confirm = "CLEAR", bool alsoCustomers = false) =>
        who.Post("/api/settings/trading/clear-test-data", new ClearTestDataRequest(confirm, alsoCustomers));

    [Fact]
    public async Task A_new_shop_is_testing()
    {
        var status = await Owner.GetJson<TradingStatus>("/api/settings/trading");
        Assert.False(status.Live);
        Assert.Empty(status.Cleared);
    }

    [Fact]
    public async Task Clearing_starts_sales_numbers_and_reports_from_zero_and_keeps_the_menu_staff_and_customers()
    {
        var first = await Sold(new OrderContact("Amira", "07700900123", null, null));
        await Sold();
        await Cashier.Post("/api/shifts/close", new CloseShiftRequest(5000 + 1798));
        var z = await (await Manager.Post("/api/reports/z", new ZReportRequest(null))).Read<DayReport>();
        Assert.Equal(1, z.ZNumber);

        var response = await Clear(Owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cleared = await response.Read<TestDataCleared>();
        Assert.Equal((2L, 2L, 0L), (cleared.Orders, cleared.Payments, cleared.Customers));
        Assert.NotNull(cleared.Backup);
        Assert.True(File.Exists(Path.Combine(Host.BackupFolder, cleared.Backup!))); // everything removed is in this backup

        Assert.Empty(await Owner.GetJson<List<OrderView>>("/api/orders"));
        var sales = await Manager.GetJson<SalesReport>("/api/reports/sales?from=2026-11-05");
        Assert.Equal(0, sales.Summary.Orders);

        // Menu, staff and the customer are still there; the customer's test orders are not counted.
        var customer = Assert.Single(await Cashier.GetJson<List<CustomerRecord>>("/api/customers?phone=07700900123"));
        Assert.Equal((0, (DateTimeOffset?)null), (customer.OrderCount, customer.LastOrderAt));

        // Everything starts again: a new till session, order number 001, Z report 1.
        Assert.Equal(HttpStatusCode.OK, (await Cashier.Post("/api/shifts/open", new OpenShiftRequest(5000))).StatusCode);
        var next = await Sold();
        Assert.Equal(first.Number, next.Number);
        await Cashier.Post("/api/shifts/close", new CloseShiftRequest(5000 + 899));
        Assert.Equal(1, (await (await Manager.Post("/api/reports/z", new ZReportRequest(null))).Read<DayReport>()).ZNumber);

        var status = await Owner.GetJson<TradingStatus>("/api/settings/trading");
        Assert.Equal("Olivia Owner", Assert.Single(status.Cleared).ByName);
    }

    [Fact]
    public async Task Customers_can_be_cleared_too()
    {
        await Sold(new OrderContact("Amira", "07700900123", null, null));

        var cleared = await (await Clear(Owner, alsoCustomers: true)).Read<TestDataCleared>();

        Assert.Equal(1, cleared.Customers);
        Assert.Empty(await Cashier.GetJson<List<CustomerRecord>>("/api/customers?phone=07700900123"));
    }

    [Fact]
    public async Task Screens_are_told_to_start_again()
    {
        await Sold();
        var before = (await Owner.GetJson<CatchUpResult>("/api/live/events?since=0")).Events.Max(e => e.Seq);

        await Clear(Owner);

        var events = (await Owner.GetJson<CatchUpResult>($"/api/live/events?since={before}")).Events;
        var cleared = Assert.Single(events);
        Assert.Equal(LiveEventKinds.SalesCleared, cleared.Kind);
        Assert.True(cleared.Seq > before); // numbers keep going up, so screens never miss it
        Assert.Contains(LiveEventKinds.SalesCleared, (await Kitchen.GetJson<CatchUpResult>($"/api/live/events?since={before}")).Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task Only_the_owner_can_clear_and_must_type_CLEAR()
    {
        await Sold();

        Assert.Equal(HttpStatusCode.Forbidden, (await Clear(Manager)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Clear(Cashier)).StatusCode);
        var wrong = await Clear(Owner, "clear it");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("Type CLEAR to confirm.", (await wrong.Read<ErrorView>()).Message);

        Assert.Single(await Owner.GetJson<List<OrderView>>("/api/orders"));
    }

    [Fact]
    public async Task After_going_live_no_sale_can_be_deleted_even_after_a_restart()
    {
        await Sold();
        Assert.Equal(HttpStatusCode.BadRequest, (await Owner.Post("/api/settings/trading/go-live", new GoLiveRequest("yes"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Post("/api/settings/trading/go-live", new GoLiveRequest("GO LIVE"))).StatusCode);

        var live = await (await Owner.Post("/api/settings/trading/go-live", new GoLiveRequest("GO LIVE"))).Read<TradingStatus>();
        Assert.True(live.Live);
        Assert.Equal("Olivia Owner", live.WentLiveBy);

        Restart();
        var refused = await Clear(Owner);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("six years", (await refused.Read<ErrorView>()).Message, StringComparison.Ordinal);
        Assert.Single(await Owner.GetJson<List<OrderView>>("/api/orders"));
        Assert.True((await Owner.GetJson<TradingStatus>("/api/settings/trading")).Live);
    }
}
