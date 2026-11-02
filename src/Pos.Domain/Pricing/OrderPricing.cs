using Pos.Domain.Common;
using Pos.Domain.MealDeals;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;

namespace Pos.Domain.Pricing;

/// <summary>An extra or choice added to an item (e.g. extra cheese). Charged at its normal price, per unit.</summary>
/// <param name="OptionId">The modifier option.</param>
/// <param name="UnitPrice">Price per unit of the parent item.</param>
public sealed record ModifierInput(Guid OptionId, Money UnitPrice);

/// <summary>A line to be priced: either a normal item or a meal deal.</summary>
public abstract record OrderLineInput
{
    /// <summary>Quantity, at least 1.</summary>
    public required int Quantity { get; init; }
}

/// <summary>A normal item, e.g. 2 x Chicken Burger with extra cheese.</summary>
public sealed record ItemLineInput : OrderLineInput
{
    /// <summary>The product, size, category and normal price.</summary>
    public required PricingItem Item { get; init; }

    /// <summary>The product's VAT category.</summary>
    public required VatCategory VatCategory { get; init; }

    /// <summary>Extras and choices (VAT follows the item).</summary>
    public IReadOnlyList<ModifierInput> Modifiers { get; init; } = [];
}

/// <summary>One choice inside a meal deal, e.g. the pizza.</summary>
public sealed record DealComponentInput
{
    /// <summary>The chosen product and its normal price (used to share the deal price).</summary>
    public required PricingItem Item { get; init; }

    /// <summary>The chosen product's VAT category.</summary>
    public required VatCategory VatCategory { get; init; }

    /// <summary>Extra charge for this choice, e.g. +£1.50 premium pizza.</summary>
    public Money? Upcharge { get; init; }

    /// <summary>Extras on this item, charged at normal prices.</summary>
    public IReadOnlyList<ModifierInput> Modifiers { get; init; } = [];
}

/// <summary>A meal deal with the customer's choices.</summary>
public sealed record MealDealLineInput : OrderLineInput
{
    /// <summary>The deal product and its normal deal price (offers can apply to the deal itself).</summary>
    public required PricingItem Deal { get; init; }

    /// <summary>The chosen items, one per choice.</summary>
    public required IReadOnlyList<DealComponentInput> Components { get; init; }
}

/// <summary>A priced item inside a meal deal (per deal unit).</summary>
/// <param name="Input">What was chosen.</param>
/// <param name="AllocatedShare">This item's share of the deal price.</param>
/// <param name="UnitTotal">Share + upcharge + extras, for one deal.</param>
/// <param name="VatRate">VAT rate for this item.</param>
public sealed record PricedDealComponent(DealComponentInput Input, Money AllocatedShare, Money UnitTotal, VatRate VatRate);

/// <summary>A priced order line.</summary>
/// <param name="Input">The line as entered.</param>
/// <param name="Offer">Offer result for the item (or deal) price.</param>
/// <param name="UnitTotal">Charge for one unit, extras and upcharges included.</param>
/// <param name="LineTotal">UnitTotal × quantity.</param>
/// <param name="VatRate">VAT rate for a normal item; null for a meal deal (see components).</param>
/// <param name="Components">Priced items for a meal deal; empty for a normal item.</param>
public sealed record PricedLine(
    OrderLineInput Input,
    OfferResult Offer,
    Money UnitTotal,
    Money LineTotal,
    VatRate? VatRate,
    IReadOnlyList<PricedDealComponent> Components)
{
    /// <summary>Total saving from offers on this line.</summary>
    public Money Saving => Offer.UnitSaving * Input.Quantity;
}

/// <summary>The full priced order.</summary>
/// <param name="Lines">Priced lines, in entry order.</param>
/// <param name="Total">Amount to pay, VAT included.</param>
/// <param name="TotalSaving">Total saved through offers.</param>
/// <param name="VatSummary">VAT per rate; always adds up to the total.</param>
public sealed record PricedOrder(IReadOnlyList<PricedLine> Lines, Money Total, Money TotalSaving, IReadOnlyList<VatSummaryLine> VatSummary);

