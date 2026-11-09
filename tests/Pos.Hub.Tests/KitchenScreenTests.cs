using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>
/// Kitchen screens per section: the grill screen shows kebabs, the pizza screen pizzas (the same rules as the kitchen
/// printers); each marks its own part ready and the order is Ready when every section with a screen is done.
/// </summary>
public sealed class KitchenScreenTests : HubTestBase
{
    private static readonly Guid GrillId = Guid.NewGuid();
    private static readonly Guid PizzaId = Guid.NewGuid();
    private static readonly PrinterChoice Pdf = new(PrintOutputType.Pdf, null, null, 80, true, 2);

    private static OrderLineRequest Pizza => new(TestMenu.Margherita, TestMenu.Margherita12);
    private static OrderLineRequest Doner => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]);
    private static OrderLineRequest Can => new(TestMenu.Can, TestMenu.CanRegular);

    private static OrderLineRequest Deal => new(TestMenu.Deal, TestMenu.DealRegular, 1, Choices:
    [
        new DealChoiceRequest(TestMenu.PizzaSlot, TestMenu.Margherita, TestMenu.Margherita12),
        new DealChoiceRequest(TestMenu.KebabSlot, TestMenu.Doner, TestMenu.DonerRegular, [TestMenu.Garlic]),
        new DealChoiceRequest(TestMenu.DrinkSlot, TestMenu.Can, TestMenu.CanRegular),
    ]);

    private HttpClient _grillScreen = null!;
    private HttpClient _pizzaScreen = null!;
    private HttpClient _allScreen = null!;
    private Guid _grillId;
    private Guid _pizzaId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Manager.Put("/api/menu", TestMenu.State());

        // Grill first (kebabs and anything nobody chose, e.g. drinks), then Pizza.
        (await Owner.Put("/api/settings/printers", new PrinterSetup(Pdf,
        [
            new KitchenPrinterChoice(GrillId, "Grill", Pdf, KitchenPrintMode.Chosen, [TestMenu.Kebabs], []),
            new KitchenPrinterChoice(PizzaId, "Pizza", Pdf, KitchenPrintMode.Chosen, [TestMenu.Pizzas], []),
        ], true))).EnsureSuccessStatusCode();

        (_grillScreen, _grillId) = await Screen("Grill screen");
        (_pizzaScreen, _pizzaId) = await Screen("Pizza screen");
        (_allScreen, _) = await Screen("Pass screen"); // not set: all food
        (await Owner.Put("/api/settings/kitchen-screens", new List<KitchenScreenChoice>
        {
            new(_grillId, [GrillId]),
            new(_pizzaId, [PizzaId]),
        })).EnsureSuccessStatusCode();
    }

    private async Task<(HttpClient Client, Guid Id)> Screen(string name)
    {
        var code = await (await Owner.Post("/api/devices/pairing-code", new { })).Read<PairingCodeView>();
        var device = await (await Anonymous.Post("/api/devices/pair", new PairRequest(code.Code, name, Security.DeviceType.KitchenScreen))).Read<Security.PairedDevice>();
        return (ClientFor(await Host.Login(device, TestShop.KitchenPin)), device.DeviceId);
    }

    private async Task<OrderView> Send(params OrderLineRequest[] lines)
    {
        var order = await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, lines))).Read<OrderView>();
        (await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { })).EnsureSuccessStatusCode();
        return order;
    }

    private static async Task<KitchenOrderView?> Seen(HttpClient screen, Guid orderId)
    {
        var response = await screen.GetAsync(new Uri($"/api/kitchen/orders/{orderId}", UriKind.Relative));
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Read<KitchenOrderView>();
    }

    private async Task<OrderStatus> Status(Guid orderId) => (await Cashier.GetJson<OrderView>($"/api/orders/{orderId}")).Status;

    private static List<string> Names(KitchenOrderView? view) => [.. view!.Order.Lines.Select(l => l.Name.En)];

    [Fact]
    public async Task Each_screen_shows_only_its_sections_food_and_an_unset_screen_shows_everything()
    {
        Assert.Equal("Grill", Assert.Single((await _grillScreen.GetJson<KitchenScreenView>("/api/kitchen/screen")).Sections).Name);
        Assert.True((await _allScreen.GetJson<KitchenScreenView>("/api/kitchen/screen")).AllFood);

        var order = await Send(Pizza, Doner, Can);

        Assert.Equal(["Doner kebab", "Can"], Names(await Seen(_grillScreen, order.Id))); // the can: no section chose drinks, so the first one
        Assert.Equal(["Margherita"], Names(await Seen(_pizzaScreen, order.Id)));
        Assert.Equal(3, (await Seen(_allScreen, order.Id))!.Order.Lines.Count);

        // A pizza-only order is not on the grill screen at all.
        var pizzaOnly = await Send(Pizza);
        Assert.Null(await Seen(_grillScreen, pizzaOnly.Id));
        var grillList = await _grillScreen.GetJson<List<KitchenOrderView>>("/api/kitchen/orders");
        Assert.Contains(grillList, v => v.Order.Id == order.Id);
        Assert.DoesNotContain(grillList, v => v.Order.Id == pizzaOnly.Id);
    }

    [Fact]
    public async Task The_order_is_ready_when_every_section_has_finished_its_part()
    {
        var order = await Send(Pizza, Doner);

        var pizza = await (await _pizzaScreen.Post($"/api/kitchen/orders/{order.Id}/ready", new { })).Read<KitchenOrderView>();
        Assert.True(pizza.DoneHere);
        Assert.Equal(["Grill"], pizza.WaitingFor);
        Assert.Equal(OrderStatus.Accepted, await Status(order.Id)); // still waiting for the grill
        Assert.False((await Seen(_grillScreen, order.Id))!.DoneHere);

        await _grillScreen.Post($"/api/kitchen/orders/{order.Id}/ready", new { });
        Assert.Equal(OrderStatus.Ready, await Status(order.Id));

        var history = Host.Services.GetRequiredService<OrderService>().Get(order.Id).History;
        Assert.Contains(history, e => e.Kind == OrderEventKind.KitchenPartChanged && e.Description == "Pizza part ready");
        Assert.Contains(history, e => e.Kind == OrderEventKind.KitchenPartChanged && e.Description == "Grill part ready");
    }

    [Fact]
    public async Task An_order_for_one_section_is_ready_as_soon_as_that_section_is_done()
    {
        var order = await Send(Pizza);
        await _pizzaScreen.Post($"/api/kitchen/orders/{order.Id}/ready", new { });
        Assert.Equal(OrderStatus.Ready, await Status(order.Id));
    }

    [Fact]
    public async Task A_meal_deal_is_split_between_the_screens()
    {
        var order = await Send(Deal);

        var pizza = (await Seen(_pizzaScreen, order.Id))!.Order.Lines.Single();
        Assert.Equal("Pizza & Kebab Deal (part)", pizza.Name.En);
        Assert.Equal(["Margherita"], pizza.DealItems.Select(d => d.Name.En));

        var grill = (await Seen(_grillScreen, order.Id))!.Order.Lines.Single();
        Assert.Equal(["Doner kebab", "Can"], grill.DealItems.Select(d => d.Name.En));
    }

    [Fact]
    public async Task Items_added_later_reopen_only_the_sections_that_got_them()
    {
        var order = await Send(Pizza, Doner);
        await _pizzaScreen.Post($"/api/kitchen/orders/{order.Id}/ready", new { });
        await _grillScreen.Post($"/api/kitchen/orders/{order.Id}/ready", new { });
        Assert.Equal(OrderStatus.Ready, await Status(order.Id));

        (await Cashier.Post($"/api/orders/{order.Id}/lines", new AddLinesRequest([Doner]))).EnsureSuccessStatusCode();
        (await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { })).EnsureSuccessStatusCode();

        Assert.Equal(OrderStatus.Preparing, await Status(order.Id)); // back on the screens
        Assert.False((await Seen(_grillScreen, order.Id))!.DoneHere);
        Assert.True((await Seen(_pizzaScreen, order.Id))!.DoneHere);

        await _grillScreen.Post($"/api/kitchen/orders/{order.Id}/ready", new { });
        Assert.Equal(OrderStatus.Ready, await Status(order.Id));
    }

    [Fact]
    public async Task Recall_puts_a_sections_part_back_and_the_order_back_to_preparing()
    {
        var order = await Send(Pizza);
        await _pizzaScreen.Post($"/api/kitchen/orders/{order.Id}/ready", new { });
        Assert.Equal(OrderStatus.Ready, await Status(order.Id));

        var back = await (await _pizzaScreen.Post($"/api/kitchen/orders/{order.Id}/recall", new { })).Read<KitchenOrderView>();
        Assert.False(back.DoneHere);
        Assert.Equal(OrderStatus.Preparing, await Status(order.Id));
    }

    [Fact]
    public async Task A_screen_showing_all_food_marks_the_whole_order_ready()
    {
        var order = await Send(Pizza, Doner);
        await _allScreen.Post($"/api/kitchen/orders/{order.Id}/ready", new { });
        Assert.Equal(OrderStatus.Ready, await Status(order.Id));
    }

    [Fact]
    public async Task Only_the_owner_chooses_and_removed_sections_fall_back_to_all_food()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put("/api/settings/kitchen-screens", new List<KitchenScreenChoice>())).StatusCode);

        var notAScreen = await Owner.Put("/api/settings/kitchen-screens", new List<KitchenScreenChoice> { new(Shop.Till.DeviceId, [PizzaId]) });
        Assert.Equal("That screen is not a kitchen screen.", (await notAScreen.Read<ErrorView>()).Message);

        var setup = await Owner.GetJson<KitchenScreensSetup>("/api/settings/kitchen-screens");
        Assert.Equal(["Grill", "Pizza"], setup.Sections.Select(s => s.Name));
        Assert.Equal(2, setup.Screens.Count);

        // The pizza printer is removed: the pizza screen now shows all food rather than nothing.
        (await Owner.Put("/api/settings/printers", new PrinterSetup(Pdf, [new KitchenPrinterChoice(GrillId, "Grill", Pdf, KitchenPrintMode.Chosen, [TestMenu.Kebabs], [])], true))).EnsureSuccessStatusCode();
        Assert.True((await _pizzaScreen.GetJson<KitchenScreenView>("/api/kitchen/screen")).AllFood);
    }
}
