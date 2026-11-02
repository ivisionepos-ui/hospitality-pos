using Pos.Domain.Common;
using Pos.Domain.MealDeals;

namespace Pos.Domain.Tests.MealDeals;

public class MealDealAllocatorTests
{
    [Fact]
    public void Shares_the_deal_price_by_normal_prices_to_the_penny()
    {
        // Pizza £11.99, kebab £8.99, can £1.20 = £22.18 normally; deal £19.99.
        var shares = MealDealAllocator.Allocate(Money.Gbp(1999), [Money.Gbp(1199), Money.Gbp(899), Money.Gbp(120)]);

        Assert.Equal([Money.Gbp(1081), Money.Gbp(810), Money.Gbp(108)], shares);
    }

    [Fact]
    public void Shares_always_add_up_exactly_for_many_combinations()
    {
        var random = new Random(12345);
        for (var run = 0; run < 2000; run++)
        {
            var count = random.Next(1, 6);
            var prices = Enumerable.Range(0, count).Select(_ => Money.Gbp(random.Next(0, 2500))).ToList();
            var deal = Money.Gbp(random.Next(0, 5000));

            var shares = MealDealAllocator.Allocate(deal, prices);

            Assert.Equal(count, shares.Count);
            Assert.Equal(deal.MinorUnits, shares.Sum(s => s.MinorUnits));
            Assert.All(shares, s => Assert.False(s.IsNegative));
        }
    }

    [Fact]
    public void Splits_equally_when_all_items_are_free()
    {
        var shares = MealDealAllocator.Allocate(Money.Gbp(1000), [Money.Gbp(0), Money.Gbp(0), Money.Gbp(0)]);

        Assert.Equal([Money.Gbp(334), Money.Gbp(333), Money.Gbp(333)], shares);
    }

    [Fact]
    public void Free_item_in_a_paid_deal_gets_no_share()
    {
        var shares = MealDealAllocator.Allocate(Money.Gbp(1000), [Money.Gbp(800), Money.Gbp(0)]);

        Assert.Equal([Money.Gbp(1000), Money.Gbp(0)], shares);
    }

    [Fact]
    public void Rejects_empty_deals_and_negative_prices()
    {
        Assert.Throws<ArgumentException>(() => MealDealAllocator.Allocate(Money.Gbp(100), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => MealDealAllocator.Allocate(Money.Gbp(-1), [Money.Gbp(1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MealDealAllocator.Allocate(Money.Gbp(1), [Money.Gbp(-1)]));
    }
}
