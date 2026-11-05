using System.Net;
using System.Net.Http.Json;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Pricing;
using Pos.Domain.Tax;
using Pos.Hub.Api;

namespace Pos.Hub.Tests;

/// <summary>Discounts on the till: the owner's limits, manager approval above them, and money already paid.</summary>
public sealed class DiscountTests : HubTestBase
{
    private static OrderLineRequest Doner => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]); // £8.99

    private static OrderLineRequest Cans(int quantity) => new(TestMenu.Can, TestMenu.CanRegular, quantity);       // £1.20 each

    private async Task<OrderView> Order(params OrderLineRequest[] lines)
    {
        await Manager.Put("/api/menu", TestMenu.State());
        return await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, lines))).Read<OrderView>();
    }

    private static async Task<HttpResponseMessage> WithManagerPin<T>(HttpClient client, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: PosJson.Options) };
        request.Headers.Add("X-Manager-Pin", TestShop.ManagerPin);
        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Limits(int percent, long pence) => Owner.Put("/api/settings/discounts", new DiscountLimits(percent, pence));

    private static DiscountRequest Percent(int percent, string reason = "Regular customer") => new(OrderDiscountType.Percent, percent, reason);

    [Fact]
    public async Task With_limits_of_nothing_every_discount_needs_a_manager()
    {
        Assert.Equal(new DiscountLimits(10, 500), await Cashier.GetJson<DiscountLimits>("/api/settings/discounts")); // from the settings file until the owner saves
        await Limits(0, 0); // the shipped settings file: every discount needs a manager
        var order = await Order(Doner);

        var response = await Cashier.Post($"/api/orders/{order.Id}/discount", Percent(5));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Read<ErrorView>();
        Assert.Equal("ApprovalRequired", error.Code);
        Assert.Equal("Staff need a manager for every discount. Ask a manager to enter their PIN.", error.Message);
    }

    [Fact]
    public async Task Within_the_limits_a_cashier_gives_a_discount_alone_and_it_is_on_the_order()
    {
        await Limits(10, 500);
        var order = await Order(Doner, Cans(1)); // £10.19

        var response = await Cashier.Post($"/api/orders/{order.Id}/discount", Percent(10, " Regular customer "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var discounted = await response.Read<OrderView>();
        Assert.Equal(Money.Gbp(102), discounted.DiscountAmount); // 10% of £10.19 = 101.9p → 102p
        Assert.Equal(Money.Gbp(917), discounted.Total);
        Assert.Equal(Money.Gbp(917), discounted.Balance);
        Assert.Equal("Regular customer", discounted.DiscountReason);
    }

    [Fact]
    public async Task Above_the_limits_a_manager_approves_with_their_pin_and_is_recorded()
    {
        await Limits(10, 500);
        var order = await Order(Doner, Cans(1));

        var alone = await Cashier.Post($"/api/orders/{order.Id}/discount", Percent(20, "Complaint"));
        Assert.Equal(HttpStatusCode.Forbidden, alone.StatusCode);
        Assert.Equal("Staff need a manager for discounts over 10% or £5.00. Ask a manager to enter their PIN.", (await alone.Read<ErrorView>()).Message);

        // £6 off is within 10%? No: it is over £5, so it needs a manager too.
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Post($"/api/orders/{order.Id}/discount", new DiscountRequest(OrderDiscountType.Amount, 600, "Complaint"))).StatusCode);

        var approved = await WithManagerPin(Cashier, $"/api/orders/{order.Id}/discount", Percent(20, "Complaint"));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(Money.Gbp(204), (await approved.Read<OrderView>()).DiscountAmount);

        var history = await Manager.GetStringAsync(new Uri($"/api/orders/{order.Id}/history", UriKind.Relative));
        Assert.Contains("Complaint", history, StringComparison.Ordinal);
        Assert.Contains(Shop.ManagerId.ToString(), history, StringComparison.Ordinal); // the approving manager
    }

    [Fact]
    public async Task A_manager_gives_any_discount_without_a_second_pin()
    {
        var order = await Order(Doner);

        Assert.Equal(HttpStatusCode.OK, (await Manager.Post($"/api/orders/{order.Id}/discount", Percent(100, "Staff meal"))).StatusCode);
    }

    [Fact]
    public async Task Removing_a_discount_puts_the_price_back()
    {
        await Limits(10, 500);
        var order = await Order(Doner);
        await Cashier.Post($"/api/orders/{order.Id}/discount", Percent(10));

        var restored = await (await Cashier.DeleteAsync(new Uri($"/api/orders/{order.Id}/discount", UriKind.Relative))).Read<OrderView>();

        Assert.Null(restored.DiscountAmount);
        Assert.Equal(Money.Gbp(899), restored.Total);
    }

    [Fact]
    public async Task A_discount_cannot_take_the_total_below_what_was_already_paid()
    {
        await Limits(50, 5000);
        await Cashier.Post("/api/shifts/open", new OpenShiftRequest(10000));
        var order = await Order(Doner, Cans(1)); // £10.19
        await Cashier.Post($"/api/orders/{order.Id}/payments/card", new CardPaymentRequest(800, null, "000123"));

        var tooMuch = await Cashier.Post($"/api/orders/{order.Id}/discount", Percent(50)); // total would be £5.10, £8.00 paid
        var fine = await Cashier.Post($"/api/orders/{order.Id}/discount", Percent(20));    // £8.15: still £0.15 to pay

        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("£8.00 has already been paid, more than the new total. Give the money back as a refund instead.", (await tooMuch.Read<ErrorView>()).Message);
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        Assert.Equal(Money.Gbp(15), (await fine.Read<OrderView>()).Balance);
    }

    [Theory]
    [InlineData(OrderDiscountType.Percent, 0, "Regular", "The percentage must be 1 to 100.")]
    [InlineData(OrderDiscountType.Percent, 101, "Regular", "The percentage must be 1 to 100.")]
    [InlineData(OrderDiscountType.Amount, 0, "Regular", "Enter the amount off.")]
    [InlineData(OrderDiscountType.Percent, 10, " ", "Choose or type a reason for the discount.")]
    public async Task Mistakes_are_explained(OrderDiscountType type, long value, string reason, string message)
    {
        var order = await Order(Doner);

        var response = await Manager.Post($"/api/orders/{order.Id}/discount", new DiscountRequest(type, value, reason));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, (await response.Read<ErrorView>()).Message);
    }

    [Fact]
    public async Task A_finished_order_cannot_be_discounted()
    {
        var order = await Order(Doner);
        await Manager.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Cancelled, "Test"));

        Assert.Equal(HttpStatusCode.Conflict, (await Manager.Post($"/api/orders/{order.Id}/discount", Percent(10))).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_sets_the_limits_and_silly_values_are_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put("/api/settings/discounts", new DiscountLimits(50, 5000))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Limits(150, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Limits(10, 1_000_000)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Limits(15, 750)).StatusCode);
        Restart();
        Assert.Equal(new DiscountLimits(15, 750), await Cashier.GetJson<DiscountLimits>("/api/settings/discounts")); // kept, and every till can read them
    }
}
