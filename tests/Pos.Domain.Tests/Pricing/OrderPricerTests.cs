using Pos.Domain.Common;
using Pos.Domain.Pricing;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;

namespace Pos.Domain.Tests.Pricing;

public class OrderPricerTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly Guid Branch = Guid.NewGuid();

    private static readonly Guid Burgers = Guid.NewGuid();
    private static readonly Guid Sandwiches = Guid.NewGuid();
    private static readonly Guid Deals = Guid.NewGuid();
    private static readonly Guid Pizzas = Guid.NewGuid();
    private static readonly Guid Kebabs = Guid.NewGuid();
    private static readonly Guid Salads = Guid.NewGuid();

    private static readonly PricingItem Burger = new(Guid.NewGuid(), null, Burgers, Money.Gbp(899));
    private static readonly PricingItem ColdSandwich = new(Guid.NewGuid(), null, Sandwiches, Money.Gbp(450));
    private static readonly PricingItem PizzaKebabDeal = new(Guid.NewGuid(), null, Deals, Money.Gbp(1999));
    private static readonly PricingItem Pizza = new(Guid.NewGuid(), null, Pizzas, Money.Gbp(1199));
    private static readonly PricingItem Kebab = new(Guid.NewGuid(), null, Kebabs, Money.Gbp(899));
    private static readonly PricingItem Salad = new(Guid.NewGuid(), null, Salads, Money.Gbp(300));

    private static readonly Promotion BurgersTueWed = new(Guid.NewGuid(), "Burgers £2 off Tue & Wed", PromotionType.AmountOff, 200,
        [new PromotionTarget(PromotionTargetKind.Category, Burgers)],
        [new WeeklyTimeWindow([DayOfWeek.Tuesday, DayOfWeek.Wednesday])]);

    // 3 Nov 2026 is a Tuesday, 5 Nov a Thursday.
    private static PricingContext Tuesday => new(new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero), London, Branch, SalesChannel.Pos);

    private static PricingContext Thursday => new(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero), London, Branch, SalesChannel.Pos);

    private static OrderPricer Pricer(params Promotion[] promotions) => new(new UkVatPolicy(), promotions);

    private static List<OrderLineInput> SampleOrder() =>
    [
        new ItemLineInput
        {
            Item = Burger, VatCategory = VatCategory.HotFood, Quantity = 2,
            Modifiers = [new ModifierInput(Guid.NewGuid(), Money.Gbp(50))], // extra cheese
        },
        new ItemLineInput { Item = ColdSandwich, VatCategory = VatCategory.ColdFood, Quantity = 1 },
        new MealDealLineInput
        {
            Deal = PizzaKebabDeal, Quantity = 1,
            Components =
            [
                new DealComponentInput
                {
                    Item = Pizza, VatCategory = VatCategory.HotFood, Upcharge = Money.Gbp(150),
                    Modifiers = [new ModifierInput(Guid.NewGuid(), Money.Gbp(100))],
                },
                new DealComponentInput { Item = Kebab, VatCategory = VatCategory.HotFood },
                new DealComponentInput { Item = Salad, VatCategory = VatCategory.ColdFood },
            ],
        },
    ];

    [Fact]
    public void Prices_a_mixed_takeaway_order_with_offer_extras_and_meal_deal()
    {
        var order = Pricer(BurgersTueWed).Price(OrderType.Takeaway, Tuesday, SampleOrder());

        // Burgers: (899 - 200 offer + 50 cheese) x 2 = 1498
        var burgers = order.Lines[0];
        Assert.Equal(Money.Gbp(749), burgers.UnitTotal);
        Assert.Equal(Money.Gbp(1498), burgers.LineTotal);
        Assert.Equal(Money.Gbp(400), burgers.Saving);
        Assert.Equal(2000, burgers.VatRate!.BasisPoints);

        // Cold sandwich to take away: zero rated
        Assert.Equal(0, order.Lines[1].VatRate!.BasisPoints);

        // Deal £19.99 shared 1000 / 749 / 250 by normal prices 1199 / 899 / 300
        var deal = order.Lines[2];
        Assert.Null(deal.VatRate);
        Assert.Equal([Money.Gbp(1000), Money.Gbp(749), Money.Gbp(250)], deal.Components.Select(c => c.AllocatedShare));
        Assert.Equal(Money.Gbp(1250), deal.Components[0].UnitTotal); // share + £1.50 premium + £1.00 extra
        Assert.Equal(0, deal.Components[2].VatRate.BasisPoints);     // cold salad to take away
        Assert.Equal(Money.Gbp(2249), deal.LineTotal);

        Assert.Equal(Money.Gbp(4197), order.Total);
        Assert.Equal(Money.Gbp(400), order.TotalSaving);

        // VAT: standard gross 1498 + 1250 + 749 = 3497 -> VAT 582.83 -> 583; zero gross 450 + 250 = 700
        Assert.Equal(2, order.VatSummary.Count);
        Assert.Equal(Money.Gbp(3497), order.VatSummary[0].Gross);
        Assert.Equal(Money.Gbp(583), order.VatSummary[0].Vat);
        Assert.Equal(Money.Gbp(2914), order.VatSummary[0].Net);
        Assert.Equal(Money.Gbp(700), order.VatSummary[1].Gross);
        Assert.Equal(Money.Gbp(0), order.VatSummary[1].Vat);
    }

    [Fact]
    public void Same_order_eaten_in_is_all_standard_rated()
    {
        var order = Pricer(BurgersTueWed).Price(OrderType.EatIn, Tuesday, SampleOrder());

        var summary = Assert.Single(order.VatSummary);
        Assert.Equal(Money.Gbp(4197), summary.Gross);
        Assert.Equal(Money.Gbp(700), summary.Vat); // 699.5p -> 700p
    }

    [Fact]
    public void Offer_does_not_apply_on_thursday()
    {
        var order = Pricer(BurgersTueWed).Price(OrderType.Takeaway, Thursday, SampleOrder());

        Assert.Equal(Money.Gbp(949), order.Lines[0].UnitTotal);
        Assert.Equal(Money.Gbp(0), order.TotalSaving);
        Assert.Equal(Money.Gbp(4597), order.Total);
    }

    [Fact]
    public void Offer_on_the_deal_itself_lowers_the_shared_price()
    {
        var tuesdayDeal = new Promotion(Guid.NewGuid(), "Tuesday deal £17.99", PromotionType.OfferPrice, 1799,
            [new PromotionTarget(PromotionTargetKind.Product, PizzaKebabDeal.ProductId)],
            [new WeeklyTimeWindow([DayOfWeek.Tuesday])]);

        var order = Pricer(tuesdayDeal).Price(OrderType.Takeaway, Tuesday, SampleOrder());
        var deal = order.Lines[2];

        Assert.Equal(1799, deal.Components.Sum(c => c.AllocatedShare.MinorUnits));
        Assert.Equal(Money.Gbp(200), deal.Saving);
        Assert.Equal(Money.Gbp(2049), deal.LineTotal); // 1799 + 150 + 100
    }

    [Fact]
    public void Vat_summary_always_adds_up_to_the_total()
    {
        var order = Pricer(BurgersTueWed).Price(OrderType.Delivery, Tuesday, SampleOrder());

        var summaryGross = order.VatSummary.Aggregate(Money.Gbp(0), (s, l) => s + l.Gross);
        var summaryParts = order.VatSummary.Aggregate(Money.Gbp(0), (s, l) => s + l.Net + l.Vat);
        Assert.Equal(order.Total, summaryGross);
        Assert.Equal(order.Total, summaryParts);
    }

    [Fact]
    public void Deal_quantity_multiplies_every_item()
    {
        var lines = SampleOrder();
        lines[2] = ((MealDealLineInput)lines[2]) with { Quantity = 3 };

        var order = Pricer().Price(OrderType.Takeaway, Thursday, lines);

        Assert.Equal(Money.Gbp(6747), order.Lines[2].LineTotal); // 2249 x 3
    }

    [Fact]
    public void Rejects_zero_quantity_and_empty_deals()
    {
        var zeroQty = new List<OrderLineInput> { new ItemLineInput { Item = Burger, VatCategory = VatCategory.HotFood, Quantity = 0 } };
        var emptyDeal = new List<OrderLineInput> { new MealDealLineInput { Deal = PizzaKebabDeal, Quantity = 1, Components = [] } };

        Assert.Throws<ArgumentOutOfRangeException>(() => Pricer().Price(OrderType.Takeaway, Thursday, zeroQty));
        Assert.Throws<ArgumentException>(() => Pricer().Price(OrderType.Takeaway, Thursday, emptyDeal));
    }

    [Fact]
    public void Empty_order_totals_zero()
    {
        var order = Pricer().Price(OrderType.Takeaway, Thursday, []);

        Assert.Equal(Money.Gbp(0), order.Total);
        Assert.Empty(order.VatSummary);
    }
}
