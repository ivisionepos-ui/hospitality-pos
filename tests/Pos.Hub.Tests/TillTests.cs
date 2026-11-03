using System.Net;
using System.Net.Http.Json;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Security;

namespace Pos.Hub.Tests;

/// <summary>Shifts, payments, refunds and the Z report through the hub API.</summary>
public sealed class TillTests : HubTestBase
{
    private static OrderLineRequest Doner => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]); // £8.99

    private static OrderLineRequest Cans(int quantity) => new(TestMenu.Can, TestMenu.CanRegular, quantity);       // £1.20 each

    private static CreateOrderRequest NewOrder(OrderSource source, params OrderLineRequest[] lines) =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), source, OrderType.Takeaway, null, null, lines);

    private async Task<OrderView> Order(params OrderLineRequest[] lines) =>
        await (await Cashier.Post("/api/orders", NewOrder(OrderSource.Counter, lines))).Read<OrderView>();

    private static async Task<HttpResponseMessage> WithManagerPin<T>(HttpClient client, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: PosJson.Options) };
        request.Headers.Add("X-Manager-Pin", TestShop.ManagerPin);
        return await client.SendAsync(request);
    }

    private async Task<PaymentResponse> Cash(Guid orderId, long tendered, long? amount = null) =>
        await (await Cashier.Post($"/api/orders/{orderId}/payments/cash", new CashPaymentRequest(tendered, amount))).Read<PaymentResponse>();

    private async Task<PaymentResponse> Card(Guid orderId, long amount, long? tip = null) =>
        await (await Cashier.Post($"/api/orders/{orderId}/payments/card", new CardPaymentRequest(amount, tip, "000123"))).Read<PaymentResponse>();

    private async Task Complete(Guid orderId) =>
        Assert.Equal(HttpStatusCode.OK, (await Cashier.Post($"/api/orders/{orderId}/status", new StatusRequest(OrderStatus.Completed))).StatusCode);

    private async Task Setup(long floatPence = 10000)
    {
        await Manager.Put("/api/menu", TestMenu.State());
        Assert.Equal(HttpStatusCode.OK, (await Cashier.Post("/api/shifts/open", new OpenShiftRequest(floatPence))).StatusCode);
    }

    [Fact]
    public async Task A_full_trading_day_reconciles_in_the_z_report()
    {
        await Setup();

        // 1. Doner + 2 cans = £11.39, £20 cash, £8.61 change
        var o1 = await Order(Doner, Cans(2));
        var cash = await Cash(o1.Id, 2000);
        Assert.Equal(Money.Gbp(861), cash.Payment.Change);
        Assert.Equal(Money.Gbp(0), cash.Order.Balance);
        await Complete(o1.Id);

        // 2. Doner £8.99 by card with a £1 tip
        var o2 = await Order(Doner);
        var card = await Card(o2.Id, 899, tip: 100);
        await Complete(o2.Id);

        // 3. Two doners split between two people: one card, one cash
        var o3 = await Order(Doner, Doner);
        var shares = await Cashier.GetJson<SplitView>($"/api/orders/{o3.Id}/split?parts=2");
        Assert.Equal([Money.Gbp(899), Money.Gbp(899)], shares.Shares);
        await Card(o3.Id, 899);
        Assert.Equal(Money.Gbp(101), (await (await Cashier.Post($"/api/orders/{o3.Id}/payments/cash", new CashPaymentRequest(1000, 899))).Read<PaymentResponse>()).Payment.Change);
        await Complete(o3.Id);

        // 4. £2.00 refunded on order 2 (needs a manager), £5 paid out for milk (needs a manager)
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Post($"/api/orders/{o2.Id}/payments/{card.Payment.Id}/refund", new RefundRequest(200, "Late"))).StatusCode);
        var refund = await (await WithManagerPin(Cashier, $"/api/orders/{o2.Id}/payments/{card.Payment.Id}/refund", new RefundRequest(200, "Late"))).Read<PaymentResponse>();
        Assert.Equal(Money.Gbp(-200), refund.Payment.Amount);
        Assert.Equal(PaymentMethod.Card, refund.Payment.Method);
        Assert.Equal(Shop.ManagerId, refund.Payment.ApprovedByUserId);
        Assert.Equal(HttpStatusCode.OK, (await WithManagerPin(Cashier, "/api/shifts/paid-out", new CashMovementRequest(500, "Milk"))).StatusCode);

        // 5. X report (managers only) during the shift
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.GetAsync(new Uri("/api/shifts/current/x-report", UriKind.Relative))).StatusCode);
        var x = await Manager.GetJson<ShiftSummary>("/api/shifts/current/x-report");
        Assert.Equal(Money.Gbp(10000 + 1139 + 899 - 500), x.ExpectedCash);

        // 6. Close with a blind count: £115.00 counted against £115.38 expected
        var closed = await (await Cashier.Post("/api/shifts/close", new CloseShiftRequest(11500))).Read<ShiftSummary>();
        Assert.Equal(Money.Gbp(11538), closed.ExpectedCash);
        Assert.Equal(Money.Gbp(-38), closed.Variance);

        // 7. Z report
        var z = await (await Manager.Post("/api/reports/z", new ZReportRequest(null))).Read<DayReport>();
        Assert.Equal(1, z.ZNumber);
        Assert.Equal(3, z.Sales.Orders);
        Assert.Equal(Money.Gbp(1139 + 899 + 1798), z.Sales.Total);
        var net = z.Payments.Aggregate(Money.Gbp(0), (s, p) => s + p.Net);
        Assert.Equal(Money.Gbp(1139 + 899 + 1798 - 200), net);
        Assert.Equal(net, Assert.Single(z.Vat).Gross);              // all 20%: VAT-able sales equal money kept
        Assert.Equal(Money.Gbp(606), z.Vat[0].Vat);                // £36.36 incl. VAT -> £6.06
        Assert.Equal(Money.Gbp(100), Assert.Single(z.TipsByStaff).Tips);
        Assert.Equal(Shop.CashierId, z.TipsByStaff[0].UserId);
        Assert.Equal(Money.Gbp(-38), z.CashVariance);
        Assert.Equal(1, z.Exceptions.Refunds);

        // 8. Taking it again gives the same report; no new shifts today
        Assert.Equal(1, (await (await Manager.Post("/api/reports/z", new ZReportRequest(null))).Read<DayReport>()).ZNumber);
        Assert.Equal(HttpStatusCode.Conflict, (await Cashier.Post("/api/shifts/open", new OpenShiftRequest(0))).StatusCode);
    }

    [Fact]
    public async Task Z_report_waits_for_every_shift_to_close()
    {
        await Setup();

        var response = await Manager.Post("/api/reports/z", new ZReportRequest(null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Close every till shift", (await response.Read<ErrorView>()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Order_cannot_be_completed_until_paid_or_cancelled_until_refunded()
    {
        await Setup();
        var order = await Order(Doner);
        var url = $"/api/orders/{order.Id}/status";

        var unpaid = await Cashier.Post(url, new StatusRequest(OrderStatus.Completed));
        Assert.Equal(HttpStatusCode.Conflict, unpaid.StatusCode);
        Assert.Contains("still to pay", (await unpaid.Read<ErrorView>()).Message, StringComparison.Ordinal);

        var payment = await Card(order.Id, 500); // part paid
        var cancel = await Manager.Post(url, new StatusRequest(OrderStatus.Cancelled, "Customer left"));
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Contains("Refund", (await cancel.Read<ErrorView>()).Message, StringComparison.Ordinal);

        await Manager.Post($"/api/orders/{order.Id}/payments/{payment.Payment.Id}/refund", new RefundRequest(500, "Customer left"));
        Assert.Equal(HttpStatusCode.OK, (await Manager.Post(url, new StatusRequest(OrderStatus.Cancelled, "Customer left"))).StatusCode);
    }

    [Fact]
    public async Task Removing_an_item_after_payment_means_a_refund_before_completing()
    {
        await Setup();
        var order = await Order(Doner, Cans(1));
        var paid = await Card(order.Id, 1019);
        await Cashier.Post($"/api/orders/{order.Id}/lines/{order.Lines[1].Id}/void", new VoidRequest("No cans left"));

        var payments = await Cashier.GetJson<PaymentsView>($"/api/orders/{order.Id}/payments");
        Assert.Equal(Money.Gbp(-120), payments.Balance);
        var blocked = await Cashier.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Completed));
        Assert.Contains("must be refunded", (await blocked.Read<ErrorView>()).Message, StringComparison.Ordinal);

        await WithManagerPin(Cashier, $"/api/orders/{order.Id}/payments/{paid.Payment.Id}/refund", new RefundRequest(120, "Can not available"));
        await Complete(order.Id);
    }

    [Fact]
    public async Task Overpaying_is_refused_and_a_resent_payment_is_taken_once()
    {
        await Setup();
        var order = await Order(Doner);

        Assert.Equal(HttpStatusCode.Conflict, (await Cashier.Post($"/api/orders/{order.Id}/payments/card", new CardPaymentRequest(900))).StatusCode);

        var request = new CardPaymentRequest(899, PaymentId: DeviceIds.New());
        var first = await (await Cashier.Post($"/api/orders/{order.Id}/payments/card", request)).Read<PaymentResponse>();
        var again = await (await Cashier.Post($"/api/orders/{order.Id}/payments/card", request)).Read<PaymentResponse>();

        Assert.False(first.Duplicate);
        Assert.True(again.Duplicate);
        Assert.Equal(first.Payment.Id, again.Payment.Id);
        Assert.Single((await Cashier.GetJson<PaymentsView>($"/api/orders/{order.Id}/payments")).Payments);
    }

    [Fact]
    public async Task Split_by_items_gives_the_amount_for_chosen_items()
    {
        await Setup();
        var order = await Order(Doner, Cans(2));

        var split = await (await Cashier.Post($"/api/orders/{order.Id}/split-by-items", new SplitByItemsRequest([order.Lines[1].Id]))).Read<SplitView>();

        Assert.Equal(Money.Gbp(240), Assert.Single(split.Shares));
        Assert.Equal(HttpStatusCode.NotFound, (await Cashier.Post($"/api/orders/{order.Id}/split-by-items", new SplitByItemsRequest([Guid.NewGuid()]))).StatusCode);
    }

    [Fact]
    public async Task Payments_need_an_open_shift_on_a_till()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var order = await Order(Doner);

        var noShift = await Cashier.Post($"/api/orders/{order.Id}/payments/card", new CardPaymentRequest(899));
        Assert.Equal(HttpStatusCode.Conflict, noShift.StatusCode);
        Assert.Contains("Open a shift", (await noShift.Read<ErrorView>()).Message, StringComparison.Ordinal);

        var code = await (await Owner.Post("/api/devices/pairing-code", new { })).Read<PairingCodeView>();
        var kitchenScreen = await (await Anonymous.Post("/api/devices/pair", new PairRequest(code.Code, "Kitchen", DeviceType.KitchenScreen))).Read<PairedDevice>();
        using var managerOnKitchen = ClientFor(await Host.Login(kitchenScreen, TestShop.ManagerPin));
        Assert.Equal("NotATill", (await (await managerOnKitchen.Post("/api/shifts/open", new OpenShiftRequest(0))).Read<ErrorView>()).Code);
    }

    [Fact]
    public async Task One_shift_at_a_time_per_till()
    {
        await Setup();

        Assert.Equal(HttpStatusCode.Conflict, (await Cashier.Post("/api/shifts/open", new OpenShiftRequest(0))).StatusCode);
        Assert.Equal(Money.Gbp(10000), (await Cashier.GetJson<Shift>("/api/shifts/current")).OpeningFloat);
    }

    [Fact]
    public async Task Yesterdays_open_shift_must_be_closed_first()
    {
        await Setup();
        await Cashier.Post("/api/shifts/close", new CloseShiftRequest(10000));
        await Cashier.Post("/api/shifts/open", new OpenShiftRequest(5000));

        Clock.Now = Clock.Now.AddDays(1).AddHours(-6); // next morning, still logged in
        var next = ClientFor(await Host.Login(new PairedDevice(Shop.Till.DeviceId, Shop.Till.DeviceKey), TestShop.CashierPin));
        var response = await next.Post("/api/shifts/open", new OpenShiftRequest(5000));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("still open", (await response.Read<ErrorView>()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paid_in_and_out_need_a_manager_and_cannot_overdraw_the_drawer()
    {
        await Setup(floatPence: 2000);

        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Post("/api/shifts/paid-in", new CashMovementRequest(1000, "Change"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WithManagerPin(Cashier, "/api/shifts/paid-in", new CashMovementRequest(1000, "Change"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Manager.Post("/api/shifts/paid-out", new CashMovementRequest(3001, "Too much"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Manager.Post("/api/shifts/paid-out", new CashMovementRequest(3000, "Safe drop"))).StatusCode);
    }

    [Fact]
    public async Task Shift_and_payments_survive_a_restart()
    {
        await Setup();
        var order = await Order(Doner, Cans(1));
        await Card(order.Id, 500);

        Restart();

        var paid = await Cash(order.Id, 1000);
        Assert.Equal(Money.Gbp(481), paid.Payment.Change);
        Assert.Equal(Money.Gbp(0), paid.Order.Balance);
        Assert.Equal(Money.Gbp(10519), (await Manager.GetJson<ShiftSummary>("/api/shifts/current/x-report")).ExpectedCash);
    }
}
