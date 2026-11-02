using Pos.Domain.Common;

namespace Pos.Domain.Promotions;

/// <summary>How an offer changes the price.</summary>
public enum PromotionType
{
    /// <summary>The item sells at a fixed offer price (Value = price in pence).</summary>
    OfferPrice,

    /// <summary>A percentage off the normal price (Value = basis points, 1500 = 15%).</summary>
    PercentOff,

    /// <summary>A fixed amount off the normal price (Value = pence).</summary>
    AmountOff,
}

/// <summary>Where an order was taken. Delivery apps run their own offers and are not listed here.</summary>
public enum SalesChannel
{
    /// <summary>Taken on a till (counter or table).</summary>
    Pos,

    /// <summary>Your own ordering website.</summary>
    Website,

    /// <summary>Telephone order.</summary>
    Phone,
}

/// <summary>What an offer target refers to.</summary>
public enum PromotionTargetKind
{
    /// <summary>A whole product (all sizes).</summary>
    Product,

    /// <summary>One size of a product.</summary>
    Variant,

    /// <summary>Every product in a category.</summary>
    Category,
}

/// <summary>An item, size or category covered by an offer.</summary>
public sealed record PromotionTarget(PromotionTargetKind Kind, Guid Id);

/// <summary>
/// A scheduled offer, e.g. "Burgers £2 off every Tuesday and Wednesday".
/// The normal price never changes; the offer is applied as a rule when an order is placed.
/// </summary>
public sealed class Promotion
{
    /// <summary>Creates and validates an offer.</summary>
    public Promotion(
        Guid id,
        string name,
        PromotionType type,
        long value,
        IReadOnlyList<PromotionTarget> targets,
        IReadOnlyList<WeeklyTimeWindow>? schedule = null,
        DateOnly? startsOn = null,
        DateOnly? endsOn = null,
        int priority = 0,
        IReadOnlySet<Guid>? branchIds = null,
        IReadOnlySet<SalesChannel>? channels = null,
        bool isActive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("An offer needs at least one item, size or category.", nameof(targets));
        }

        switch (type)
        {
            case PromotionType.PercentOff when value is <= 0 or > Rounding.BasisPointsPerWhole:
                throw new ArgumentOutOfRangeException(nameof(value), "Percent off must be between 0.01% and 100%.");
            case PromotionType.AmountOff when value <= 0:
                throw new ArgumentOutOfRangeException(nameof(value), "Amount off must be more than zero.");
            case PromotionType.OfferPrice when value < 0:
                throw new ArgumentOutOfRangeException(nameof(value), "Offer price cannot be negative.");
        }

        if (startsOn is { } s && endsOn is { } e && e < s)
        {
            throw new ArgumentException("The end date is before the start date.", nameof(endsOn));
        }

        Id = id;
        Name = name;
        Type = type;
        Value = value;
        Targets = targets;
        Schedule = schedule ?? [];
        StartsOn = startsOn;
        EndsOn = endsOn;
        Priority = priority;
        BranchIds = branchIds;
        Channels = channels;
        IsActive = isActive;
    }

    /// <summary>Unique id.</summary>
    public Guid Id { get; }

    /// <summary>Name shown in the back office and on receipts.</summary>
    public string Name { get; }

    /// <summary>How the price is changed.</summary>
    public PromotionType Type { get; }

    /// <summary>Pence for OfferPrice and AmountOff; basis points for PercentOff.</summary>
    public long Value { get; }

    /// <summary>Items, sizes or categories covered.</summary>
    public IReadOnlyList<PromotionTarget> Targets { get; }

    /// <summary>Weekly windows when the offer runs. Empty = every day, all day.</summary>
    public IReadOnlyList<WeeklyTimeWindow> Schedule { get; }

    /// <summary>First date the offer runs (branch local date), or null.</summary>
    public DateOnly? StartsOn { get; }

    /// <summary>Last date the offer runs (inclusive), or null.</summary>
    public DateOnly? EndsOn { get; }

    /// <summary>When two offers match one item, the higher priority wins.</summary>
    public int Priority { get; }

    /// <summary>Branches where it runs; null = all branches.</summary>
    public IReadOnlySet<Guid>? BranchIds { get; }

    /// <summary>Channels where it runs; null = all channels.</summary>
    public IReadOnlySet<SalesChannel>? Channels { get; }

    /// <summary>False when a manager has switched the offer off.</summary>
    public bool IsActive { get; }

    /// <summary>The unit price after this offer. Never below zero and never above the normal price.</summary>
    public Money ApplyTo(Money normalUnitPrice)
    {
        var normal = normalUnitPrice.MinorUnits;
        var result = Type switch
        {
            PromotionType.OfferPrice => Math.Min(normal, Value),
            PromotionType.PercentOff => normal - Rounding.ApplyBasisPoints(normal, (int)Value),
            PromotionType.AmountOff => normal - Value,
            _ => normal,
        };

        return new Money(Math.Clamp(result, 0, Math.Max(normal, 0)), normalUnitPrice.Currency);
    }
}
