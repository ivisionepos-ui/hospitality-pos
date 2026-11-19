using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Pricing;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;

namespace Pos.Domain.Tests.Menus;

public class MenuOrderBuilderTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly Guid Manager = Guid.NewGuid();

    private static PricingContext Now => new(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero), London, Guid.NewGuid(), SalesChannel.Pos);

    private static List<string> Codes(IReadOnlyList<SelectionProblem> problems) => [.. problems.Select(p => p.Code)];

    private static DealSelection ValidDeal(SampleMenu s) => new(s.Deal, s.DealRegular,
    [
        new SlotChoice(s.PizzaSlot, s.Pepperoni, s.Pepperoni12, [s.ExtraCheese]),
        new SlotChoice(s.KebabSlot, s.Doner, s.DonerRegular, [s.Chilli]),
        new SlotChoice(s.DrinkSlot, s.Bottle, s.BottleRegular, []),
    ]);

    [Fact]
    public void Valid_item_with_extras_becomes_a_priced_line()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);

        var line = builder.ToLine(new ItemSelection(s.Margherita, s.Margherita12, [s.ExtraCheese, s.Peppers], Quantity: 2));
        var order = new OrderPricer(new UkVatPolicy(), []).Price(OrderType.Takeaway, Now, [line]);

        Assert.Equal(Money.Gbp(1199), line.Item.NormalUnitPrice);
        Assert.Equal(VatCategory.HotFood, line.VatCategory);
        Assert.Equal(Money.Gbp((1199 + 100 + 75) * 2), order.Total);
    }

    [Theory]
    [InlineData(null, null, null, 899)]
    [InlineData(OrderType.Delivery, 799, 999, 899)]   // delivery: always the main price
    [InlineData(OrderType.Takeaway, 799, 999, 799)]   // takeaway and collection: their own price
    [InlineData(OrderType.EatIn, 799, 999, 999)]      // eat in: its own price
    [InlineData(OrderType.Takeaway, null, 999, 899)]  // missing: the main price
    [InlineData(OrderType.EatIn, 799, 0, 899)]        // zero: the main price
    [InlineData(OrderType.Takeaway, 0, 0, 899)]
    public void Each_order_type_uses_its_own_price_or_the_main_price(OrderType? type, int? takeaway, int? eatIn, int expected)
    {
        var size = new ProductVariant(Guid.NewGuid(), new LocalizedText("Regular"), Money.Gbp(899), false,
            takeaway is { } t ? Money.Gbp(t) : null, eatIn is { } e ? Money.Gbp(e) : null);

        Assert.Equal(Money.Gbp(expected), size.PriceFor(type));
    }

    [Fact]
    public void Lines_are_priced_for_the_order_type_including_meal_deals()
    {
        var s = new SampleMenu();
        var margherita = s.Menu.GetProduct(s.Margherita);
        s.Menu.UpdateProduct(margherita with { Variants = [.. margherita.Variants.Select(v => v.Id == s.Margherita12 ? v with { TakeawayPrice = Money.Gbp(1099), EatInPrice = Money.Gbp(1299) } : v)] }, Manager);
        var deal = s.Menu.GetProduct(s.Deal);
        s.Menu.UpdateProduct(deal with { Variants = [.. deal.Variants.Select(v => v with { EatInPrice = v.Price + Money.Gbp(200) })] }, Manager);
        var item = new ItemSelection(s.Margherita, s.Margherita12, [s.ExtraCheese]);

        Assert.Equal(Money.Gbp(1199), new MenuOrderBuilder(s.Menu, OrderType.Delivery).ToLine(item).Item.NormalUnitPrice);
        Assert.Equal(Money.Gbp(1099), new MenuOrderBuilder(s.Menu, OrderType.Takeaway).ToLine(item).Item.NormalUnitPrice);
        Assert.Equal(Money.Gbp(1299), new MenuOrderBuilder(s.Menu, OrderType.EatIn).ToLine(item).Item.NormalUnitPrice);
        Assert.Equal(Money.Gbp(100), new MenuOrderBuilder(s.Menu, OrderType.EatIn).ToLine(item).Modifiers[0].UnitPrice); // extras keep one price

        var mainDeal = new MenuOrderBuilder(s.Menu, OrderType.Takeaway).ToLine(ValidDeal(s)).Deal.NormalUnitPrice;
        Assert.Equal(mainDeal + Money.Gbp(200), new MenuOrderBuilder(s.Menu, OrderType.EatIn).ToLine(ValidDeal(s)).Deal.NormalUnitPrice);
    }

    [Fact]
    public void Negative_takeaway_or_eat_in_prices_are_refused_and_changes_are_logged()
    {
        var s = new SampleMenu();
        var doner = s.Menu.GetProduct(s.Doner);
        Assert.Throws<ArgumentException>(() => s.Menu.UpdateProduct(doner with { Variants = [.. doner.Variants.Select(v => v with { EatInPrice = Money.Gbp(-1) })] }, Manager));

        var change = s.Menu.UpdateProduct(doner with { Variants = [.. doner.Variants.Select(v => v with { TakeawayPrice = Money.Gbp(650) })] }, Manager);
        Assert.Contains("takeaway price", change.Summary, StringComparison.Ordinal);
        Assert.Contains("main price → £6.50", change.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Sold_out_items_sizes_and_extras_are_refused()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);

        Assert.Contains("SoldOut", Codes(builder.Check(new ItemSelection(s.Margherita, s.Margherita12, [s.Olives]))));

        s.Menu.SetSoldOut(s.Pepperoni, true, Manager);
        Assert.Contains("SoldOut", Codes(builder.Check(new ItemSelection(s.Pepperoni, s.Pepperoni12, []))));

        var margherita = s.Menu.GetProduct(s.Margherita);
        s.Menu.UpdateProduct(margherita with { Variants = [margherita.Variants[0], margherita.Variants[1] with { IsSoldOut = true }] }, Manager);
        Assert.Contains("SoldOut", Codes(builder.Check(new ItemSelection(s.Margherita, s.Margherita12, []))));
        Assert.Empty(builder.Check(new ItemSelection(s.Margherita, s.Margherita10, [])));
    }

    [Fact]
    public void Extras_limits_are_enforced()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);

        Assert.Contains("ExtraMissing", Codes(builder.Check(new ItemSelection(s.Doner, s.DonerRegular, []))));
        Assert.Contains("TooManyExtras", Codes(builder.Check(new ItemSelection(s.Doner, s.DonerRegular, [s.Garlic, s.Chilli]))));
        Assert.Empty(builder.Check(new ItemSelection(s.Doner, s.DonerRegular, [s.Garlic])));

        var threeToppings = new ItemSelection(s.Margherita, s.Margherita12, [s.ExtraCheese, s.Peppers, s.Olives]);
        Assert.Contains("TooManyExtras", Codes(builder.Check(threeToppings)));
        Assert.Contains("ExtraNotAllowed", Codes(builder.Check(new ItemSelection(s.Margherita, s.Margherita12, [s.Garlic]))));
        Assert.Contains("DuplicateExtra", Codes(builder.Check(new ItemSelection(s.Margherita, s.Margherita12, [s.ExtraCheese, s.ExtraCheese]))));
    }

    [Fact]
    public void Unknown_size_hidden_items_and_bad_quantity_are_refused()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);

        Assert.Contains("SizeNotFound", Codes(builder.Check(new ItemSelection(s.Margherita, s.Pepperoni12, []))));
        Assert.Contains("BadQuantity", Codes(builder.Check(new ItemSelection(s.Can, s.CanRegular, [], Quantity: 0))));
        Assert.Contains("NotOnMenu", Codes(builder.Check(new ItemSelection(Guid.NewGuid(), Guid.NewGuid(), []))));
        Assert.Contains("WrongType", Codes(builder.Check(new ItemSelection(s.Deal, s.DealRegular, []))));

        s.Menu.UpdateCategory(s.Menu.GetCategory(s.Drinks) with { IsHidden = true }, Manager);
        Assert.Contains("Hidden", Codes(builder.Check(new ItemSelection(s.Can, s.CanRegular, []))));
    }

    [Fact]
    public void Invalid_selection_cannot_become_a_line()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);

        var ex = Assert.Throws<InvalidSelectionException>(() => builder.ToLine(new ItemSelection(s.Doner, s.DonerRegular, [])));
        Assert.Equal("ExtraMissing", Assert.Single(ex.Problems).Code);
        Assert.Contains("Sauce: choose at least 1.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_meal_deal_becomes_a_line_with_upcharges_in_slot_order()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);

        // Choices made out of order: drink first.
        var selection = ValidDeal(s) with { Choices = [.. ValidDeal(s).Choices.Reverse()] };
        var line = builder.ToLine(selection);

        Assert.Equal([s.Pepperoni, s.Doner, s.Bottle], line.Components.Select(c => c.Item.ProductId));
        Assert.Equal(Money.Gbp(150), line.Components[0].Upcharge);  // pepperoni is premium
        Assert.Null(line.Components[1].Upcharge);
        Assert.Equal(Money.Gbp(50), line.Components[2].Upcharge);   // bottle instead of a can

        var order = new OrderPricer(new UkVatPolicy(), []).Price(OrderType.Takeaway, Now, [line]);
        Assert.Equal(Money.Gbp(1999 + 150 + 50 + 100), order.Total); // + extra cheese
    }

    [Fact]
    public void Deal_with_a_can_has_no_drink_upcharge()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);
        var selection = ValidDeal(s) with
        {
            Choices = [.. ValidDeal(s).Choices.Take(2), new SlotChoice(s.DrinkSlot, s.Can, s.CanRegular, [])],
        };

        Assert.Null(builder.ToLine(selection).Components[2].Upcharge);
    }

    [Fact]
    public void Deal_rules_are_enforced()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);
        var good = ValidDeal(s);

        // A 10" pizza is not allowed (12" only)
        var tenInch = good with { Choices = [new SlotChoice(s.PizzaSlot, s.Margherita, s.Margherita10, []), .. good.Choices.Skip(1)] };
        Assert.Contains("NotAllowedInDeal", Codes(builder.Check(tenInch)));

        // A kebab in the pizza slot
        var kebabAsPizza = good with { Choices = [new SlotChoice(s.PizzaSlot, s.Doner, s.DonerRegular, [s.Garlic]), .. good.Choices.Skip(1)] };
        Assert.Contains("NotAllowedInDeal", Codes(builder.Check(kebabAsPizza)));

        // Missing drink, two kebabs, a choice for another deal
        Assert.Contains("ChoiceMissing", Codes(builder.Check(good with { Choices = [.. good.Choices.Take(2)] })));
        Assert.Contains("TooManyChoices", Codes(builder.Check(good with { Choices = [.. good.Choices, good.Choices[1]] })));
        Assert.Contains("UnknownSlot", Codes(builder.Check(good with { Choices = [.. good.Choices, new SlotChoice(Guid.NewGuid(), s.Can, s.CanRegular, [])] })));

        // Kebab in the deal still needs its sauce
        var noSauce = good with { Choices = [good.Choices[0], new SlotChoice(s.KebabSlot, s.Doner, s.DonerRegular, []), good.Choices[2]] };
        Assert.Contains("ExtraMissing", Codes(builder.Check(noSauce)));

        // A normal item chosen as a deal
        Assert.Contains("WrongType", Codes(builder.Check(new DealSelection(s.Can, s.CanRegular, []))));

        Assert.Empty(builder.Check(good));
    }

    [Fact]
    public void Sold_out_item_cannot_be_chosen_in_a_deal()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);
        s.Menu.SetSoldOut(s.Doner, true, Manager);

        Assert.Contains("SoldOut", Codes(builder.Check(ValidDeal(s))));
    }

    [Fact]
    public void Price_change_on_the_menu_is_used_by_new_lines_only()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);
        var before = builder.ToLine(new ItemSelection(s.Can, s.CanRegular, []));

        s.Menu.SetPrice(s.Can, s.CanRegular, Money.Gbp(130), Manager);
        var after = builder.ToLine(new ItemSelection(s.Can, s.CanRegular, []));

        Assert.Equal(Money.Gbp(120), before.Item.NormalUnitPrice); // the line already taken keeps its price
        Assert.Equal(Money.Gbp(130), after.Item.NormalUnitPrice);
    }
}
