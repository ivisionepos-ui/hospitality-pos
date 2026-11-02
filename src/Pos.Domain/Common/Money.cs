using System.Globalization;
using System.Text.Json.Serialization;

namespace Pos.Domain.Common;

/// <summary>
/// An amount of money held as a whole number of minor units (pence for GBP).
/// Floating point is never used for money anywhere in the system.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>Creates an amount from minor units, e.g. 1250 = £12.50.</summary>
    [JsonConstructor]
    public Money(long minorUnits, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (currency.Length != 3)
        {
            throw new ArgumentException("Currency must be a 3-letter ISO 4217 code.", nameof(currency));
        }

        MinorUnits = minorUnits;
        Currency = currency.ToUpperInvariant();
    }

    /// <summary>The amount in minor units (pence).</summary>
    public long MinorUnits { get; }

    /// <summary>ISO 4217 currency code, e.g. GBP.</summary>
    public string Currency { get; }

    /// <summary>Shortcut for a GBP amount in pence.</summary>
    public static Money Gbp(long pence) => new(pence, "GBP");

    /// <summary>Zero in the given currency.</summary>
    public static Money Zero(string currency) => new(0, currency);

    /// <summary>True when the amount is below zero.</summary>
    [JsonIgnore]
    public bool IsNegative => MinorUnits < 0;

    /// <summary>Adds two amounts of the same currency.</summary>
    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.MinorUnits + right.MinorUnits), left.Currency);
    }

    /// <summary>Subtracts two amounts of the same currency.</summary>
    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.MinorUnits - right.MinorUnits), left.Currency);
    }

    /// <summary>Multiplies by a whole quantity.</summary>
    public static Money operator *(Money amount, int quantity) =>
        new(checked(amount.MinorUnits * quantity), amount.Currency);

    /// <summary>Adds two amounts (named alternative to +).</summary>
    public static Money Add(Money left, Money right) => left + right;

    /// <summary>Subtracts two amounts (named alternative to -).</summary>
    public static Money Subtract(Money left, Money right) => left - right;

    /// <summary>Multiplies by a quantity (named alternative to *).</summary>
    public static Money Multiply(Money amount, int quantity) => amount * quantity;

    /// <summary>The smaller of two amounts.</summary>
    public static Money Min(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return a.MinorUnits <= b.MinorUnits ? a : b;
    }

    /// <summary>The larger of two amounts.</summary>
    public static Money Max(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return a.MinorUnits >= b.MinorUnits ? a : b;
    }

    /// <inheritdoc />
    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return MinorUnits.CompareTo(other.MinorUnits);
    }

    /// <summary>Compares two amounts of the same currency.</summary>
    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    /// <summary>Compares two amounts of the same currency.</summary>
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    /// <summary>Compares two amounts of the same currency.</summary>
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two amounts of the same currency.</summary>
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <summary>Plain format for logs and tests, e.g. "GBP 12.50". Receipts use their own formatting.</summary>
    public override string ToString()
    {
        var sign = MinorUnits < 0 ? "-" : string.Empty;
        var abs = Math.Abs(MinorUnits);
        return string.Create(CultureInfo.InvariantCulture, $"{Currency} {sign}{abs / 100}.{abs % 100:00}");
    }

    private static void EnsureSameCurrency(Money a, Money b)
    {
        if (!string.Equals(a.Currency, b.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot combine {a.Currency} with {b.Currency}.");
        }
    }
}
