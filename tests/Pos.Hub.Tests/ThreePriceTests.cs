using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.MenuSheets;

namespace Pos.Hub.Tests;

/// <summary>
/// Main, takeaway &amp; collection, and eat-in prices: delivery always pays the main price; takeaway (and collection)
/// and eat in pay their own price when it is set above zero, otherwise the main price.
/// </summary>
public sealed class ThreePriceTests : HubTestBase
{
    // Doner: main (delivery) £8.99, takeaway & collection £7.99, eat in £9.99. Can: one price, £1.20, for everything.
    private static MenuState Priced()
    {
        var state = TestMenu.State();
        return state with
        {
            Products = [.. state.Products.Select(p => p.Id == TestMenu.Doner
                ? p with { Variants = [.. p.Variants.Select(v => v with { TakeawayPrice = Money.Gbp(799), EatInPrice = Money.Gbp(999) })] }
                : p)],
        };
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        (await Manager.Put("/api/menu", Priced())).EnsureSuccessStatusCode();
    }

    private static OrderLineRequest Doner(int quantity = 1) => new(TestMenu.Doner, TestMenu.DonerRegular, quantity, [TestMenu.Garlic]);

    private static OrderLineRequest Can() => new(TestMenu.Can, TestMenu.CanRegular, 1);

    private static CreateOrderRequest Request(OrderType type, OrderSource source, params OrderLineRequest[] lines) => new(
        DeviceIds.New(), Guid.NewGuid().ToString(), source, type, null, null, lines,
        type == OrderType.Delivery || source == OrderSource.Phone ? new OrderContact("Sam", "07700 900321", type == OrderType.Delivery ? new DeliveryAddress("1 High Street", null, "Birmingham", "B12 9AA") : null, null) : null,
        type == OrderType.Delivery ? new DeliveryRequest(1.0m) : null);

    private async Task<OrderView> Place(CreateOrderRequest request)
    {
        var response = await Cashier.Post("/api/orders", request);
        response.EnsureSuccessStatusCode();
        return await response.Read<OrderView>();
    }

    [Theory]
    [InlineData(OrderType.Takeaway, OrderSource.Counter, 799)]
    [InlineData(OrderType.Takeaway, OrderSource.Phone, 799)] // collection
    [InlineData(OrderType.EatIn, OrderSource.Counter, 999)]
    public async Task Takeaway_collection_and_eat_in_orders_pay_their_own_price(OrderType type, OrderSource source, int donerPence)
    {
        var order = await Place(Request(type, source, Doner(2), Can()));

        Assert.Equal(Money.Gbp(donerPence * 2), order.Lines[0].LineTotal);
        Assert.Equal(Money.Gbp(120), order.Lines[1].LineTotal); // one price: the same for every type
        Assert.Equal(Money.Gbp((donerPence * 2) + 120), order.GoodsTotal);
    }

    [Fact]
    public async Task Delivery_always_pays_the_main_price()
    {
        var quote = await (await Cashier.Post("/api/orders/quote", Request(OrderType.Delivery, OrderSource.Phone, Doner(2)))).Read<OrderView>();
        Assert.Equal(Money.Gbp(899 * 2), quote.Lines[0].LineTotal);
    }

    [Fact]
    public async Task Items_added_to_an_open_eat_in_order_take_the_eat_in_price()
    {
        var order = await Place(Request(OrderType.EatIn, OrderSource.Counter, Can()));

        var more = await (await Cashier.Post($"/api/orders/{order.Id}/lines", new AddLinesRequest([Doner()]))).Read<OrderView>();

        Assert.Equal(Money.Gbp(999), more.Lines.Single(l => l.Name.En == "Doner kebab").LineTotal);
    }

