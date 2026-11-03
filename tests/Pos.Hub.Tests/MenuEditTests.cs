using System.Net;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Data;

namespace Pos.Hub.Tests;

/// <summary>Edit menu mode over the API, and adding items to an order safely.</summary>
public sealed class MenuEditTests : HubTestBase
{
    private async Task<MenuState> Menu() => await Manager.GetJson<MenuState>("/api/menu");

    private async Task<MenuState> Setup()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        return await Menu();
    }

    [Fact]
    public async Task Edit_panel_saves_name_prices_vat_extras_and_sold_out_in_one_go()
    {
        var menu = await Setup();
        var doner = menu.Products.Single(p => p.Id == TestMenu.Doner);
        var edited = doner with
        {
            Name = new LocalizedText("Doner kebab wrap"),
            ButtonName = new LocalizedText("Doner wrap"),
            Variants = [doner.Variants[0] with { Price = Money.Gbp(949) }, new ProductVariant(Guid.NewGuid(), new LocalizedText("Large"), Money.Gbp(1149))],
            IsSoldOut = true,
        };

        var response = await Manager.Put($"/api/menu/products/{doner.Id}", new SaveProductRequest(edited, doner));
        var saved = (await Menu()).Products.Single(p => p.Id == doner.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Doner kebab wrap", saved.Name.En);
        Assert.Equal("Doner wrap", saved.ButtonName!.En);
        Assert.Equal([949L, 1149L], saved.Variants.Select(v => v.Price.MinorUnits));
        Assert.True(saved.IsSoldOut);
        Assert.Contains("Doner", (await Manager.GetJson<List<MenuChangeRecord>>("/api/menu/changes?since=0"))[^1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_is_refused_if_the_item_changed_on_another_till_meanwhile()
    {
        var menu = await Setup();
        var doner = menu.Products.Single(p => p.Id == TestMenu.Doner);

        // Till 2 changes the price while till 1 has the edit panel open.
        await Manager.Post($"/api/menu/products/{doner.Id}/price", new PriceRequest(TestMenu.DonerRegular, 999));
        var response = await Manager.Put($"/api/menu/products/{doner.Id}", new SaveProductRequest(doner with { Name = new LocalizedText("Doner") }, doner));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ChangedElsewhere", (await response.Read<ErrorView>()).Code);
        Assert.Equal(999, (await Menu()).Products.Single(p => p.Id == doner.Id).Variants[0].Price.MinorUnits); // nothing lost
    }

    [Fact]
    public async Task Items_and_categories_can_be_added_moved_and_undone()
    {
        await Setup();
        var sides = new Category { Id = Guid.NewGuid(), Name = new LocalizedText("Sides"), Colour = "#4d7c0f" };
        Assert.Equal(HttpStatusCode.OK, (await Manager.Post("/api/menu/categories", sides)).StatusCode);

        var chips = new Product
        {
            Id = Guid.NewGuid(), CategoryId = sides.Id, Name = new LocalizedText("Chips"), VatCategory = VatCategory.HotFood,
            Variants = [new ProductVariant(Guid.NewGuid(), new LocalizedText("Regular"), Money.Gbp(250))],
        };
        Assert.Equal(HttpStatusCode.OK, (await Manager.Post("/api/menu/products", new AddProductRequest(chips))).StatusCode);

        // Move the doner into Sides, first place; then the Sides tab to the front.
        await Manager.Post($"/api/menu/products/{TestMenu.Doner}/move", new MoveRequest(0, sides.Id));
        await Manager.Post($"/api/menu/categories/{sides.Id}/move", new MoveRequest(0));
        var menu = await Menu();
        Assert.Equal(sides.Id, menu.Categories.OrderBy(c => c.SortOrder).First().Id);
        Assert.Equal(["Doner kebab", "Chips"], menu.Products.Where(p => p.CategoryId == sides.Id).OrderBy(p => p.SortOrder).Select(p => p.Name.En));

        // Undo the tab move.
        await Manager.PostAsync(new Uri("/api/menu/undo", UriKind.Relative), null);
        Assert.NotEqual(sides.Id, (await Menu()).Categories.OrderBy(c => c.SortOrder).First().Id);
    }

    [Fact]
    public async Task Category_panel_and_extras_can_be_saved()
    {
        var menu = await Setup();
        var pizzas = menu.Categories.Single(c => c.Id == TestMenu.Pizzas);
        var sauce = menu.ModifierGroups.Single(g => g.Id == TestMenu.Sauce);

        await Manager.Put($"/api/menu/categories/{pizzas.Id}", new SaveCategoryRequest(pizzas with { Name = new LocalizedText("Stone-baked pizzas"), Colour = "#c2410c" }, pizzas));
        var chilli = new ModifierOption(Guid.NewGuid(), new LocalizedText("Chilli"), Money.Gbp(20));
        await Manager.Put($"/api/menu/modifier-groups/{sauce.Id}", new SaveModifierGroupRequest(sauce with { MaxChoices = 2, Options = [.. sauce.Options, chilli] }, sauce));
        var stale = await Manager.Put($"/api/menu/modifier-groups/{sauce.Id}", new SaveModifierGroupRequest(sauce with { MaxChoices = 3 }, sauce));

        menu = await Menu();
        Assert.Equal("Stone-baked pizzas", menu.Categories.Single(c => c.Id == pizzas.Id).Name.En);
        Assert.Equal(2, menu.ModifierGroups.Single(g => g.Id == sauce.Id).Options.Count);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Only_managers_edit_the_menu_and_bad_items_are_refused()
    {
        var menu = await Setup();
        var doner = menu.Products.Single(p => p.Id == TestMenu.Doner);

        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Put($"/api/menu/products/{doner.Id}", new SaveProductRequest(doner))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manager.Put($"/api/menu/products/{doner.Id}", new SaveProductRequest(doner with { Variants = [] }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manager.Put($"/api/menu/products/{TestMenu.Can}", new SaveProductRequest(doner))).StatusCode);
    }

    [Fact]
    public async Task Adding_items_with_a_request_id_is_safe_to_resend()
    {
        await Setup();
        var order = await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null,
            [new OrderLineRequest(TestMenu.Can, TestMenu.CanRegular)]))).Read<OrderView>();
        var request = new AddLinesRequest([new OrderLineRequest(TestMenu.Can, TestMenu.CanRegular, 2)], Guid.NewGuid());

        var first = await (await Cashier.Post($"/api/orders/{order.Id}/lines", request)).Read<OrderView>();
        var again = await (await Cashier.Post($"/api/orders/{order.Id}/lines", request)).Read<OrderView>();
        var other = await Cashier.Post($"/api/orders/{Guid.NewGuid()}/lines", request);

        Assert.Equal(2, first.Lines.Count);
        Assert.False(first.Duplicate);
        Assert.Equal(2, again.Lines.Count); // not added twice
        Assert.True(again.Duplicate);
        Assert.Equal(first.Total, again.Total);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
    }
}
