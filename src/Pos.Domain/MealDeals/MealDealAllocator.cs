using Pos.Domain.Common;

namespace Pos.Domain.MealDeals;

/// <summary>
/// Shares a meal deal's price across the items inside it, in proportion to their normal prices.
/// Needed for VAT (items in one deal can have different VAT rates) and for per-item sales reports.
/// The shares always add up to the deal price exactly, to the penny.
/// </summary>
public static class MealDealAllocator
{
    /// <summary>Splits <paramref name="dealPrice"/> across items whose normal prices are given.</summary>
    public static IReadOnlyList<Money> Allocate(Money dealPrice, IReadOnlyList<Money> normalPrices)
    {
        ArgumentNullException.ThrowIfNull(normalPrices);
        if (normalPrices.Count == 0)
        {
            throw new ArgumentException("A meal deal needs at least one item.", nameof(normalPrices));
        }

        if (dealPrice.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(dealPrice), "Deal price cannot be negative.");
        }

        if (normalPrices.Any(p => p.IsNegative))
        {
            throw new ArgumentOutOfRangeException(nameof(normalPrices), "Item prices cannot be negative.");
        }

        return ProportionalAllocator.Allocate(dealPrice, normalPrices);
    }
}
