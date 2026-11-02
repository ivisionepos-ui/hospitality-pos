using Pos.Domain.Common;
using Pos.Domain.Tax;

namespace Pos.Domain.Tests.Tax;

public class UkVatPolicyTests
{
    private readonly UkVatPolicy _policy = new();

    [Theory]
    [InlineData(VatCategory.HotFood, OrderType.EatIn, 2000)]
    [InlineData(VatCategory.HotFood, OrderType.Takeaway, 2000)]
    [InlineData(VatCategory.HotFood, OrderType.Delivery, 2000)]
    [InlineData(VatCategory.ColdFood, OrderType.EatIn, 2000)]
    [InlineData(VatCategory.ColdFood, OrderType.Takeaway, 0)]
    [InlineData(VatCategory.ColdFood, OrderType.Delivery, 0)]
    [InlineData(VatCategory.AlwaysStandard, OrderType.EatIn, 2000)]
    [InlineData(VatCategory.AlwaysStandard, OrderType.Takeaway, 2000)]
    [InlineData(VatCategory.AlwaysStandard, OrderType.Delivery, 2000)]
    public void Picks_the_uk_rate_by_category_and_order_type(VatCategory category, OrderType type, int expectedBasisPoints)
    {
        Assert.Equal(expectedBasisPoints, _policy.RateFor(category, type).BasisPoints);
    }

    [Fact]
    public void Standard_rate_can_be_changed_without_code_changes()
    {
        var policy = new UkVatPolicy(new VatRate("S", 1750), new VatRate("Z", 0));
        Assert.Equal(17.5m, policy.RateFor(VatCategory.HotFood, OrderType.EatIn).Percent);
    }
}

public class VatCalculatorTests
{
    private static readonly VatRate Standard = new("S", 2000);
    private static readonly VatRate Zero = new("Z", 0);

    [Theory]
    [InlineData(1200, 200)]   // £12.00 incl. VAT contains £2.00 VAT
    [InlineData(1099, 183)]   // 183.17p -> 183p
    [InlineData(3, 1)]        // 0.5p -> 1p
    [InlineData(0, 0)]
    public void Extracts_vat_from_inclusive_prices(long gross, long expectedVat)
    {
        Assert.Equal(Money.Gbp(expectedVat), VatCalculator.VatInGross(Money.Gbp(gross), Standard));
    }

    [Fact]
    public void Zero_rate_has_no_vat()
    {
        Assert.Equal(Money.Gbp(0), VatCalculator.VatInGross(Money.Gbp(450), Zero));
    }

    [Fact]
    public void Summary_rounds_once_per_rate_and_adds_up_to_the_total()
    {
        // Three items at 1p each: rounding per item would give 3 x 0p VAT;
        // rounding the 3p total gives 0.5p -> 1p.
        var lines = new[]
        {
            (Standard, Money.Gbp(1)),
            (Standard, Money.Gbp(1)),
            (Standard, Money.Gbp(1)),
            (Zero, Money.Gbp(350)),
        };

        var summary = VatCalculator.Summarise(lines);

        Assert.Equal(2, summary.Count);
        var standardLine = summary[0];
        Assert.Equal(Standard, standardLine.Rate);
        Assert.Equal(Money.Gbp(3), standardLine.Gross);
        Assert.Equal(Money.Gbp(1), standardLine.Vat);
        Assert.Equal(Money.Gbp(2), standardLine.Net);

        var zeroLine = summary[1];
        Assert.Equal(Money.Gbp(350), zeroLine.Gross);
        Assert.Equal(Money.Gbp(0), zeroLine.Vat);

        var grossTotal = summary.Select(s => s.Gross).Aggregate((a, b) => a + b);
        var partsTotal = summary.Select(s => s.Net + s.Vat).Aggregate((a, b) => a + b);
        Assert.Equal(grossTotal, partsTotal);
    }

    [Fact]
    public void Refunds_produce_negative_vat()
    {
        Assert.Equal(Money.Gbp(-200), VatCalculator.VatInGross(Money.Gbp(-1200), Standard));
    }
}
