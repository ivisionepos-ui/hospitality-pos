using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Tax;

namespace Pos.Domain.Tests.Menus;

public class MenuEditingTests
{
    private static readonly Guid Manager = Guid.NewGuid();

    [Fact]
    public void Items_appear_in_the_order_they_were_added()
    {
        var s = new SampleMenu();

        Assert.Equal([s.Margherita, s.Pepperoni], s.Menu.ProductsIn(s.Pizzas).Select(p => p.Id));
        Assert.Equal([s.Pizzas, s.Kebabs, s.Drinks, s.Deals], s.Menu.Categories.Select(c => c.Id));
    }

    [Fact]
    public void Adding_at_a_position_shifts_the_buttons_after_it()
    {
        var s = new SampleMenu();
        var hawaiian = new Product
        {
            Id = Guid.NewGuid(), CategoryId = s.Pizzas, Name = new LocalizedText("Hawaiian"), VatCategory = VatCategory.HotFood,
            Variants = [new ProductVariant(Guid.NewGuid(), new LocalizedText("12\""), Money.Gbp(1249))],
        };

        s.Menu.AddProduct(hawaiian, Manager, position: 0);

        Assert.Equal([hawaiian.Id, s.Margherita, s.Pepperoni], s.Menu.ProductsIn(s.Pizzas).Select(p => p.Id));
        Assert.Equal([0, 1, 2], s.Menu.ProductsIn(s.Pizzas).Select(p => p.SortOrder));
    }

    [Fact]
    public void Renaming_is_logged_with_who_when_and_what()
    {
        var s = new SampleMenu();
        var versionBefore = s.Menu.Version;
        s.Clock.Now = new DateTimeOffset(2026, 11, 3, 15, 30, 0, TimeSpan.Zero);

        var change = s.Menu.RenameProduct(s.Margherita, new LocalizedText("Margherita Classic", "مارغريتا كلاسيك"), Manager);

        var product = s.Menu.GetProduct(s.Margherita);
        Assert.Equal("Margherita Classic", product.Name.En);
        Assert.Equal("مارغريتا كلاسيك", product.Name.For("ar"));
        Assert.Equal(versionBefore + 1, s.Menu.Version);
        Assert.Equal(versionBefore + 1, change.Version);
        Assert.Equal(Manager, change.UserId);
        Assert.Equal(s.Clock.Now, change.At);
        Assert.Equal(MenuChangeKind.Updated, change.Kind);
        Assert.Contains("renamed to \"Margherita Classic\"", change.Summary, StringComparison.Ordinal);
        Assert.Same(change, s.Menu.ChangeLog[^1]);
    }

