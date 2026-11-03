using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Tax;

namespace Pos.Hub;

/// <summary>
/// A typical UK takeaway menu for trying the till before the real menu is entered.
/// Loaded only when the hub's menu is empty, so it can never overwrite a real menu.
/// </summary>
public static class SampleMenu
{
    /// <summary>Builds the sample menu with fresh ids.</summary>
    public static MenuState Build()
    {
        var starters = new CourseGroup(Guid.NewGuid(), new LocalizedText("Starters"), 0);
        var mains = new CourseGroup(Guid.NewGuid(), new LocalizedText("Main courses"), 1);
        var sides = new CourseGroup(Guid.NewGuid(), new LocalizedText("Side orders"), 2);
        var desserts = new CourseGroup(Guid.NewGuid(), new LocalizedText("Desserts"), 3);
        var drinks = new CourseGroup(Guid.NewGuid(), new LocalizedText("Drinks"), 4);

        var categories = new List<Category>();
        Category Cat(string name, CourseGroup? course, string colour)
        {
            var category = new Category { Id = Guid.NewGuid(), Name = new LocalizedText(name), CourseGroupId = course?.Id, SortOrder = categories.Count, Colour = colour };
            categories.Add(category);
            return category;
        }

        var dealsCat = Cat("Meal deals", null, "#7c3aed");
        var startersCat = Cat("Starters", starters, "#0f766e");
        var pizzasCat = Cat("Pizzas", mains, "#c2410c");
        var kebabsCat = Cat("Kebabs", mains, "#b91c1c");
        var burgersCat = Cat("Burgers", mains, "#a16207");
        var sidesCat = Cat("Sides", sides, "#4d7c0f");
        var dessertsCat = Cat("Desserts", desserts, "#be185d");
        var drinksCat = Cat("Drinks", drinks, "#1d4ed8");

        static ModifierOption Option(string name, long pence) => new(Guid.NewGuid(), new LocalizedText(name), Money.Gbp(pence));
        var toppings = new ModifierGroup(Guid.NewGuid(), new LocalizedText("Extra toppings"), 0, 5,
            [Option("Extra cheese", 100), Option("Pepperoni", 100), Option("Mushrooms", 80), Option("Peppers", 80), Option("Jalapeños", 80), Option("Olives", 80)]);
        var sauce = new ModifierGroup(Guid.NewGuid(), new LocalizedText("Sauce"), 1, 2,
            [Option("Garlic", 0), Option("Chilli", 0), Option("Mint", 0), Option("BBQ", 0), Option("Burger sauce", 0)]);
        var salad = new ModifierGroup(Guid.NewGuid(), new LocalizedText("Salad"), 0, 4,
            [Option("No onion", 0), Option("No lettuce", 0), Option("No tomato", 0), Option("No cabbage", 0)]);
        var burgerExtras = new ModifierGroup(Guid.NewGuid(), new LocalizedText("Burger extras"), 0, 3,
            [Option("Extra cheese", 60), Option("Bacon", 100), Option("Extra patty", 200)]);

        var products = new List<Product>();
        Product Item(Category category, string name, VatCategory vat, (string Size, long Pence)[] sizes, params ModifierGroup[] groups)
        {
            var product = new Product
            {
                Id = Guid.NewGuid(),
                CategoryId = category.Id,
                Name = new LocalizedText(name),
                VatCategory = vat,
                SortOrder = products.Count(p => p.CategoryId == category.Id),
                Variants = [.. sizes.Select(s => new ProductVariant(Guid.NewGuid(), new LocalizedText(s.Size), Money.Gbp(s.Pence)))],
                ModifierGroupIds = [.. groups.Select(g => g.Id)],
            };
            products.Add(product);
            return product;
        }

        (string, long)[] One(long pence) => [("Regular", pence)];
        (string, long)[] Pizza(long ten, long twelve, long fourteen) => [("10\"", ten), ("12\"", twelve), ("14\"", fourteen)];
        (string, long)[] Kebab(long regular, long large) => [("Regular", regular), ("Large", large)];

        Item(startersCat, "Garlic bread", VatCategory.HotFood, One(350));
        Item(startersCat, "Garlic bread with cheese", VatCategory.HotFood, One(420));
        Item(startersCat, "Chicken wings (6)", VatCategory.HotFood, One(499), sauce);
        Item(startersCat, "Onion rings", VatCategory.HotFood, One(299));

        var pizzas = new[]
        {
            Item(pizzasCat, "Margherita", VatCategory.HotFood, Pizza(799, 999, 1199), toppings),
            Item(pizzasCat, "Pepperoni", VatCategory.HotFood, Pizza(899, 1099, 1299), toppings),
            Item(pizzasCat, "Hawaiian", VatCategory.HotFood, Pizza(899, 1099, 1299), toppings),
            Item(pizzasCat, "Vegetarian", VatCategory.HotFood, Pizza(899, 1099, 1299), toppings),
            Item(pizzasCat, "Meat feast", VatCategory.HotFood, Pizza(999, 1199, 1399), toppings),
        };

        Item(kebabsCat, "Doner kebab", VatCategory.HotFood, Kebab(799, 999), sauce, salad);
        Item(kebabsCat, "Chicken shish", VatCategory.HotFood, Kebab(899, 1099), sauce, salad);
        Item(kebabsCat, "Mixed kebab", VatCategory.HotFood, Kebab(999, 1299), sauce, salad);
        Item(kebabsCat, "Lamb shish", VatCategory.HotFood, Kebab(999, 1199), sauce, salad);

        Item(burgersCat, "Cheeseburger", VatCategory.HotFood, One(599), burgerExtras);
        Item(burgersCat, "Double cheeseburger", VatCategory.HotFood, One(799), burgerExtras);
        Item(burgersCat, "Chicken fillet burger", VatCategory.HotFood, One(649), burgerExtras);

        Item(sidesCat, "Chips", VatCategory.HotFood, [("Regular", 250), ("Large", 350)]);
        Item(sidesCat, "Cheesy chips", VatCategory.HotFood, [("Regular", 350), ("Large", 450)]);
        Item(sidesCat, "Coleslaw", VatCategory.ColdFood, One(150));

        Item(dessertsCat, "Chocolate fudge cake", VatCategory.HotFood, One(399));
        Item(dessertsCat, "Cheesecake", VatCategory.ColdFood, One(399));
        Item(dessertsCat, "Ice cream tub", VatCategory.AlwaysStandard, One(499));

        var drinkItems = new[]
        {
            Item(drinksCat, "Can 330ml", VatCategory.AlwaysStandard, One(120)),
            Item(drinksCat, "Bottle 1.5L", VatCategory.AlwaysStandard, One(299)),
            Item(drinksCat, "Water 500ml", VatCategory.AlwaysStandard, One(100)),
        };

        var twelveInch = pizzas.Select(p => p.Variants[1]).ToList();
        products.Add(new Product
        {
            Id = Guid.NewGuid(),
            CategoryId = dealsCat.Id,
            Name = new LocalizedText("Pizza, kebab & drink"),
            ButtonName = new LocalizedText("Pizza + Kebab + Drink"),
            Type = ProductType.MealDeal,
            VatCategory = VatCategory.HotFood,
            Variants = [new ProductVariant(Guid.NewGuid(), new LocalizedText("Regular"), Money.Gbp(1999))],
            DealSlots =
            [
                new MealDealSlot(Guid.NewGuid(), new LocalizedText("Choose a 12\" pizza"), 1, 1,
                    [.. twelveInch.Select(v => new SlotOption(SlotOptionKind.Variant, v.Id))]),
                new MealDealSlot(Guid.NewGuid(), new LocalizedText("Choose a kebab"), 1, 1, [new SlotOption(SlotOptionKind.Category, kebabsCat.Id)]),
                new MealDealSlot(Guid.NewGuid(), new LocalizedText("Choose a drink"), 1, 1, [new SlotOption(SlotOptionKind.Category, drinksCat.Id)]),
            ],
        });
        products.Add(new Product
        {
            Id = Guid.NewGuid(),
            CategoryId = dealsCat.Id,
            Name = new LocalizedText("Burger meal"),
            Type = ProductType.MealDeal,
            VatCategory = VatCategory.HotFood,
            SortOrder = 1,
            Variants = [new ProductVariant(Guid.NewGuid(), new LocalizedText("Regular"), Money.Gbp(899))],
            DealSlots =
            [
                new MealDealSlot(Guid.NewGuid(), new LocalizedText("Choose a burger"), 1, 1, [new SlotOption(SlotOptionKind.Category, burgersCat.Id)]),
                new MealDealSlot(Guid.NewGuid(), new LocalizedText("Chips"), 1, 1, [new SlotOption(SlotOptionKind.Product, products.First(p => p.Name.En == "Chips").Id)]),
                new MealDealSlot(Guid.NewGuid(), new LocalizedText("Choose a drink"), 1, 1, [.. drinkItems.Take(1).Select(d => new SlotOption(SlotOptionKind.Product, d.Id))]),
            ],
        });

        return new MenuState("GBP", 0, [starters, mains, sides, desserts, drinks], categories, [toppings, sauce, salad, burgerExtras], products);
    }
}
