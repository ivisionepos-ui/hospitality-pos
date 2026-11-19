using System.Net;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;

namespace Pos.Hub.Tests;

/// <summary>"Same as last time" for regular customers, and the till's Popular tab.</summary>
public sealed class RepeatOrderTests : HubTestBase
{
    private static readonly OrderContact Amira = new("Amira", "07700 900123", null, null);

    private static OrderLineRequest Doner(int quantity = 1, string? note = null) => new(TestMenu.Doner, TestMenu.DonerRegular, quantity, [TestMenu.Garlic], null, note);

    private static OrderLineRequest Can(int quantity = 1) => new(TestMenu.Can, TestMenu.CanRegular, quantity);

    private static OrderLineRequest MealDeal => new(TestMenu.Deal, TestMenu.DealRegular, 1, Choices:
    [
        new DealChoiceRequest(TestMenu.PizzaSlot, TestMenu.Margherita, TestMenu.Margherita12),
        new DealChoiceRequest(TestMenu.KebabSlot, TestMenu.Doner, TestMenu.DonerRegular, [TestMenu.Garlic]),
        new DealChoiceRequest(TestMenu.DrinkSlot, TestMenu.Can, TestMenu.CanRegular),
    ]);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Manager.Put("/api/menu", TestMenu.State());
    }

    private async Task<OrderView> Order(OrderContact? contact, params OrderLineRequest[] lines) =>
        await (await Cashier.Post("/api/orders", new CreateOrderRequest(
            Pos.Domain.Orders.DeviceIds.New(), Guid.NewGuid().ToString(), contact is null ? OrderSource.Counter : OrderSource.Phone, OrderType.Takeaway, null, null, lines, contact))).Read<OrderView>();

    [Fact]
    public async Task A_customers_recent_orders_newest_first_without_cancelled_ones_or_other_customers()
    {
        var first = await Order(Amira, Doner(2), Can());
        var cancelled = await Order(Amira, Can());
        Assert.Equal(HttpStatusCode.OK, (await Manager.Post($"/api/orders/{cancelled.Id}/status", new StatusRequest(OrderStatus.Cancelled, "Customer changed their mind"))).StatusCode);
        await Order(new OrderContact("Ben", "07700 900456", null, null), Can());
        var latest = await Order(Amira, MealDeal);

        var past = await Cashier.GetJson<List<PastOrder>>("/api/customers/orders?phone=%2B447700900123"); // any way of writing the number

        Assert.Equal([latest.Id, first.Id], past.Select(p => p.Id));
        Assert.Equal("2 × Doner kebab, Can", past[1].Summary);
        Assert.Equal(first.Total, past[1].Total);
        Assert.Empty(await Cashier.GetJson<List<PastOrder>>("/api/customers/orders?phone=123"));
    }

    [Fact]
    public async Task Repeating_gives_the_same_items_extras_notes_and_meal_deal_choices()
    {
        var original = await Order(Amira, Doner(2, "no onions"), MealDeal, Can(3));

        var repeat = await Cashier.GetJson<RepeatView>($"/api/orders/{original.Id}/repeat");

        Assert.Empty(repeat.Unavailable);
        Assert.Equal(3, repeat.Lines.Count);
        Assert.Equal((TestMenu.Doner, 2, "no onions"), (repeat.Lines[0].ProductId, repeat.Lines[0].Quantity, repeat.Lines[0].Note));
        Assert.Equal([TestMenu.Garlic], repeat.Lines[0].Extras);
        Assert.Equal(MealDeal.Choices!.Select(c => (c.SlotId, c.ProductId, c.VariantId)), repeat.Lines[1].Choices!.Select(c => (c.SlotId, c.ProductId, c.VariantId)));
        Assert.Equal([TestMenu.Garlic], repeat.Lines[1].Choices![1].Extras);

        // Placing the repeat gives the same total (prices unchanged).
        var again = await Order(Amira, [.. repeat.Lines]);
        Assert.Equal(original.Total, again.Total);
    }

    [Fact]
    public async Task Items_sold_out_today_are_left_out_and_named()
    {
        var original = await Order(Amira, Doner(), Can());
        await Cashier.Post($"/api/menu/products/{TestMenu.Can}/sold-out", new SoldOutRequest(true));

        var repeat = await Cashier.GetJson<RepeatView>($"/api/orders/{original.Id}/repeat");

        Assert.Equal(TestMenu.Doner, Assert.Single(repeat.Lines).ProductId);
        Assert.Equal(["Can"], repeat.Unavailable);
    }

    [Fact]
    public async Task Popular_lists_best_sellers_once_sold_twice_or_more()
    {
        Assert.Empty(await Cashier.GetJson<List<Guid>>("/api/menu/popular"));

        await Order(null, Can(5), Doner());
        await Order(null, Doner(2));
        var popular = await Cashier.GetJson<List<Guid>>("/api/menu/popular");
        Assert.Empty(popular); // worked out at most every 10 minutes

        Clock.Now += TimeSpan.FromMinutes(11);
        Assert.Equal([TestMenu.Can, TestMenu.Doner], await Cashier.GetJson<List<Guid>>("/api/menu/popular"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await TillOnly.GetAsync(new Uri("/api/menu/popular", UriKind.Relative))).StatusCode);
    }
}
