using Pos.Domain.Common;

namespace Pos.Domain.Tests.Common;

public class MoneyTests
{
    [Fact]
    public void Adds_and_subtracts_in_pence()
    {
        var total = Money.Gbp(1250) + Money.Gbp(199) - Money.Gbp(49);
        Assert.Equal(Money.Gbp(1400), total);
    }

    [Fact]
    public void Multiplies_by_quantity()
    {
        Assert.Equal(Money.Gbp(2997), Money.Gbp(999) * 3);
    }

    [Fact]
    public void Refuses_to_mix_currencies()
    {
        Assert.Throws<InvalidOperationException>(() => Money.Gbp(100) + new Money(100, "EUR"));
    }

    [Fact]
    public void Rejects_invalid_currency_codes()
    {
        Assert.Throws<ArgumentException>(() => new Money(100, "POUNDS"));
    }

    [Fact]
    public void Normalises_currency_code_to_upper_case()
    {
        Assert.Equal(Money.Gbp(5), new Money(5, "gbp"));
    }

    [Theory]
    [InlineData(1250, "GBP 12.50")]
    [InlineData(5, "GBP 0.05")]
    [InlineData(-199, "GBP -1.99")]
    [InlineData(0, "GBP 0.00")]
    public void Formats_for_logs(long pence, string expected)
    {
        Assert.Equal(expected, Money.Gbp(pence).ToString());
    }

    [Theory]
    [InlineData(1250, "£12.50")]
    [InlineData(5, "£0.05")]
    [InlineData(-199, "-£1.99")]
    [InlineData(123456, "£1,234.56")]
    public void Formats_for_staff_messages(long pence, string expected)
    {
        Assert.Equal(expected, Money.Gbp(pence).Display());
    }

    [Fact]
    public void Compares_amounts()
    {
        Assert.True(Money.Gbp(100) < Money.Gbp(101));
        Assert.Equal(Money.Gbp(100), Money.Min(Money.Gbp(100), Money.Gbp(250)));
        Assert.Equal(Money.Gbp(250), Money.Max(Money.Gbp(100), Money.Gbp(250)));
    }

    [Fact]
    public void Overflow_throws_instead_of_wrapping()
    {
        Assert.Throws<OverflowException>(() => new Money(long.MaxValue, "GBP") + Money.Gbp(1));
    }
}

public class RoundingTests
{
    [Theory]
    [InlineData(5, 2, 3)]     // 2.5 -> 3
    [InlineData(-5, 2, -3)]   // -2.5 -> -3 (half away from zero)
    [InlineData(7, 2, 4)]     // 3.5 -> 4
    [InlineData(1, 3, 0)]     // 0.33 -> 0
    [InlineData(2, 3, 1)]     // 0.67 -> 1
    [InlineData(10, 5, 2)]    // exact
    [InlineData(5, -2, -3)]   // negative denominator
    public void Rounds_half_away_from_zero(long numerator, long denominator, long expected)
    {
        Assert.Equal(expected, Rounding.DivideRounded(numerator, denominator));
    }

    [Theory]
    [InlineData(1000, 1500, 150)]  // 15% of £10.00
    [InlineData(999, 1000, 100)]   // 10% of £9.99 = 99.9p -> 100p
    [InlineData(333, 5000, 167)]   // 50% of £3.33 = 166.5p -> 167p
    public void Applies_basis_points(long amount, int basisPoints, long expected)
    {
        Assert.Equal(expected, Rounding.ApplyBasisPoints(amount, basisPoints));
    }

    [Fact]
    public void Divide_by_zero_throws()
    {
        Assert.Throws<DivideByZeroException>(() => Rounding.DivideRounded(1, 0));
    }
}
