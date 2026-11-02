using Pos.Domain.Common;
using Pos.Domain.Promotions;

namespace Pos.Domain.Tests.Promotions;

public class PromotionEngineTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly Guid Branch = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid OtherBranch = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Burgers = Guid.Parse("00000000-0000-0000-0000-00000000c001");
    private static readonly Guid Pizzas = Guid.Parse("00000000-0000-0000-0000-00000000c002");
    private static readonly Guid ChickenBurger = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    private static readonly Guid Margherita = Guid.Parse("00000000-0000-0000-0000-00000000a002");
    private static readonly Guid Margherita12 = Guid.Parse("00000000-0000-0000-0000-00000000f012");
    private static readonly Guid Margherita10 = Guid.Parse("00000000-0000-0000-0000-00000000f010");

    private static readonly PricingItem Burger = new(ChickenBurger, null, Burgers, Money.Gbp(899));
    private static readonly PricingItem Pizza12 = new(Margherita, Margherita12, Pizzas, Money.Gbp(1199));

    private static readonly WeeklyTimeWindow TueWed = new([DayOfWeek.Tuesday, DayOfWeek.Wednesday]);

    // 3 Nov 2026 is a Tuesday; the UK is on GMT (UTC+0) in November.
    private static PricingContext At(int year, int month, int day, int hour, int minute = 0, SalesChannel channel = SalesChannel.Pos, Guid? branch = null) =>
        new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero), London, branch ?? Branch, channel);

    private static PricingContext Tuesday => At(2026, 11, 3, 12);

    private static PricingContext Thursday => At(2026, 11, 5, 12);

    private static Promotion BurgerTwoPoundsOff(int priority = 0, IReadOnlySet<SalesChannel>? channels = null, IReadOnlySet<Guid>? branches = null, bool active = true) =>
        new(Guid.NewGuid(), "Burgers £2 off Tue & Wed", PromotionType.AmountOff, 200,
            [new PromotionTarget(PromotionTargetKind.Category, Burgers)], [TueWed],
            priority: priority, channels: channels, branchIds: branches, isActive: active);

    [Fact]
    public void Applies_amount_off_on_offer_days()
    {
        var result = PromotionEngine.Apply(Burger, Tuesday, [BurgerTwoPoundsOff()]);

        Assert.Equal(Money.Gbp(699), result.UnitPrice);
        Assert.Equal(Money.Gbp(899), result.NormalUnitPrice);
        Assert.Equal(Money.Gbp(200), result.UnitSaving);
        Assert.NotNull(result.Promotion);
    }

    [Fact]
    public void Normal_price_on_other_days()
    {
        var result = PromotionEngine.Apply(Burger, Thursday, [BurgerTwoPoundsOff()]);

        Assert.Equal(Money.Gbp(899), result.UnitPrice);
        Assert.Null(result.Promotion);
    }

    [Fact]
    public void Uses_branch_local_time_not_utc()
    {
        // 2 Jun 2026 is a Tuesday. 23:30 UTC is 00:30 Wednesday in London (BST).
        var tuesdayOnly = new Promotion(Guid.NewGuid(), "Tue only", PromotionType.AmountOff, 100,
            [new PromotionTarget(PromotionTargetKind.Product, ChickenBurger)], [new WeeklyTimeWindow([DayOfWeek.Tuesday])]);

        var result = PromotionEngine.Apply(Burger, At(2026, 6, 2, 23, 30), [tuesdayOnly]);

        Assert.Null(result.Promotion);
    }

    [Fact]
    public void Offer_price_sets_a_fixed_price_but_never_raises_it()
    {
        var offer = new Promotion(Guid.NewGuid(), "Pizza £8", PromotionType.OfferPrice, 800,
            [new PromotionTarget(PromotionTargetKind.Product, Margherita)]);
        var cheapItem = Pizza12 with { NormalUnitPrice = Money.Gbp(650) };

        Assert.Equal(Money.Gbp(800), PromotionEngine.Apply(Pizza12, Thursday, [offer]).UnitPrice);
        Assert.Equal(Money.Gbp(650), PromotionEngine.Apply(cheapItem, Thursday, [offer]).UnitPrice);
    }

    [Fact]
    public void Percent_off_rounds_to_the_nearest_penny()
    {
        // 15% of £11.99 = 179.85p -> 180p off -> £10.19
        var offer = new Promotion(Guid.NewGuid(), "15% off pizzas", PromotionType.PercentOff, 1500,
            [new PromotionTarget(PromotionTargetKind.Category, Pizzas)]);

        Assert.Equal(Money.Gbp(1019), PromotionEngine.Apply(Pizza12, Thursday, [offer]).UnitPrice);
    }

    [Fact]
    public void Amount_off_never_goes_below_zero()
    {
        var offer = new Promotion(Guid.NewGuid(), "£20 off", PromotionType.AmountOff, 2000,
            [new PromotionTarget(PromotionTargetKind.Product, ChickenBurger)]);

        Assert.Equal(Money.Gbp(0), PromotionEngine.Apply(Burger, Thursday, [offer]).UnitPrice);
    }

    [Fact]
    public void Variant_target_only_matches_that_size()
    {
        var offer = new Promotion(Guid.NewGuid(), "12 inch deal", PromotionType.AmountOff, 300,
            [new PromotionTarget(PromotionTargetKind.Variant, Margherita12)]);

        Assert.Equal(Money.Gbp(899), PromotionEngine.Apply(Pizza12, Thursday, [offer]).UnitPrice);
        var pizza10 = Pizza12 with { VariantId = Margherita10, NormalUnitPrice = Money.Gbp(899) };
        Assert.Equal(Money.Gbp(899), PromotionEngine.Apply(pizza10, Thursday, [offer]).UnitPrice);
        Assert.Null(PromotionEngine.Apply(pizza10, Thursday, [offer]).Promotion);
    }

    [Fact]
    public void Higher_priority_wins_even_if_less_generous()
    {
        var small = BurgerTwoPoundsOff(priority: 10);
        var big = new Promotion(Guid.NewGuid(), "Half price", PromotionType.PercentOff, 5000,
            [new PromotionTarget(PromotionTargetKind.Product, ChickenBurger)], priority: 1);

        var result = PromotionEngine.Apply(Burger, Tuesday, [big, small]);

        Assert.Same(small, result.Promotion);
        Assert.Equal(Money.Gbp(699), result.UnitPrice);
    }

    [Fact]
    public void Equal_priority_gives_the_customer_the_lower_price_and_offers_do_not_stack()
    {
        var twoOff = BurgerTwoPoundsOff();
        var halfPrice = new Promotion(Guid.NewGuid(), "Half price", PromotionType.PercentOff, 5000,
            [new PromotionTarget(PromotionTargetKind.Product, ChickenBurger)]);

        var result = PromotionEngine.Apply(Burger, Tuesday, [twoOff, halfPrice]);

        Assert.Same(halfPrice, result.Promotion);
        Assert.Equal(Money.Gbp(449), result.UnitPrice); // 50% of 899p = 449.5p -> 450p off
    }

    [Fact]
    public void Respects_branch_and_channel_limits()
    {
        var websiteOnly = BurgerTwoPoundsOff(channels: new HashSet<SalesChannel> { SalesChannel.Website });
        var otherBranchOnly = BurgerTwoPoundsOff(branches: new HashSet<Guid> { OtherBranch });

        Assert.Null(PromotionEngine.Apply(Burger, Tuesday, [websiteOnly]).Promotion);
        Assert.NotNull(PromotionEngine.Apply(Burger, At(2026, 11, 3, 12, channel: SalesChannel.Website), [websiteOnly]).Promotion);
        Assert.Null(PromotionEngine.Apply(Burger, Tuesday, [otherBranchOnly]).Promotion);
    }

    [Fact]
    public void Switched_off_offers_are_ignored()
    {
        Assert.Null(PromotionEngine.Apply(Burger, Tuesday, [BurgerTwoPoundsOff(active: false)]).Promotion);
    }

    [Fact]
    public void Respects_start_and_end_dates()
    {
        var november = new Promotion(Guid.NewGuid(), "November only", PromotionType.AmountOff, 100,
            [new PromotionTarget(PromotionTargetKind.Product, ChickenBurger)],
            startsOn: new DateOnly(2026, 11, 4), endsOn: new DateOnly(2026, 11, 30));

        Assert.Null(PromotionEngine.Apply(Burger, Tuesday, [november]).Promotion);          // 3 Nov: before start
        Assert.NotNull(PromotionEngine.Apply(Burger, Thursday, [november]).Promotion);      // 5 Nov
        Assert.NotNull(PromotionEngine.Apply(Burger, At(2026, 11, 30, 23, 59), [november]).Promotion); // last day included
        Assert.Null(PromotionEngine.Apply(Burger, At(2026, 12, 1, 0, 0), [november]).Promotion);
    }

    [Theory]
    [InlineData(PromotionType.PercentOff, 0)]
    [InlineData(PromotionType.PercentOff, 10_001)]
    [InlineData(PromotionType.AmountOff, 0)]
    [InlineData(PromotionType.OfferPrice, -1)]
    public void Rejects_invalid_values(PromotionType type, long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Promotion(Guid.NewGuid(), "Bad", type, value,
            [new PromotionTarget(PromotionTargetKind.Product, ChickenBurger)]));
    }

    [Fact]
    public void Rejects_offer_with_no_targets_or_end_before_start()
    {
        Assert.Throws<ArgumentException>(() => new Promotion(Guid.NewGuid(), "Bad", PromotionType.AmountOff, 100, []));
        Assert.Throws<ArgumentException>(() => new Promotion(Guid.NewGuid(), "Bad", PromotionType.AmountOff, 100,
            [new PromotionTarget(PromotionTargetKind.Product, ChickenBurger)],
            startsOn: new DateOnly(2026, 11, 10), endsOn: new DateOnly(2026, 11, 1)));
    }
}
