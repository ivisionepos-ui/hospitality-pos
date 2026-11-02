namespace Pos.Domain.Common;

/// <summary>
/// Splits an amount across several parts in proportion to their weights, to the penny.
/// Used for meal deal prices, order discounts, delivery fees and service charges,
/// so each item and each VAT rate gets its fair share and the shares always add up exactly.
/// </summary>
public static class ProportionalAllocator
{
    /// <summary>
    /// Largest-remainder method: every part gets its share rounded down, then the pennies left over
    /// go to the parts with the largest fractions (earlier parts first on a tie).
    /// If all weights are zero, the amount is split equally.
    /// </summary>
    public static IReadOnlyList<Money> Allocate(Money total, IReadOnlyList<Money> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0)
        {
            throw new ArgumentException("At least one part is required.", nameof(weights));
        }

        if (total.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "Amount to share cannot be negative.");
        }

        if (weights.Any(w => w.IsNegative))
        {
            throw new ArgumentOutOfRangeException(nameof(weights), "Weights cannot be negative.");
        }

        var w = weights.Select(x => x.MinorUnits).ToArray();
        var totalWeight = w.Sum();
        if (totalWeight == 0)
        {
            w = [.. Enumerable.Repeat(1L, w.Length)];
            totalWeight = w.Length;
        }

        var amount = total.MinorUnits;
        var shares = new long[w.Length];
        var remainders = new long[w.Length];
        long allocated = 0;
        for (var i = 0; i < w.Length; i++)
        {
            var product = checked(amount * w[i]);
            shares[i] = product / totalWeight;
            remainders[i] = product % totalWeight;
            allocated += shares[i];
        }

        var leftover = amount - allocated;
        foreach (var index in Enumerable.Range(0, w.Length)
                     .OrderByDescending(i => remainders[i])
                     .ThenBy(i => i)
                     .Take((int)leftover))
        {
            shares[index]++;
        }

        return [.. shares.Select(s => new Money(s, total.Currency))];
    }
}