    [Fact]
    public void Price_change_is_logged_with_old_and_new_price()
    {
        var s = new SampleMenu();

        var change = s.Menu.SetPrice(s.Margherita, s.Margherita12, Money.Gbp(1249), Manager);

        Assert.Equal(Money.Gbp(1249), s.Menu.GetProduct(s.Margherita).Variants.Single(v => v.Id == s.Margherita12).Price);
        Assert.Equal(Money.Gbp(899), s.Menu.GetProduct(s.Margherita).Variants.Single(v => v.Id == s.Margherita10).Price);
        Assert.Contains("price (12\") £11.99 → £12.49", change.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Sold_out_and_back_in_stock()
    {
        var s = new SampleMenu();

        var soldOut = s.Menu.SetSoldOut(s.Doner, true, Manager);
        Assert.True(s.Menu.GetProduct(s.Doner).IsSoldOut);
        Assert.Contains("sold out", soldOut.Summary, StringComparison.Ordinal);

        var back = s.Menu.SetSoldOut(s.Doner, false, Manager);
        Assert.False(s.Menu.GetProduct(s.Doner).IsSoldOut);
        Assert.Contains("back in stock", back.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_panel_save_cannot_change_position_or_category()
    {
        var s = new SampleMenu();
        var pepperoni = s.Menu.GetProduct(s.Pepperoni);

        s.Menu.UpdateProduct(pepperoni with { SortOrder = 99, Colour = "#C0392B" }, Manager);
        Assert.Equal(1, s.Menu.GetProduct(s.Pepperoni).SortOrder);
        Assert.Equal("#C0392B", s.Menu.GetProduct(s.Pepperoni).Colour);

        Assert.Throws<InvalidOperationException>(() => s.Menu.UpdateProduct(pepperoni with { CategoryId = s.Kebabs }, Manager));
    }

    [Fact]
    public void Moving_within_a_category_reorders_the_buttons()
    {
        var s = new SampleMenu();

        var change = s.Menu.MoveProduct(s.Pepperoni, s.Pizzas, 0, Manager);

        Assert.Equal([s.Pepperoni, s.Margherita], s.Menu.ProductsIn(s.Pizzas).Select(p => p.Id));
        Assert.Equal(MenuChangeKind.Moved, change.Kind);
    }

    [Fact]
    public void Moving_to_another_category_renumbers_both()
    {
        var s = new SampleMenu();

        s.Menu.MoveProduct(s.Margherita, s.Kebabs, 1, Manager);

        Assert.Equal([s.Pepperoni], s.Menu.ProductsIn(s.Pizzas).Select(p => p.Id));
        Assert.Equal(0, s.Menu.GetProduct(s.Pepperoni).SortOrder);
        Assert.Equal([s.Doner, s.Margherita], s.Menu.ProductsIn(s.Kebabs).Select(p => p.Id));
        Assert.Equal(s.Kebabs, s.Menu.GetProduct(s.Margherita).CategoryId);
    }

    [Fact]
    public void Moving_a_category_tab()
    {
        var s = new SampleMenu();

        s.Menu.MoveCategory(s.Deals, 0, Manager);

        Assert.Equal([s.Deals, s.Pizzas, s.Kebabs, s.Drinks], s.Menu.Categories.Select(c => c.Id));
    }

    [Fact]
    public void Undo_restores_the_previous_price_and_is_itself_logged()
    {
        var s = new SampleMenu();
        s.Menu.SetPrice(s.Doner, s.DonerRegular, Money.Gbp(999), Manager);

        var undo = s.Menu.UndoLast(Manager);

        Assert.Equal(Money.Gbp(899), s.Menu.GetProduct(s.Doner).Variants[0].Price);
        Assert.Equal(MenuChangeKind.Undo, undo.Kind);
        Assert.StartsWith("Undid:", undo.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_steps_back_one_change_at_a_time()
    {
        var s = new SampleMenu();
        s.Menu.SetPrice(s.Doner, s.DonerRegular, Money.Gbp(999), Manager);
        s.Menu.RenameProduct(s.Doner, new LocalizedText("Lamb doner"), Manager);

        s.Menu.UndoLast(Manager);
        Assert.Equal("Doner kebab", s.Menu.GetProduct(s.Doner).Name.En);
        Assert.Equal(Money.Gbp(999), s.Menu.GetProduct(s.Doner).Variants[0].Price);

        s.Menu.UndoLast(Manager);
        Assert.Equal(Money.Gbp(899), s.Menu.GetProduct(s.Doner).Variants[0].Price);
    }

    [Fact]
    public void Undo_of_an_add_removes_it_and_restores_positions()
    {
        var s = new SampleMenu();
        var newPizza = new Product
        {
            Id = Guid.NewGuid(), CategoryId = s.Pizzas, Name = new LocalizedText("Veggie"), VatCategory = VatCategory.HotFood,
            Variants = [new ProductVariant(Guid.NewGuid(), new LocalizedText("12\""), Money.Gbp(1099))],
        };
        s.Menu.AddProduct(newPizza, Manager, position: 0);

        s.Menu.UndoLast(Manager);

        Assert.False(s.Menu.HasProduct(newPizza.Id));
        Assert.Equal([s.Margherita, s.Pepperoni], s.Menu.ProductsIn(s.Pizzas).Select(p => p.Id));
        Assert.Equal([0, 1], s.Menu.ProductsIn(s.Pizzas).Select(p => p.SortOrder));
    }

    [Fact]
    public void Undo_of_a_move_puts_everything_back()
    {
        var s = new SampleMenu();
        s.Menu.MoveProduct(s.Margherita, s.Kebabs, 0, Manager);

        s.Menu.UndoLast(Manager);

        Assert.Equal([s.Margherita, s.Pepperoni], s.Menu.ProductsIn(s.Pizzas).Select(p => p.Id));
        Assert.Equal([s.Doner], s.Menu.ProductsIn(s.Kebabs).Select(p => p.Id));
    }

    [Fact]
    public void Undo_with_nothing_left_throws()
    {
        var menu = new Menu();
        Assert.Throws<InvalidOperationException>(() => menu.UndoLast(Manager));
    }

    [Fact]
    public void Version_goes_up_with_every_change()
    {
        var s = new SampleMenu();
        var start = s.Menu.Version;

        s.Menu.SetSoldOut(s.Can, true, Manager);
        s.Menu.UndoLast(Manager);

        Assert.Equal(start + 2, s.Menu.Version);
        Assert.Equal(s.Menu.ChangeLog.Count, s.Menu.Version);
    }

    [Fact]
    public void Course_group_comes_from_the_item_or_its_category()
    {
        var s = new SampleMenu();

        Assert.Equal(s.Mains, s.Menu.CourseGroupFor(s.Menu.GetProduct(s.Margherita))!.Id);
        Assert.Equal(s.DrinksGroup, s.Menu.CourseGroupFor(s.Menu.GetProduct(s.Can))!.Id);
        Assert.Null(s.Menu.CourseGroupFor(s.Menu.GetProduct(s.Deal)));
    }

    [Fact]
    public void Arabic_falls_back_to_english_when_missing()
    {
        var s = new SampleMenu();

        Assert.Equal("Kebabs", s.Menu.GetCategory(s.Kebabs).Name.For("ar"));
        Assert.Equal("بيتزا", s.Menu.GetCategory(s.Pizzas).Name.For("ar"));
    }

    [Fact]
    public void Rejects_invalid_edits()
    {
        var s = new SampleMenu();
        var margherita = s.Menu.GetProduct(s.Margherita);

        Assert.Throws<ArgumentException>(() => s.Menu.SetPrice(s.Margherita, s.Margherita12, Money.Gbp(-1), Manager));
        Assert.Throws<ArgumentException>(() => s.Menu.SetPrice(s.Margherita, s.Margherita12, new Money(100, "EUR"), Manager));
        Assert.Throws<KeyNotFoundException>(() => s.Menu.SetPrice(s.Margherita, Guid.NewGuid(), Money.Gbp(100), Manager));
        Assert.Throws<ArgumentException>(() => s.Menu.UpdateProduct(margherita with { Colour = "red" }, Manager));
        Assert.Throws<ArgumentException>(() => s.Menu.UpdateProduct(margherita with { Variants = [] }, Manager));
        Assert.Throws<ArgumentException>(() => s.Menu.UpdateProduct(margherita with { ModifierGroupIds = [Guid.NewGuid()] }, Manager));
        Assert.Throws<ArgumentException>(() => s.Menu.UpdateProduct(margherita with { CourseGroupId = Guid.NewGuid() }, Manager));
        Assert.Throws<ArgumentException>(() => new LocalizedText(" "));
        Assert.Throws<ArgumentException>(() => new LocalizedText(new string('x', 121)));
    }

    [Fact]
    public void Size_ids_must_be_unique_across_the_menu()
    {
        var s = new SampleMenu();
        var copy = new Product
        {
            Id = Guid.NewGuid(), CategoryId = s.Pizzas, Name = new LocalizedText("Copy"), VatCategory = VatCategory.HotFood,
            Variants = [new ProductVariant(s.Margherita12, new LocalizedText("12\""), Money.Gbp(1000))],
        };

        Assert.Throws<ArgumentException>(() => s.Menu.AddProduct(copy, Manager));
    }

    [Fact]
    public void Rejects_invalid_meal_deals_and_extras()
    {
        var s = new SampleMenu();
        var deal = s.Menu.GetProduct(s.Deal);

        // A choice pointing at something not on the menu
        Assert.Throws<ArgumentException>(() => s.Menu.UpdateProduct(deal with
        {
            DealSlots = [new MealDealSlot(Guid.NewGuid(), new LocalizedText("Pick"), 1, 1, [new SlotOption(SlotOptionKind.Product, Guid.NewGuid())])],
        }, Manager));

        // Minimum above maximum
        Assert.Throws<ArgumentException>(() => s.Menu.UpdateProduct(deal with
        {
            DealSlots = [new MealDealSlot(Guid.NewGuid(), new LocalizedText("Pick"), 2, 1, [new SlotOption(SlotOptionKind.Category, s.Drinks)])],
        }, Manager));

        // A deal with no choices, and a normal item with choices
        Assert.Throws<ArgumentException>(() => s.Menu.UpdateProduct(deal with { DealSlots = [] }, Manager));
        Assert.Throws<ArgumentException>(() => s.Menu.UpdateProduct(s.Menu.GetProduct(s.Can) with { DealSlots = deal.DealSlots }, Manager));

        // Extras group asking for more than it offers
        Assert.Throws<ArgumentException>(() => s.Menu.SaveModifierGroup(new ModifierGroup(Guid.NewGuid(), new LocalizedText("Bad"), 3, 3,
            [new ModifierOption(Guid.NewGuid(), new LocalizedText("Only one"), Money.Gbp(0))]), Manager));
    }
}
