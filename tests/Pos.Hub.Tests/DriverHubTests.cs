using System.Net;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Staff;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Security;

namespace Pos.Hub.Tests;

/// <summary>Drivers: sending deliveries out, bringing them back, taking the money, and each driver's day.</summary>
public sealed class DriverHubTests : HubTestBase
{
    private static readonly OrderContact Sam = new("Sam Patel", "07700 900123", new DeliveryAddress("12 Park Lane", null, "Leeds", "ls29jt"), null);

    private static OrderLineRequest Pizza => new(TestMenu.Margherita, TestMenu.Margherita12); // £11.99

    private async Task<StaffMember> AddDriver(string name, string pin) =>
        await (await Owner.Post("/api/staff", new AddStaffRequest(name, StaffRole.Driver, pin))).Read<StaffMember>();

    /// <summary>A delivery of two pizzas (£23.98 + £1.50 delivery = £25.48), sent to the kitchen.</summary>
    private async Task<OrderView> Delivery()
    {
        var order = await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Phone, OrderType.Delivery,
            null, null, [Pizza, Pizza], Sam, new DeliveryRequest(1.3m)))).Read<OrderView>();
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });
        return order;
    }

    private Task<HttpResponseMessage> SendOut(Guid orderId, Guid driverId) => Cashier.Post($"/api/orders/{orderId}/send-out", new SendOutRequest(driverId));

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Manager.Put("/api/menu", TestMenu.State());
        await Cashier.Post("/api/shifts/open", new OpenShiftRequest(5000));
    }

    [Fact]
    public async Task Drivers_are_listed_first_then_everyone_else_on_the_staff_list()
    {
        await AddDriver("Dan Driver", "6152");

        var list = await Cashier.GetJson<List<DriverView>>("/api/drivers");

        Assert.Equal("Dan Driver", list[0].Name);
        Assert.Contains(list, d => d.Name == "Mia Manager"); // managers sometimes deliver too
        Assert.Equal(HttpStatusCode.Forbidden, (await Kitchen.GetAsync(new Uri("/api/drivers", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task A_delivery_goes_out_and_every_till_sees_who_has_it()
    {
        var dan = await AddDriver("Dan Driver", "6152");
        var order = await Delivery();

        var response = await SendOut(order.Id, dan.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = await response.Read<OrderView>();
        Assert.Equal(OrderStatus.OutForDelivery, sent.Status);
        Assert.Equal(dan.Id, sent.DriverId);
        Assert.Equal(dan.Id, (await TillOnly.GetJson<OrderView>($"/api/orders/{order.Id}")).DriverId);
    }

    [Fact]
    public async Task Mistakes_are_explained()
    {
        var dan = await AddDriver("Dan Driver", "6152");
        var unsent = await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Phone, OrderType.Delivery,
            null, null, [Pizza, Pizza], Sam, new DeliveryRequest(1.3m)))).Read<OrderView>();
        var takeaway = await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway,
            null, null, [Pizza]))).Read<OrderView>();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendOut(unsent.Id, Guid.NewGuid())).StatusCode); // not on the staff list
        var notSent = await SendOut(unsent.Id, dan.Id);
        Assert.Equal(HttpStatusCode.Conflict, notSent.StatusCode);
        Assert.Equal($"Order {unsent.Number} has items not sent to the kitchen yet. Send it first.", (await notSent.Read<ErrorView>()).Message);
        Assert.Equal(HttpStatusCode.Conflict, (await SendOut(takeaway.Id, dan.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Cashier.Post($"/api/orders/{unsent.Id}/status", new StatusRequest(OrderStatus.OutForDelivery))).StatusCode); // needs a driver
    }

    [Fact]
    public async Task Brought_back_it_is_ready_in_the_shop_again()
    {
        var dan = await AddDriver("Dan Driver", "6152");
        var order = await Delivery();
        await SendOut(order.Id, dan.Id);

        var back = await (await Cashier.Post($"/api/orders/{order.Id}/back-to-shop", new BackToShopRequest("Nobody home"))).Read<OrderView>();

        Assert.Equal(OrderStatus.Ready, back.Status);
        Assert.Null(back.DriverId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cashier.Post($"/api/orders/{order.Id}/back-to-shop", new BackToShopRequest(" "))).StatusCode);
    }

    [Fact]
    public async Task A_removed_driver_cannot_be_given_deliveries()
    {
        var dan = await AddDriver("Dan Driver", "6152");
        await Owner.DeleteAsync(new Uri($"/api/staff/{dan.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, (await SendOut((await Delivery()).Id, dan.Id)).StatusCode);
    }

    [Fact]
    public async Task Each_drivers_day_shows_what_is_out_what_is_delivered_and_the_money_they_took()
    {
        var dan = await AddDriver("Dan Driver", "6152");
        var eve = await AddDriver("Eve Driver", "6153");
        var cash = await Delivery();
        var card = await Delivery();
        var prepaid = await Delivery();
        var stillOut = await Delivery();
        await Cashier.Post($"/api/orders/{prepaid.Id}/payments/card", new CardPaymentRequest(2548, null, "000001")); // paid by phone before going out

        Clock.Now += TimeSpan.FromMinutes(10);
        foreach (var o in new[] { cash, card, prepaid })
        {
            await SendOut(o.Id, dan.Id);
        }

        await SendOut(stillOut.Id, eve.Id);

        // Dan comes back: cash from one customer, card on his machine for the other, the third was already paid.
        Clock.Now += TimeSpan.FromMinutes(40);
        await Cashier.Post($"/api/orders/{cash.Id}/payments/cash", new CashPaymentRequest(2548));
        await Cashier.Post($"/api/orders/{card.Id}/payments/card", new CardPaymentRequest(2548, null, "000002"));
        foreach (var o in new[] { cash, card, prepaid })
        {
            Assert.Equal(HttpStatusCode.OK, (await Cashier.Post($"/api/orders/{o.Id}/status", new StatusRequest(OrderStatus.Completed))).StatusCode);
        }

        var day = await Cashier.GetJson<List<DriverDayView>>("/api/drivers/today");

        var d = day.Single(x => x.Name == "Dan Driver");
        Assert.Equal((0, 3, Money.Gbp(2548), Money.Gbp(2548)), (d.Out, d.Delivered, d.Cash, d.Card)); // the phone payment is not his
        var e = day.Single(x => x.Name == "Eve Driver");
        Assert.Equal((1, 0, Money.Gbp(2548)), (e.Out, e.Delivered, e.ToCollect));
    }

    [Fact]
    public async Task An_unpaid_delivery_cannot_be_marked_delivered()
    {
        var dan = await AddDriver("Dan Driver", "6152");
        var order = await Delivery();
        await SendOut(order.Id, dan.Id);

        var response = await Cashier.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Completed));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("£25.48 is still to pay.", (await response.Read<ErrorView>()).Message);
    }
}
