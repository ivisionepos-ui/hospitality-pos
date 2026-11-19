using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Pricing;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>
/// More → Service charge: off, or on for eat-in orders at a chosen percentage. Optional for the customer (staff can
/// take it off), outside the scope of VAT, and never on takeaway, collection or delivery.
/// </summary>
public sealed class ServiceChargeTests : HubTestBase
{
    private static OrderLineRequest Doner(int quantity = 1) => new(TestMenu.Doner, TestMenu.DonerRegular, quantity, [TestMenu.Garlic]);

    private static CreateOrderRequest Order(OrderType type, bool? serviceCharge = null, int doners = 2) =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, type, null, null, [Doner(doners)], ServiceCharge: serviceCharge);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Manager.Put("/api/menu", TestMenu.State());
    }

    private async Task<OrderView> Place(CreateOrderRequest request)
    {
        var response = await Cashier.Post("/api/orders", request);
        response.EnsureSuccessStatusCode();
        return await response.Read<OrderView>();
    }

    private async Task SwitchOn(decimal percent = 12.5m) =>
        (await Owner.Put("/api/settings/service-charge", new ServiceChargeSettings(true, percent))).EnsureSuccessStatusCode();

    [Fact]
    public async Task A_new_shop_has_no_service_charge()
    {
        Assert.Equal(ServiceChargeSettings.Default, await Cashier.GetJson<ServiceChargeSettings>("/api/settings/service-charge"));
        var order = await Place(Order(OrderType.EatIn));
        Assert.Null(order.ServiceCharge);
        Assert.Equal(order.GoodsTotal, order.Total);
    }

    [Fact]
    public async Task When_on_eat_in_orders_get_the_percentage_outside_the_scope_of_VAT()
    {
        await SwitchOn(12.5m);

        var order = await Place(Order(OrderType.EatIn)); // 2 × £8.99 = £17.98

        Assert.Equal(Money.Gbp(225), order.ServiceCharge); // 12.5% of £17.98 = £2.2475 → £2.25
        Assert.Equal(12.5m, order.ServiceChargePercent);
        Assert.Equal(Money.Gbp(1798 + 225), order.Total);
        Assert.Equal(Money.Gbp(225), order.OutsideScopeOfVat); // optional: no VAT on it
        Assert.Equal(Money.Gbp(1798), order.Vat.Aggregate(Money.Gbp(0), (sum, v) => sum + v.Gross));

        // Printed as optional on the receipt.
        var saved = Host.Services.GetRequiredService<OrderService>().Get(order.Id);
        var receipt = Layouts.Receipt(saved, null, PrintSamples.Shop, TimeZoneInfo.Utc, "Sam", isCopy: false).PlainText;
        Assert.Contains("Optional service charge 12.5%", receipt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OrderType.Takeaway)]
    [InlineData(OrderType.Delivery)]
    public async Task Takeaway_collection_and_delivery_never_have_it(OrderType type)
    {
        await SwitchOn();
        var request = Order(type) with
        {
            Source = type == OrderType.Delivery ? OrderSource.Phone : OrderSource.Counter,
            Contact = type == OrderType.Delivery ? new OrderContact("Sam", "07700 900321", new DeliveryAddress("1 High Street", null, "Birmingham", "B12 9AA"), null) : null,
            Delivery = type == OrderType.Delivery ? new DeliveryRequest(1.0m) : null,
        };
        Assert.Null((await Place(request)).ServiceCharge);
        Assert.Null((await (await Cashier.Post("/api/orders/quote", request)).Read<OrderView>()).ServiceCharge);
    }

    [Fact]
    public async Task The_quote_shows_it_and_a_customer_can_decline_it_before_or_after_the_order_is_saved()
    {
        await SwitchOn(10m);
        Assert.Equal(Money.Gbp(180), (await (await Cashier.Post("/api/orders/quote", Order(OrderType.EatIn))).Read<OrderView>()).ServiceCharge);
        Assert.Null((await (await Cashier.Post("/api/orders/quote", Order(OrderType.EatIn, serviceCharge: false))).Read<OrderView>()).ServiceCharge);
        Assert.Null((await Place(Order(OrderType.EatIn, serviceCharge: false))).ServiceCharge);

        var order = await Place(Order(OrderType.EatIn));
        var removed = await (await Cashier.Post($"/api/orders/{order.Id}/service-charge", new OrderServiceChargeRequest(false))).Read<OrderView>();
        Assert.Null(removed.ServiceCharge);
        Assert.Equal(removed.GoodsTotal, removed.Total);
        var back = await (await Cashier.Post($"/api/orders/{order.Id}/service-charge", new OrderServiceChargeRequest(true))).Read<OrderView>();
        Assert.Equal(Money.Gbp(180), back.ServiceCharge);

        var history = Host.Services.GetRequiredService<OrderService>().Get(order.Id).History;
        Assert.Contains(history, e => e.Description == "Service charge removed");
    }

    [Fact]
    public async Task It_follows_items_added_later_and_discounts()
    {
        await SwitchOn(10m);
        var order = await Place(Order(OrderType.EatIn, doners: 1)); // £8.99 → £0.90

        var more = await (await Cashier.Post($"/api/orders/{order.Id}/lines", new AddLinesRequest([Doner()]))).Read<OrderView>();
        Assert.Equal(Money.Gbp(180), more.ServiceCharge); // £17.98 → £1.80

        var discounted = await (await Manager.Post($"/api/orders/{order.Id}/discount", new DiscountRequest(OrderDiscountType.Percent, 10, "Regular"))).Read<OrderView>();
        Assert.Equal(Money.Gbp(162), discounted.ServiceCharge); // on the food after the discount: £16.18 → £1.62
    }

    [Fact]
    public async Task Switching_it_off_leaves_open_orders_alone_but_new_ones_have_none()
    {
        await SwitchOn();
        var open = await Place(Order(OrderType.EatIn));
        (await Owner.Put("/api/settings/service-charge", new ServiceChargeSettings(false, 12.5m))).EnsureSuccessStatusCode();

        Assert.Null((await Place(Order(OrderType.EatIn))).ServiceCharge);
        Assert.NotNull(Host.Services.GetRequiredService<OrderService>().Get(open.Id).ServiceCharge);

        // It can be taken off, but not put back while switched off.
        var back = await Cashier.Post($"/api/orders/{open.Id}/service-charge", new OrderServiceChargeRequest(true));
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Cashier.Post($"/api/orders/{open.Id}/service-charge", new OrderServiceChargeRequest(false))).StatusCode);
        Assert.Equal(12.5m, (await Cashier.GetJson<ServiceChargeSettings>("/api/settings/service-charge")).Percent); // the rate is remembered
    }

    [Fact]
    public async Task Only_eat_in_orders_can_have_it_added()
    {
        await SwitchOn();
        var takeaway = await Place(Order(OrderType.Takeaway));
        Assert.Equal(HttpStatusCode.Conflict, (await Cashier.Post($"/api/orders/{takeaway.Id}/service-charge", new OrderServiceChargeRequest(true))).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(25.5)]
    [InlineData(12.345)]
    public async Task Rates_outside_0_to_25_percent_or_with_more_than_two_decimals_are_refused(double percent)
    {
        var response = await Owner.Put("/api/settings/service-charge", new ServiceChargeSettings(true, (decimal)percent));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_changes_it_and_it_survives_a_restart()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Put("/api/settings/service-charge", new ServiceChargeSettings(true, 10m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put("/api/settings/service-charge", new ServiceChargeSettings(true, 10m))).StatusCode);
        await SwitchOn(15m);

        Restart();

        Assert.Equal(new ServiceChargeSettings(true, 15m), await Cashier.GetJson<ServiceChargeSettings>("/api/settings/service-charge"));
    }
}
