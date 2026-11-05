using Pos.Domain.Common;

namespace Pos.Domain.Pricing;

/// <summary>How an order discount is expressed.</summary>
public enum OrderDiscountType
{
    /// <summary>Percentage of the food and drink total (Value = basis points, 1000 = 10%).</summary>
    Percent,

    /// <summary>Fixed amount off (Value = pence).</summary>
    Amount,
}

/// <summary>A discount on the whole order, e.g. "10% staff discount" or "£3 off, cold food complaint".</summary>
/// <param name="Type">Percent or amount.</param>
/// <param name="Value">Basis points for Percent; pence for Amount.</param>
/// <param name="Reason">Why it was given. Always required; it appears in the audit log and reports.</param>
/// <param name="GivenByUserId">Staff member who applied it.</param>
/// <param name="ApprovedByUserId">Manager who approved it, when the amount is above the staff limit.</param>
public sealed record OrderDiscountInput(OrderDiscountType Type, long Value, string Reason, Guid GivenByUserId, Guid? ApprovedByUserId = null);

/// <summary>
/// The limits staff may give without a manager. Above either limit the discount needs a manager's approval.
/// Set per business in the back office.
/// </summary>
/// <param name="StaffMaxPercentBasisPoints">Largest percentage staff may give alone (basis points).</param>
/// <param name="StaffMaxAmount">Largest amount staff may give alone.</param>
public sealed record DiscountPolicy(int StaffMaxPercentBasisPoints, Money StaffMaxAmount)
{
    /// <summary>Every order discount needs a manager. The safe default.</summary>
    public static DiscountPolicy ManagerAlways(string currency = "GBP") => new(0, Money.Zero(currency));

    /// <summary>
    /// No limit. Used when an order is re-priced after the discount was already allowed (items added, order reloaded),
    /// so a discount given within the rules is never refused later because the order grew or the limits changed.
    /// </summary>
    public static DiscountPolicy NoLimit(string currency = "GBP") => new(int.MaxValue, new Money(long.MaxValue, currency));
}

/// <summary>Thrown when a discount is above the staff limit and no manager has approved it.</summary>
public sealed class DiscountApprovalRequiredException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public DiscountApprovalRequiredException()
        : base("This discount needs a manager's approval.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DiscountApprovalRequiredException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public DiscountApprovalRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Works out an order discount and checks the approval rule.</summary>
public static class OrderDiscountCalculator
{
    /// <summary>
    /// Returns the amount off. Percent discounts are rounded to the nearest penny;
    /// amount discounts are capped at the food and drink total, so an order never goes negative.
    /// </summary>
    public static Money AmountOff(OrderDiscountInput discount, Money goodsTotal)
    {
        ArgumentNullException.ThrowIfNull(discount);
        Validate(discount);

        var off = discount.Type switch
        {
            OrderDiscountType.Percent => Rounding.ApplyBasisPoints(goodsTotal.MinorUnits, (int)discount.Value),
            OrderDiscountType.Amount => discount.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(discount), "Unknown discount type."),
        };

        return new Money(Math.Clamp(off, 0, Math.Max(goodsTotal.MinorUnits, 0)), goodsTotal.Currency);
    }

    /// <summary>True when the discount is above what staff may give alone.</summary>
    public static bool NeedsApproval(OrderDiscountInput discount, Money amountOff, DiscountPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(discount);
        ArgumentNullException.ThrowIfNull(policy);

        var percentTooHigh = discount.Type == OrderDiscountType.Percent && discount.Value > policy.StaffMaxPercentBasisPoints;
        return percentTooHigh || amountOff > policy.StaffMaxAmount;
    }

    /// <summary>Throws if the discount needs approval and none was given.</summary>
    public static void EnsureApproved(OrderDiscountInput discount, Money amountOff, DiscountPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(discount);
        if (NeedsApproval(discount, amountOff, policy) && discount.ApprovedByUserId is null)
        {
            throw new DiscountApprovalRequiredException();
        }
    }

    private static void Validate(OrderDiscountInput discount)
    {
        if (string.IsNullOrWhiteSpace(discount.Reason))
        {
            throw new ArgumentException("A reason is required for every discount.", nameof(discount));
        }

        switch (discount.Type)
        {
            case OrderDiscountType.Percent when discount.Value is <= 0 or > Rounding.BasisPointsPerWhole:
                throw new ArgumentOutOfRangeException(nameof(discount), "Percent discount must be between 0.01% and 100%.");
            case OrderDiscountType.Amount when discount.Value <= 0:
                throw new ArgumentOutOfRangeException(nameof(discount), "Amount discount must be more than zero.");
        }
    }
}
