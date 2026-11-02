using Pos.Domain.Common;

namespace Pos.Domain.Delivery;

/// <summary>One distance band, e.g. 1 to 2 miles costs £2.50.</summary>
/// <param name="FromMiles">Start of the band (included).</param>
/// <param name="ToMiles">End of the band (not included).</param>
/// <param name="Fee">Delivery fee for this band.</param>
/// <param name="MinimumOrder">Smallest food and drink total accepted in this band, or null for the table default.</param>
/// <param name="FreeOver">Delivery is free when the food and drink total reaches this, or null for the table default.</param>
public sealed record DeliveryBand(decimal FromMiles, decimal ToMiles, Money Fee, Money? MinimumOrder = null, Money? FreeOver = null);

/// <summary>Result of checking a delivery address.</summary>
public enum DeliveryQuoteStatus
{
    /// <summary>Deliverable; the fee applies.</summary>
    Ok,

    /// <summary>Within range, but the order is below the minimum for this distance.</summary>
    BelowMinimumOrder,

    /// <summary>Further than the shop delivers.</summary>
    BeyondMaximumDistance,
}

/// <summary>What the delivery would cost, and whether it is allowed.</summary>
/// <param name="Status">Ok, below minimum order, or too far.</param>
/// <param name="Fee">Fee to charge (zero when too far).</param>
/// <param name="DistanceMiles">Distance used.</param>
/// <param name="MinimumOrder">Minimum order that applied, if any.</param>
/// <param name="IsFreeDelivery">True when the free-delivery threshold was reached.</param>
public sealed record DeliveryQuote(DeliveryQuoteStatus Status, Money Fee, decimal DistanceMiles, Money? MinimumOrder, bool IsFreeDelivery);

/// <summary>
/// A branch's delivery charges: either distance bands or a rate per mile,
/// with an optional minimum order, free delivery above an amount, and a maximum distance.
/// </summary>
public sealed class DeliveryChargeTable
{
    private readonly IReadOnlyList<DeliveryBand> _bands;

