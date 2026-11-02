using Pos.Domain.Common;

namespace Pos.Domain.Tests.Common;

public class ProportionalAllocatorTests
{
    [Fact]
    public void Splits_by_weight_and_adds_up_exactly()
    {
        var shares = ProportionalAllocator.Allocate(Money.Gbp(100), [Money.Gbp(1), Money.Gbp(1), Money.Gbp(1)]);

        Assert.Equal([Money.Gbp(34), Money.Gbp(33), Money.Gbp(33)], shares);
    }

    [Fact]
    public void Zero_amount_gives_zero_shares()
    {
        var shares = ProportionalAllocator.Allocate(Money.Gbp(0), [Money.Gbp(500), Money.Gbp(250)]);

        Assert.All(shares, s => Assert.Equal(Money.Gbp(0), s));
    }

    [Fact]
    public void Rejects_bad_input()
    {
        Assert.Throws<ArgumentException>(() => ProportionalAllocator.Allocate(Money.Gbp(1), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProportionalAllocator.Allocate(Money.Gbp(-1), [Money.Gbp(1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProportionalAllocator.Allocate(Money.Gbp(1), [Money.Gbp(-1)]));
    }
}