/// <summary>
/// Prices an order: applies scheduled offers, shares meal deal prices across their items,
/// works out VAT per item by order type, and builds the VAT summary.
/// Prices are VAT-inclusive, as on UK menus.
/// </summary>
public sealed class OrderPricer
{
    private readonly IVatPolicy _vatPolicy;
    private readonly IReadOnlyList<Promotion> _promotions;

    /// <summary>Creates a pricer with the VAT rules and the offers loaded for the branch.</summary>
    public OrderPricer(IVatPolicy vatPolicy, IReadOnlyList<Promotion> promotions)
    {
        _vatPolicy = vatPolicy ?? throw new ArgumentNullException(nameof(vatPolicy));
        _promotions = promotions ?? throw new ArgumentNullException(nameof(promotions));
    }

    /// <summary>Prices the order.</summary>
    public PricedOrder Price(OrderType orderType, PricingContext context, IReadOnlyList<OrderLineInput> lines, string currency = "GBP")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lines);

        var priced = new List<PricedLine>(lines.Count);
        var vatAmounts = new List<(VatRate, Money)>();

        foreach (var line in lines)
        {
            if (line.Quantity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(lines), "Quantity must be at least 1.");
            }

            var result = line switch
            {
                ItemLineInput item => PriceItem(item, orderType, context, vatAmounts),
                MealDealLineInput deal => PriceDeal(deal, orderType, context, vatAmounts),
                _ => throw new ArgumentException($"Unknown line type {line.GetType().Name}.", nameof(lines)),
            };
            priced.Add(result);
        }

        var total = priced.Aggregate(Money.Zero(currency), (sum, l) => sum + l.LineTotal);
        var saving = priced.Aggregate(Money.Zero(currency), (sum, l) => sum + l.Saving);
        return new PricedOrder(priced, total, saving, VatCalculator.Summarise(vatAmounts));
    }

    private PricedLine PriceItem(ItemLineInput line, OrderType orderType, PricingContext context, List<(VatRate, Money)> vatAmounts)
    {
        var offer = PromotionEngine.Apply(line.Item, context, _promotions);
        var unitTotal = line.Modifiers.Aggregate(offer.UnitPrice, (sum, m) => sum + m.UnitPrice);
        var lineTotal = unitTotal * line.Quantity;
        var rate = _vatPolicy.RateFor(line.VatCategory, orderType);
        vatAmounts.Add((rate, lineTotal));
        return new PricedLine(line, offer, unitTotal, lineTotal, rate, []);
    }

    private PricedLine PriceDeal(MealDealLineInput line, OrderType orderType, PricingContext context, List<(VatRate, Money)> vatAmounts)
    {
        if (line.Components.Count == 0)
        {
            throw new ArgumentException("A meal deal must have its choices filled in.", nameof(line));
        }

        var offer = PromotionEngine.Apply(line.Deal, context, _promotions);
        var shares = MealDealAllocator.Allocate(offer.UnitPrice, [.. line.Components.Select(c => c.Item.NormalUnitPrice)]);

        var components = new List<PricedDealComponent>(line.Components.Count);
        for (var i = 0; i < line.Components.Count; i++)
        {
            var component = line.Components[i];
            var unitTotal = shares[i] + (component.Upcharge ?? Money.Zero(offer.UnitPrice.Currency));
            unitTotal = component.Modifiers.Aggregate(unitTotal, (sum, m) => sum + m.UnitPrice);
            var rate = _vatPolicy.RateFor(component.VatCategory, orderType);
            vatAmounts.Add((rate, unitTotal * line.Quantity));
            components.Add(new PricedDealComponent(component, shares[i], unitTotal, rate));
        }

        var dealUnitTotal = components.Aggregate(Money.Zero(offer.UnitPrice.Currency), (sum, c) => sum + c.UnitTotal);
        return new PricedLine(line, offer, dealUnitTotal, dealUnitTotal * line.Quantity, null, components);
    }
}