    private DeliveryChargeTable(
        IReadOnlyList<DeliveryBand> bands,
        Money? ratePerMile,
        Money? minimumFee,
        Money? maximumFee,
        decimal maximumMiles,
        Money? minimumOrder,
        Money? freeOver)
    {
        if (maximumMiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMiles), "Maximum delivery distance must be more than zero.");
        }

        _bands = bands;
        RatePerMile = ratePerMile;
        MinimumFee = minimumFee;
        MaximumFee = maximumFee;
        MaximumMiles = maximumMiles;
        MinimumOrder = minimumOrder;
        FreeOver = freeOver;
    }

    /// <summary>Distance bands (empty for a per-mile table).</summary>
    public IReadOnlyList<DeliveryBand> Bands => _bands;

    /// <summary>Price per mile for a per-mile table.</summary>
    public Money? RatePerMile { get; }

    /// <summary>Lowest fee for a per-mile table.</summary>
    public Money? MinimumFee { get; }

    /// <summary>Highest fee for a per-mile table.</summary>
    public Money? MaximumFee { get; }

    /// <summary>Furthest distance delivered to.</summary>
    public decimal MaximumMiles { get; }

    /// <summary>Default minimum order.</summary>
    public Money? MinimumOrder { get; }

    /// <summary>Default free-delivery threshold.</summary>
    public Money? FreeOver { get; }

    /// <summary>Creates a table from distance bands. Bands must not overlap.</summary>
    public static DeliveryChargeTable Banded(IReadOnlyList<DeliveryBand> bands, decimal maximumMiles, Money? minimumOrder = null, Money? freeOver = null)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Count == 0)
        {
            throw new ArgumentException("At least one band is required.", nameof(bands));
        }

        var ordered = bands.OrderBy(b => b.FromMiles).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var band = ordered[i];
            if (band.FromMiles < 0 || band.ToMiles <= band.FromMiles)
            {
                throw new ArgumentException($"Band {band.FromMiles}–{band.ToMiles} miles is not valid.", nameof(bands));
            }

            if (band.Fee.IsNegative)
            {
                throw new ArgumentException("Delivery fees cannot be negative.", nameof(bands));
            }

            if (i > 0 && band.FromMiles < ordered[i - 1].ToMiles)
            {
                throw new ArgumentException("Delivery bands overlap.", nameof(bands));
            }
        }

        return new DeliveryChargeTable(ordered, null, null, null, maximumMiles, minimumOrder, freeOver);
    }

    /// <summary>Creates a per-mile table: fee = miles × rate, rounded to the penny, kept between the minimum and maximum fee.</summary>
    public static DeliveryChargeTable PerMile(Money ratePerMile, Money minimumFee, decimal maximumMiles, Money? maximumFee = null, Money? minimumOrder = null, Money? freeOver = null)
    {
        if (ratePerMile.IsNegative || minimumFee.IsNegative || maximumFee is { IsNegative: true })
        {
            throw new ArgumentException("Delivery fees cannot be negative.");
        }

        if (maximumFee is { } max && max < minimumFee)
        {
            throw new ArgumentException("The maximum fee is lower than the minimum fee.", nameof(maximumFee));
        }

        return new DeliveryChargeTable([], ratePerMile, minimumFee, maximumFee, maximumMiles, minimumOrder, freeOver);
    }

    /// <summary>The table as plain data, for saving.</summary>
    public DeliveryChargeTableState ToState() =>
        new(_bands, RatePerMile, MinimumFee, MaximumFee, MaximumMiles, MinimumOrder, FreeOver);

    /// <summary>Rebuilds a table saved with <see cref="ToState"/> (checked again on the way in).</summary>
    public static DeliveryChargeTable FromState(DeliveryChargeTableState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.RatePerMile is { } rate
            ? PerMile(rate, state.MinimumFee ?? Money.Zero(rate.Currency), state.MaximumMiles, state.MaximumFee, state.MinimumOrder, state.FreeOver)
            : Banded(state.Bands, state.MaximumMiles, state.MinimumOrder, state.FreeOver);
    }

    /// <summary>Quotes a delivery for a distance and the food and drink total (after discounts).</summary>
    public DeliveryQuote Quote(decimal distanceMiles, Money goodsTotal)
    {
        if (distanceMiles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceMiles), "Distance cannot be negative.");
        }

        var zero = Money.Zero(goodsTotal.Currency);
        if (distanceMiles > MaximumMiles)
        {
            return new DeliveryQuote(DeliveryQuoteStatus.BeyondMaximumDistance, zero, distanceMiles, null, false);
        }

        Money fee;
        Money? minimumOrder = MinimumOrder;
        Money? freeOver = FreeOver;
        if (RatePerMile is { } rate)
        {
            var raw = (long)Math.Round(distanceMiles * rate.MinorUnits, MidpointRounding.AwayFromZero);
            var clamped = Math.Max(raw, MinimumFee!.Value.MinorUnits);
            if (MaximumFee is { } maxFee)
            {
                clamped = Math.Min(clamped, maxFee.MinorUnits);
            }

            fee = new Money(clamped, goodsTotal.Currency);
        }
        else
        {
            // Bands include their start and exclude their end, except the last band, which includes its end
            // (so "3 to 5 miles" covers exactly 5 miles).
            var last = _bands[^1];
            var band = _bands.FirstOrDefault(b => distanceMiles >= b.FromMiles
                && (distanceMiles < b.ToMiles || (ReferenceEquals(b, last) && distanceMiles == b.ToMiles)));
            if (band is null)
            {
                return new DeliveryQuote(DeliveryQuoteStatus.BeyondMaximumDistance, zero, distanceMiles, null, false);
            }

            fee = band.Fee;
            minimumOrder = band.MinimumOrder ?? minimumOrder;
            freeOver = band.FreeOver ?? freeOver;
        }

        if (minimumOrder is { } min && goodsTotal < min)
        {
            return new DeliveryQuote(DeliveryQuoteStatus.BelowMinimumOrder, fee, distanceMiles, minimumOrder, false);
        }

        var isFree = freeOver is { } threshold && goodsTotal >= threshold;
        return new DeliveryQuote(DeliveryQuoteStatus.Ok, isFree ? zero : fee, distanceMiles, minimumOrder, isFree);
    }
}

/// <summary>A delivery fee typed in by staff instead of the table's fee. Reason and user are recorded.</summary>
/// <param name="Fee">Fee to charge.</param>
/// <param name="Reason">Why it was changed.</param>
/// <param name="ByUserId">Staff member with permission to change it.</param>
public sealed record DeliveryFeeOverride(Money Fee, string Reason, Guid ByUserId);

/// <summary>Delivery details for pricing an order.</summary>
/// <param name="DistanceMiles">Distance from the shop to the address.</param>
/// <param name="Table">The branch's delivery charges.</param>
/// <param name="Override">Staff override, or null.</param>
public sealed record DeliveryInput(decimal DistanceMiles, DeliveryChargeTable Table, DeliveryFeeOverride? Override = null);

/// <summary>A delivery charge table as plain data.</summary>
/// <param name="Bands">Distance bands (empty for per-mile).</param>
/// <param name="RatePerMile">Per-mile rate, or null for bands.</param>
/// <param name="MinimumFee">Lowest per-mile fee.</param>
/// <param name="MaximumFee">Highest per-mile fee.</param>
/// <param name="MaximumMiles">Furthest distance.</param>
/// <param name="MinimumOrder">Default minimum order.</param>
/// <param name="FreeOver">Default free-delivery threshold.</param>
public sealed record DeliveryChargeTableState(
    IReadOnlyList<DeliveryBand> Bands,
    Money? RatePerMile,
    Money? MinimumFee,
    Money? MaximumFee,
    decimal MaximumMiles,
    Money? MinimumOrder,
    Money? FreeOver);
