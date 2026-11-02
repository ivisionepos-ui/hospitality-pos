using Pos.Domain.Common;

namespace Pos.Domain.MealDeals;

/// <summary>
/// Shares a meal deal's price across the items inside it, in proportion to their normal prices.
/// Needed for VAT (items in one deal can have different VAT rates) and for per-item sales reports.
/// The shares always add up to the deal price exactly, to the penny.
/// </summary>
public static class MealDealAllocator
{
    /// <summary>
    /// Splits <paramref name="dealPrice"/> across items whose normal prices are given.
    /// Uses the largest-remainder method: every item gets its share rounded down,
    /// then the pennies left over go to the items with the largest fractions (earlier items first on a tie).
    /// If all normal prices are zero, the price is split equally.
    /// </summary>
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

        var weights = normalPrices.Select(p => p.MinorUnits).ToArray();
        var totalWeight = weights.Sum();
        if (totalWeight == 0)
        {
            weights = [.. Enumerable.Repeat(1L, weights.Length)];
            totalWeight = weights.Length;
        }

        var total = dealPrice.MinorUnits;
        var shares = new long[weights.Length];
        var remainders = new long[weights.Length];
        long allocated = 0;
        for (var i = 0; i < weights.Length; i++)
        {
            var product = checked(total * weights[i]);
            shares[i] = product / totalWeight;
            remainders[i] = product % totalWeight;
            allocated += shares[i];
        }

        var leftover = total - allocated;
        foreach (var index in Enumerable.Range(0, weights.Length)
                     .OrderByDescending(i => remainders[i])
                     .ThenBy(i => i)
                     .Take((int)leftover))
        {
            shares[index]++;
        }

        return [.. shares.Select(s => new Money(s, dealPrice.Currency))];
    }
}
