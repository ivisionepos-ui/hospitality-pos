using Pos.Domain.Common;

namespace Pos.Domain.Promotions;

/// <summary>The item being priced: what the offer rules look at.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="VariantId">The size chosen, if the product has sizes.</param>
/// <param name="CategoryId">The product's category.</param>
/// <param name="NormalUnitPrice">The normal price of one unit (size included, extras excluded).</param>
public sealed record PricingItem(Guid ProductId, Guid? VariantId, Guid CategoryId, Money NormalUnitPrice);

/// <summary>Where and when an order is placed.</summary>
/// <param name="PlacedAt">The moment the order was placed (not when it will be collected).</param>
/// <param name="BranchTimeZone">The branch's time zone, e.g. Europe/London.</param>
/// <param name="BranchId">The branch taking the order.</param>
/// <param name="Channel">Till, website or phone.</param>
public sealed record PricingContext(DateTimeOffset PlacedAt, TimeZoneInfo BranchTimeZone, Guid BranchId, SalesChannel Channel)
{
    /// <summary>Local date and time at the branch.</summary>
    public DateTime LocalTime => TimeZoneInfo.ConvertTime(PlacedAt, BranchTimeZone).DateTime;
}

/// <summary>The price of one unit after offers.</summary>
/// <param name="NormalUnitPrice">Price before any offer.</param>
/// <param name="UnitPrice">Price charged.</param>
/// <param name="Promotion">The offer applied, or null.</param>
public sealed record OfferResult(Money NormalUnitPrice, Money UnitPrice, Promotion? Promotion)
{
    /// <summary>Saving per unit.</summary>
    public Money UnitSaving => NormalUnitPrice - UnitPrice;
}

/// <summary>
/// Finds the offer that applies to an item. Rules:
/// the offer must be active, inside its dates and weekly schedule at the time the order is placed
/// (branch local time), for this branch and channel, and cover the item, size or category.
/// If several match, the highest priority wins; on a tie, the lower price wins (best for the customer).
/// Offers never add together.
/// </summary>
public static class PromotionEngine
{
    /// <summary>Returns the price after the best matching offer.</summary>
    public static OfferResult Apply(PricingItem item, PricingContext context, IEnumerable<Promotion> promotions)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(promotions);

        var localTime = context.LocalTime;
        var best = promotions
            .Where(p => IsRunning(p, context, localTime) && Covers(p, item))
            .Select(p => (Promotion: p, Price: p.ApplyTo(item.NormalUnitPrice)))
            .OrderByDescending(x => x.Promotion.Priority)
            .ThenBy(x => x.Price.MinorUnits)
            .ThenBy(x => x.Promotion.Id)
            .FirstOrDefault();

        return best.Promotion is null
            ? new OfferResult(item.NormalUnitPrice, item.NormalUnitPrice, null)
            : new OfferResult(item.NormalUnitPrice, best.Price, best.Promotion);
    }

    /// <summary>True when the offer is switched on and in date, schedule, branch and channel.</summary>
    public static bool IsRunning(Promotion promotion, PricingContext context, DateTime localTime)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        ArgumentNullException.ThrowIfNull(context);

        if (!promotion.IsActive)
        {
            return false;
        }

        var date = DateOnly.FromDateTime(localTime);
        if (promotion.StartsOn is { } start && date < start)
        {
            return false;
        }

        if (promotion.EndsOn is { } end && date > end)
        {
            return false;
        }

        if (promotion.BranchIds is { } branches && !branches.Contains(context.BranchId))
        {
            return false;
        }

        if (promotion.Channels is { } channels && !channels.Contains(context.Channel))
        {
            return false;
        }

        return promotion.Schedule.Count == 0 || promotion.Schedule.Any(w => w.Contains(localTime));
    }

    private static bool Covers(Promotion promotion, PricingItem item) =>
        promotion.Targets.Any(t => t.Kind switch
        {
            PromotionTargetKind.Product => t.Id == item.ProductId,
            PromotionTargetKind.Variant => item.VariantId == t.Id,
            PromotionTargetKind.Category => t.Id == item.CategoryId,
            _ => false,
        });
}
