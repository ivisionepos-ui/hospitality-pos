using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Tax;

namespace Pos.Hub.Tests;

/// <summary>A clock the tests control.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Runs a real hub in memory with its own database file, and talks to it over HTTP.</summary>
internal sealed class HubTestHost : WebApplicationFactory<Program>
{
    public HubTestHost(string databasePath, TestClock clock)
    {
        DatabasePath = databasePath;
        Clock = clock;
    }

    public string DatabasePath { get; }

    public TestClock Clock { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Hub:DatabasePath", DatabasePath);
        builder.UseSetting("Hub:BranchCode", "TEST1");
        builder.UseSetting("Hub:StaffDiscountMaxPercent", "1000");
        builder.UseSetting("Hub:StaffDiscountMaxPence", "500");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>The test menu: pizzas, kebabs (sauce required), drinks and a pizza-kebab-drink deal.</summary>
internal static class TestMenu
{
    public static readonly Guid Owner = Guid.Parse("10000000-0000-0000-0000-000000000001");

    public static readonly Guid Mains = Guid.NewGuid();
    public static readonly Guid DrinksGroup = Guid.NewGuid();
    public static readonly Guid Pizzas = Guid.NewGuid();
    public static readonly Guid Kebabs = Guid.NewGuid();
    public static readonly Guid Drinks = Guid.NewGuid();
    public static readonly Guid Deals = Guid.NewGuid();
    public static readonly Guid Sauce = Guid.NewGuid();
    public static readonly Guid Garlic = Guid.NewGuid();
    public static readonly Guid Margherita = Guid.NewGuid();
    public static readonly Guid Margherita12 = Guid.NewGuid();
    public static readonly Guid Doner = Guid.NewGuid();
    public static readonly Guid DonerRegular = Guid.NewGuid();
    public static readonly Guid Can = Guid.NewGuid();
    public static readonly Guid CanRegular = Guid.NewGuid();
    public static readonly Guid Deal = Guid.NewGuid();
    public static readonly Guid DealRegular = Guid.NewGuid();
    public static readonly Guid PizzaSlot = Guid.NewGuid();
    public static readonly Guid KebabSlot = Guid.NewGuid();
    public static readonly Guid DrinkSlot = Guid.NewGuid();

    public static MenuState State() => new(
        "GBP",
        0,
        [new CourseGroup(Mains, new LocalizedText("Main courses", "الأطباق الرئيسية"), 1), new CourseGroup(DrinksGroup, new LocalizedText("Drinks"), 4)],
        [
            new Category { Id = Pizzas, Name = new LocalizedText("Pizzas", "بيتزا"), CourseGroupId = Mains, SortOrder = 0 },
            new Category { Id = Kebabs, Name = new LocalizedText("Kebabs"), CourseGroupId = Mains, SortOrder = 1 },
            new Category { Id = Drinks, Name = new LocalizedText("Drinks"), CourseGroupId = DrinksGroup, SortOrder = 2 },
            new Category { Id = Deals, Name = new LocalizedText("Meal deals"), SortOrder = 3 },
        ],
        [new ModifierGroup(Sauce, new LocalizedText("Sauce"), 1, 1, [new ModifierOption(Garlic, new LocalizedText("Garlic"), Money.Gbp(0))])],
        [
            new Product
            {
                Id = Margherita, CategoryId = Pizzas, Name = new LocalizedText("Margherita"), VatCategory = VatCategory.HotFood,
                Variants = [new ProductVariant(Margherita12, new LocalizedText("12\""), Money.Gbp(1199))],
            },
            new Product
            {
                Id = Doner, CategoryId = Kebabs, Name = new LocalizedText("Doner kebab"), VatCategory = VatCategory.HotFood,
                Variants = [new ProductVariant(DonerRegular, new LocalizedText("Regular"), Money.Gbp(899))], ModifierGroupIds = [Sauce],
            },
            new Product
            {
                Id = Can, CategoryId = Drinks, Name = new LocalizedText("Can"), VatCategory = VatCategory.AlwaysStandard,
                Variants = [new ProductVariant(CanRegular, new LocalizedText("Regular"), Money.Gbp(120))],
            },
            new Product
            {
                Id = Deal, CategoryId = Deals, Name = new LocalizedText("Pizza & Kebab Deal"), Type = ProductType.MealDeal,
                VatCategory = VatCategory.HotFood, Variants = [new ProductVariant(DealRegular, new LocalizedText("Regular"), Money.Gbp(1999))],
                DealSlots =
                [
                    new MealDealSlot(PizzaSlot, new LocalizedText("Choose a pizza"), 1, 1, [new SlotOption(SlotOptionKind.Category, Pizzas)]),
                    new MealDealSlot(KebabSlot, new LocalizedText("Choose a kebab"), 1, 1, [new SlotOption(SlotOptionKind.Category, Kebabs)]),
                    new MealDealSlot(DrinkSlot, new LocalizedText("Choose a drink"), 1, 1, [new SlotOption(SlotOptionKind.Category, Drinks)]),
                ],
            },
        ]);
}

/// <summary>Small helpers for calling the hub with the shared JSON format.</summary>
internal static class HttpHelpers
{
    public static Task<HttpResponseMessage> Post<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, PosJson.Options);

    public static Task<HttpResponseMessage> Put<T>(this HttpClient client, string url, T body) =>
        client.PutAsJsonAsync(url, body, PosJson.Options);

    public static async Task<T> Read<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(PosJson.Options))!;

    public static async Task<T> GetJson<T>(this HttpClient client, string url) =>
        (await client.GetFromJsonAsync<T>(url, PosJson.Options))!;
}
