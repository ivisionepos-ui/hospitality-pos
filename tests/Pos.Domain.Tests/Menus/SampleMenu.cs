using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Tax;

namespace Pos.Domain.Tests.Menus;

/// <summary>A clock the tests control.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A small takeaway menu used by the menu tests.</summary>
internal sealed class SampleMenu
{
    public static readonly Guid Owner = Guid.NewGuid();

    public readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
    public readonly Menu Menu;

    public readonly Guid Mains = Guid.NewGuid();
    public readonly Guid DrinksGroup = Guid.NewGuid();

    public readonly Guid Pizzas = Guid.NewGuid();
    public readonly Guid Kebabs = Guid.NewGuid();
    public readonly Guid Drinks = Guid.NewGuid();
    public readonly Guid Deals = Guid.NewGuid();

    public readonly Guid Toppings = Guid.NewGuid();
    public readonly Guid ExtraCheese = Guid.NewGuid();
    public readonly Guid Peppers = Guid.NewGuid();
    public readonly Guid Olives = Guid.NewGuid();
    public readonly Guid Sauce = Guid.NewGuid();
    public readonly Guid Garlic = Guid.NewGuid();
    public readonly Guid Chilli = Guid.NewGuid();

    public readonly Guid Margherita = Guid.NewGuid();
    public readonly Guid Margherita10 = Guid.NewGuid();
    public readonly Guid Margherita12 = Guid.NewGuid();
    public readonly Guid Pepperoni = Guid.NewGuid();
    public readonly Guid Pepperoni12 = Guid.NewGuid();
    public readonly Guid Doner = Guid.NewGuid();
    public readonly Guid DonerRegular = Guid.NewGuid();
    public readonly Guid Can = Guid.NewGuid();
    public readonly Guid CanRegular = Guid.NewGuid();
    public readonly Guid Bottle = Guid.NewGuid();
    public readonly Guid BottleRegular = Guid.NewGuid();

    public readonly Guid Deal = Guid.NewGuid();
    public readonly Guid DealRegular = Guid.NewGuid();
    public readonly Guid PizzaSlot = Guid.NewGuid();
    public readonly Guid KebabSlot = Guid.NewGuid();
    public readonly Guid DrinkSlot = Guid.NewGuid();

    public SampleMenu()
    {
        Menu = new Menu(clock: Clock);
        var u = Owner;

        Menu.AddCourseGroup(new CourseGroup(Mains, new LocalizedText("Main courses", "الأطباق الرئيسية"), 1), u);
        Menu.AddCourseGroup(new CourseGroup(DrinksGroup, new LocalizedText("Drinks", "المشروبات"), 4), u);

        Menu.AddCategory(new Category { Id = Pizzas, Name = new LocalizedText("Pizzas", "بيتزا"), CourseGroupId = Mains }, u);
        Menu.AddCategory(new Category { Id = Kebabs, Name = new LocalizedText("Kebabs"), CourseGroupId = Mains }, u);
        Menu.AddCategory(new Category { Id = Drinks, Name = new LocalizedText("Drinks"), CourseGroupId = DrinksGroup }, u);
        Menu.AddCategory(new Category { Id = Deals, Name = new LocalizedText("Meal deals") }, u);

        Menu.SaveModifierGroup(new ModifierGroup(Toppings, new LocalizedText("Extra toppings"), 0, 2,
        [
            new ModifierOption(ExtraCheese, new LocalizedText("Extra cheese"), Money.Gbp(100)),
            new ModifierOption(Peppers, new LocalizedText("Peppers"), Money.Gbp(75)),
            new ModifierOption(Olives, new LocalizedText("Olives"), Money.Gbp(75), IsSoldOut: true),
        ]), u);
        Menu.SaveModifierGroup(new ModifierGroup(Sauce, new LocalizedText("Sauce"), 1, 1,
        [
            new ModifierOption(Garlic, new LocalizedText("Garlic"), Money.Gbp(0)),
            new ModifierOption(Chilli, new LocalizedText("Chilli"), Money.Gbp(0)),
        ]), u);

        Menu.AddProduct(new Product
        {
            Id = Margherita, CategoryId = Pizzas, Name = new LocalizedText("Margherita", "مارغريتا"), VatCategory = VatCategory.HotFood,
            Variants = [new ProductVariant(Margherita10, new LocalizedText("10\""), Money.Gbp(899)), new ProductVariant(Margherita12, new LocalizedText("12\""), Money.Gbp(1199))],
            ModifierGroupIds = [Toppings],
        }, u);
        Menu.AddProduct(new Product
        {
            Id = Pepperoni, CategoryId = Pizzas, Name = new LocalizedText("Pepperoni"), VatCategory = VatCategory.HotFood,
            Variants = [new ProductVariant(Pepperoni12, new LocalizedText("12\""), Money.Gbp(1299))],
            ModifierGroupIds = [Toppings],
        }, u);
        Menu.AddProduct(new Product
        {
            Id = Doner, CategoryId = Kebabs, Name = new LocalizedText("Doner kebab"), VatCategory = VatCategory.HotFood,
            Variants = [new ProductVariant(DonerRegular, new LocalizedText("Regular"), Money.Gbp(899))],
            ModifierGroupIds = [Sauce],
        }, u);
        Menu.AddProduct(new Product
        {
            Id = Can, CategoryId = Drinks, Name = new LocalizedText("Can 330ml"), VatCategory = VatCategory.AlwaysStandard,
            Variants = [new ProductVariant(CanRegular, new LocalizedText("Regular"), Money.Gbp(120))],
        }, u);
        Menu.AddProduct(new Product
        {
            Id = Bottle, CategoryId = Drinks, Name = new LocalizedText("Bottle 500ml"), VatCategory = VatCategory.AlwaysStandard,
            Variants = [new ProductVariant(BottleRegular, new LocalizedText("Regular"), Money.Gbp(200))],
        }, u);

        // Pizza & Kebab Deal £19.99: any 12" pizza (+£1.50 for pepperoni), a doner, any drink (+£0.50 for a bottle).
        Menu.AddProduct(new Product
        {
            Id = Deal, CategoryId = Deals, Name = new LocalizedText("Pizza & Kebab Deal"), Type = ProductType.MealDeal,
            VatCategory = VatCategory.HotFood,
            Variants = [new ProductVariant(DealRegular, new LocalizedText("Regular"), Money.Gbp(1999))],
            DealSlots =
            [
                new MealDealSlot(PizzaSlot, new LocalizedText("Choose a pizza"), 1, 1,
                [
                    new SlotOption(SlotOptionKind.Variant, Margherita12),
                    new SlotOption(SlotOptionKind.Variant, Pepperoni12, Money.Gbp(150)),
                ]),
                new MealDealSlot(KebabSlot, new LocalizedText("Choose a kebab"), 1, 1, [new SlotOption(SlotOptionKind.Product, Doner)]),
                new MealDealSlot(DrinkSlot, new LocalizedText("Choose a drink"), 1, 1,
                [
                    new SlotOption(SlotOptionKind.Category, Drinks),
                    new SlotOption(SlotOptionKind.Product, Bottle, Money.Gbp(50)),
                ]),
            ],
        }, u);
    }
}
