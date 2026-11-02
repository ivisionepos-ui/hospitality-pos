namespace Pos.Domain.Pricing;

/// <summary>
/// A service charge on the order, as a percentage of the food and drink total after discounts.
/// Optional (discretionary) charges are outside the scope of VAT; compulsory ones carry VAT like the food.
/// Both count as tips under the Employment (Allocation of Tips) Act 2023 and must be passed to staff.
/// </summary>
/// <param name="BasisPoints">Rate in basis points: 1250 = 12.5%.</param>
/// <param name="IsOptional">True when the customer may decline it (the usual UK practice).</param>
public sealed record ServiceChargeInput(int BasisPoints, bool IsOptional = true)
{
    /// <summary>Checks the rate is sensible (above 0% and at most 100%).</summary>
    public void Validate()
    {
        if (BasisPoints is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(BasisPoints), "Service charge must be between 0.01% and 100%.");
        }
    }
}