    [Fact]
    public async Task Zero_or_empty_takeaway_and_eat_in_prices_mean_the_main_price()
    {
        var state = Priced();
        state = state with { Products = [.. state.Products.Select(p => p.Id == TestMenu.Doner ? p with { Variants = [.. p.Variants.Select(v => v with { TakeawayPrice = Money.Gbp(0), EatInPrice = null })] } : p)] };
        (await Manager.Put("/api/menu", state with { Version = (await Manager.GetJson<MenuState>("/api/menu")).Version })).EnsureSuccessStatusCode();

        Assert.Equal(Money.Gbp(899), (await Place(Request(OrderType.Takeaway, OrderSource.Counter, Doner()))).Lines[0].LineTotal);
        Assert.Equal(Money.Gbp(899), (await Place(Request(OrderType.EatIn, OrderSource.Counter, Doner()))).Lines[0].LineTotal);
    }

    [Fact]
    public async Task Repeating_an_earlier_order_for_another_type_uses_todays_price_for_that_type()
    {
        var takeaway = await Place(Request(OrderType.Takeaway, OrderSource.Phone, Doner()));
        var repeat = await Cashier.GetJson<RepeatView>($"/api/orders/{takeaway.Id}/repeat");

        var delivered = await (await Cashier.Post("/api/orders/quote", Request(OrderType.Delivery, OrderSource.Phone, [.. repeat.Lines]))).Read<OrderView>();

        Assert.Equal(Money.Gbp(799), takeaway.GoodsTotal);
        Assert.Equal(Money.Gbp(899), delivered.Lines[0].LineTotal);
    }

    [Fact]
    public void The_spreadsheet_has_takeaway_and_eat_in_columns_that_round_trip_and_import()
    {
        var current = Priced();
        var sheets = Spreadsheet.Read(MenuSheetImport.Workbook(current), "menu.xlsx");
        Assert.Equal(["Category", "Item", "Size", "Price", "Takeaway price", "Eat in price"], sheets[0].Rows[0].Take(6));
        Assert.Contains(sheets[0].Rows, r => r.Contains("Doner kebab") && r.Contains("7.99") && r.Contains("9.99"));
        var same = MenuSheetImport.Plan(current, [.. sheets], MissingItems.Keep, Guid.NewGuid());
        Assert.Empty(same.Preview.Changes); // downloading and importing again changes nothing

        var sheet = Spreadsheet.ReadCsv(string.Join("\n",
            "Category,Item,Size,Price,Takeaway price,Eat in price,VAT",
            "Kebabs,Doner kebab,,9.49,,10.49,Hot food",
            "Drinks,Can,,1.20,0,,Standard"), "Menu");
        var plan = MenuSheetImport.Plan(current, [sheet], MissingItems.Keep, Guid.NewGuid());

        Assert.Empty(plan.Preview.Problems);
        var doner = plan.State!.Products.Single(p => p.Id == TestMenu.Doner).Variants[0];
        Assert.Equal((Money.Gbp(949), (Money?)null, (Money?)Money.Gbp(1049)), (doner.Price, doner.TakeawayPrice, doner.EatInPrice));
        Assert.Null(plan.State.Products.Single(p => p.Id == TestMenu.Can).Variants[0].TakeawayPrice);
        Assert.Contains(plan.Preview.Changes, c => c.Contains("takeaway £7.99 → main price", StringComparison.Ordinal) && c.Contains("eat in £9.99 → £10.49", StringComparison.Ordinal));

        // A sheet without the columns keeps the till's takeaway and eat-in prices.
        var plain = MenuSheetImport.Plan(current, [Spreadsheet.ReadCsv("Category,Item,Price,VAT\nKebabs,Doner kebab,8.99,Hot food", "Menu")], MissingItems.Keep, Guid.NewGuid());
        Assert.Equal(Money.Gbp(799), plain.State?.Products.Single(p => p.Id == TestMenu.Doner).Variants[0].TakeawayPrice ?? Money.Gbp(799));
        Assert.Empty(plain.Preview.Changes);
    }
}
