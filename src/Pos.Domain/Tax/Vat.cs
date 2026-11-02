using Pos.Domain.Common;

namespace Pos.Domain.Tax;

/// <summary>How the order will be consumed. This decides the VAT rate of food in the UK.</summary>
public enum OrderType
{
    /// <summary>Eaten on the premises (tables, counter seating).</summary>
    EatIn,

    /// <summary>Collected and eaten elsewhere (counter takeaway, collection orders).</summary>
    Takeaway,

    /// <summary>Delivered to the customer.</summary>
    Delivery,
}

/// <summary>The VAT category set on each product in the back office.</summary>
public enum VatCategory
{
    /// <summary>Food heated to be eaten hot (pizza, kebab, chips). Standard rate eat-in and takeaway.</summary>
    HotFood,

    /// <summary>Cold food (cold sandwiches, salads). Standard rate eat-in, zero rate takeaway/delivery.</summary>
    ColdFood,

    /// <summary>
    /// Always standard rate: hot drinks, soft drinks, bottled water, alcohol,
    /// confectionery, crisps, ice cream and other "excepted items".
    /// </summary>
    AlwaysStandard,
}

/// <summary>A VAT rate as printed on receipts and reports.</summary>
/// <param name="Code">Short code shown next to items on receipts, e.g. "S" or "Z".</param>
/// <param name="BasisPoints">Rate in basis points: 2000 = 20%.</param>
public sealed record VatRate(string Code, int BasisPoints)
{
    /// <summary>Rate as a percentage for display, e.g. 20.</summary>
    public decimal Percent => BasisPoints / 100m;
}

/// <summary>Chooses the VAT rate for a product category and order type.</summary>
public interface IVatPolicy
{
    /// <summary>Returns the rate that applies.</summary>
    VatRate RateFor(VatCategory category, OrderType orderType);
}

/// <summary>
/// UK catering VAT rules (VAT Notice 709/1): everything eaten in is standard rated;
/// hot takeaway food is standard rated; cold takeaway food is zero rated unless it is an excepted item.
/// Delivered cold food is treated like takeaway. Confirm your menu with your accountant.
/// </summary>
public sealed class UkVatPolicy : IVatPolicy
{
    /// <summary>Creates the policy with the current UK rates (20% standard, 0% zero).</summary>
    public UkVatPolicy()
        : this(new VatRate("S", 2000), new VatRate("Z", 0))
    {
    }

    /// <summary>Creates the policy with custom rates, e.g. if the standard rate changes.</summary>
    public UkVatPolicy(VatRate standard, VatRate zero)
    {
        Standard = standard ?? throw new ArgumentNullException(nameof(standard));
        Zero = zero ?? throw new ArgumentNullException(nameof(zero));
    }

    /// <summary>The standard rate.</summary>
    public VatRate Standard { get; }

    /// <summary>The zero rate.</summary>
    public VatRate Zero { get; }

    /// <inheritdoc />
    public VatRate RateFor(VatCategory category, OrderType orderType) => category switch
    {
        VatCategory.HotFood => Standard,
        VatCategory.AlwaysStandard => Standard,
        VatCategory.ColdFood => orderType == OrderType.EatIn ? Standard : Zero,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown VAT category."),
    };
}

/// <summary>One line of the VAT summary on a receipt.</summary>
/// <param name="Rate">The VAT rate.</param>
/// <param name="Gross">Total charged at this rate, VAT included.</param>
/// <param name="Net">Gross minus VAT.</param>
/// <param name="Vat">VAT contained in the gross.</param>
public sealed record VatSummaryLine(VatRate Rate, Money Gross, Money Net, Money Vat);

/// <summary>Works out the VAT contained in VAT-inclusive prices (UK menu prices include VAT).</summary>
public static class VatCalculator
{
    /// <summary>VAT inside a VAT-inclusive amount: gross × rate / (100% + rate), rounded to the nearest penny.</summary>
    public static Money VatInGross(Money gross, VatRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        var vat = Rounding.DivideRounded(
            checked(gross.MinorUnits * rate.BasisPoints),
            Rounding.BasisPointsPerWhole + rate.BasisPoints);
        return new Money(vat, gross.Currency);
    }

    /// <summary>
    /// Builds the VAT summary by adding up the gross per rate first and rounding once per rate,
    /// so the summary always adds up exactly to the order total.
    /// </summary>
    public static IReadOnlyList<VatSummaryLine> Summarise(IEnumerable<(VatRate Rate, Money Gross)> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        return amounts
            .GroupBy(a => a.Rate)
            .Select(g =>
            {
                var gross = g.Select(x => x.Gross).Aggregate((a, b) => a + b);
                var vat = VatInGross(gross, g.Key);
                return new VatSummaryLine(g.Key, gross, gross - vat, vat);
            })
            .OrderByDescending(l => l.Rate.BasisPoints)
            .ToList();
    }
}
