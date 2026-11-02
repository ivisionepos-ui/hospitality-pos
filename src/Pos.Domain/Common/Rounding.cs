namespace Pos.Domain.Common;

/// <summary>
/// Whole-number rounding used for all money calculations.
/// Rule: round half away from zero (0.5p rounds up to 1p), the usual commercial rule in the UK.
/// </summary>
public static class Rounding
{
    /// <summary>Basis points in 100%. 2000 bp = 20%.</summary>
    public const long BasisPointsPerWhole = 10_000;

    /// <summary>Returns numerator / denominator rounded half away from zero.</summary>
    public static long DivideRounded(long numerator, long denominator)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException();
        }

        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var quotient = Math.DivRem(numerator, denominator, out var remainder);
        if (Math.Abs(remainder) * 2 >= denominator)
        {
            quotient += numerator < 0 ? -1 : 1;
        }

        return quotient;
    }

    /// <summary>Returns amount × basisPoints / 10000, rounded half away from zero.</summary>
    public static long ApplyBasisPoints(long amountMinor, int basisPoints) =>
        DivideRounded(checked(amountMinor * basisPoints), BasisPointsPerWhole);
}
