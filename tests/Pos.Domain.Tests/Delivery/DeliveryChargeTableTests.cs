using Pos.Domain.Common;
using Pos.Domain.Delivery;

namespace Pos.Domain.Tests.Delivery;

public class DeliveryChargeTableTests
{
    // 0–1 mile £1.50, 1–3 miles £2.50, 3–5 miles £3.50 (minimum order £15), up to 5 miles, free over £30.
    private static readonly DeliveryChargeTable Bands = DeliveryChargeTable.Banded(
        [
            new DeliveryBand(0m, 1m, Money.Gbp(150)),
            new DeliveryBand(1m, 3m, Money.Gbp(250)),
            new DeliveryBand(3m, 5m, Money.Gbp(350), MinimumOrder: Money.Gbp(1500)),
        ],
        maximumMiles: 5m,
        freeOver: Money.Gbp(3000));

    [Theory]
    [InlineData(0.0, 150)]
    [InlineData(0.5, 150)]
    [InlineData(1.0, 250)]   // a band's end belongs to the next band
    [InlineData(2.99, 250)]
    [InlineData(3.0, 350)]
    [InlineData(5.0, 350)]   // the last band includes its end
    public void Picks_the_band_for_the_distance(double miles, long expectedFee)
    {
        var quote = Bands.Quote((decimal)miles, Money.Gbp(2000));

        Assert.Equal(DeliveryQuoteStatus.Ok, quote.Status);
        Assert.Equal(Money.Gbp(expectedFee), quote.Fee);
    }

    [Fact]
    public void Too_far_is_refused_with_no_fee()
    {
        var quote = Bands.Quote(5.2m, Money.Gbp(2000));

        Assert.Equal(DeliveryQuoteStatus.BeyondMaximumDistance, quote.Status);
        Assert.Equal(Money.Gbp(0), quote.Fee);
    }

    [Fact]
    public void Band_minimum_order_is_enforced()
    {
        var below = Bands.Quote(3.5m, Money.Gbp(1499));
        var enough = Bands.Quote(3.5m, Money.Gbp(1500));

        Assert.Equal(DeliveryQuoteStatus.BelowMinimumOrder, below.Status);
        Assert.Equal(Money.Gbp(1500), below.MinimumOrder);
        Assert.Equal(DeliveryQuoteStatus.Ok, enough.Status);
    }

    [Fact]
    public void Delivery_is_free_from_the_threshold()
    {
        var free = Bands.Quote(2m, Money.Gbp(3000));
        var notFree = Bands.Quote(2m, Money.Gbp(2999));

        Assert.True(free.IsFreeDelivery);
        Assert.Equal(Money.Gbp(0), free.Fee);
        Assert.False(notFree.IsFreeDelivery);
        Assert.Equal(Money.Gbp(250), notFree.Fee);
    }

    [Theory]
    [InlineData(1.0, 200)]    // £1.00 by distance, raised to the £2.00 minimum
    [InlineData(3.25, 325)]
    [InlineData(2.345, 235)]  // 234.5p rounds to 235p
    [InlineData(7.0, 600)]    // £7.00 by distance, capped at £6.00
    public void Per_mile_fee_is_rounded_and_kept_between_min_and_max(double miles, long expectedFee)
    {
        var table = DeliveryChargeTable.PerMile(Money.Gbp(100), Money.Gbp(200), maximumMiles: 8m, maximumFee: Money.Gbp(600));

        Assert.Equal(Money.Gbp(expectedFee), table.Quote((decimal)miles, Money.Gbp(1000)).Fee);
    }

    [Fact]
    public void Per_mile_table_refuses_beyond_maximum_and_checks_minimum_order()
    {
        var table = DeliveryChargeTable.PerMile(Money.Gbp(100), Money.Gbp(200), maximumMiles: 8m, minimumOrder: Money.Gbp(1000));

        Assert.Equal(DeliveryQuoteStatus.BeyondMaximumDistance, table.Quote(9m, Money.Gbp(5000)).Status);
        Assert.Equal(DeliveryQuoteStatus.BelowMinimumOrder, table.Quote(2m, Money.Gbp(999)).Status);
    }

    [Fact]
    public void Rejects_invalid_tables()
    {
        Assert.Throws<ArgumentException>(() => DeliveryChargeTable.Banded([], 5m));
        Assert.Throws<ArgumentException>(() => DeliveryChargeTable.Banded(
            [new DeliveryBand(0m, 2m, Money.Gbp(100)), new DeliveryBand(1m, 3m, Money.Gbp(200))], 5m));
        Assert.Throws<ArgumentException>(() => DeliveryChargeTable.Banded([new DeliveryBand(2m, 1m, Money.Gbp(100))], 5m));
        Assert.Throws<ArgumentException>(() => DeliveryChargeTable.Banded([new DeliveryBand(0m, 1m, Money.Gbp(-1))], 5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeliveryChargeTable.Banded([new DeliveryBand(0m, 1m, Money.Gbp(100))], 0m));
        Assert.Throws<ArgumentException>(() => DeliveryChargeTable.PerMile(Money.Gbp(100), Money.Gbp(500), 5m, maximumFee: Money.Gbp(400)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bands.Quote(-1m, Money.Gbp(100)));
    }
}
