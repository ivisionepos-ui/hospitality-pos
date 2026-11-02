using System.Text.Json.Serialization;
using Pos.Domain.Common;
using Pos.Domain.Delivery;
using Pos.Domain.MealDeals;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;

namespace Pos.Domain.Pricing;

/// <summary>An extra or choice added to an item (e.g. extra cheese). Charged at its normal price, per unit.</summary>
/// <param name="OptionId">The modifier option.</param>
/// <param name="UnitPrice">Price per unit of the parent item.</param>
public sealed record ModifierInput(Guid OptionId, Money UnitPrice);

/// <summary>A line to be priced: either a normal item or a meal deal.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ItemLineInput), "item")]
[JsonDerivedType(typeof(MealDealLineInput), "deal")]
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

/// <summary>Everything needed to price an order.</summary>
public sealed record OrderPricingRequest
{
    /// <summary>Eat-in, takeaway or delivery. Decides VAT on food.</summary>
    public required OrderType OrderType { get; init; }

    /// <summary>When, where and through which channel the order is placed.</summary>
    public required PricingContext Context { get; init; }

    /// <summary>Items and meal deals.</summary>
    public required IReadOnlyList<OrderLineInput> Lines { get; init; }

    /// <summary>Discount on the whole order, or null.</summary>
    public OrderDiscountInput? Discount { get; init; }

    /// <summary>Staff discount limits. Default: every discount needs a manager.</summary>
    public DiscountPolicy? DiscountPolicy { get; init; }

    /// <summary>Delivery distance and charges; only for delivery orders.</summary>
    public DeliveryInput? Delivery { get; init; }

    /// <summary>Service charge, or null.</summary>
    public ServiceChargeInput? ServiceCharge { get; init; }

    /// <summary>Currency of the order.</summary>
    public string Currency { get; init; } = "GBP";
}

/// <summary>A priced item inside a meal deal (per deal unit).</summary>
/// <param name="Input">What was chosen.</param>
/// <param name="AllocatedShare">This item's share of the deal price.</param>
/// <param name="UnitTotal">Share + upcharge + extras, for one deal.</param>
/// <param name="VatRate">VAT rate for this item.</param>
public sealed record PricedDealComponent(DealComponentInput Input, Money AllocatedShare, Money UnitTotal, VatRate VatRate)
{
    /// <summary>This item's share of the order discount (whole line quantity).</summary>
    public Money DiscountShare { get; init; } = Money.Gbp(0);
}

/// <summary>A priced order line.</summary>
/// <param name="Input">The line as entered.</param>
/// <param name="Offer">Offer result for the item (or deal) price.</param>
/// <param name="UnitTotal">Charge for one unit, extras and upcharges included.</param>
/// <param name="LineTotal">UnitTotal × quantity, before any order discount.</param>
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

    /// <summary>This line's share of the order discount (for per-item reports).</summary>
    public Money DiscountShare { get; init; } = Money.Gbp(0);
}

/// <summary>The order discount as applied.</summary>
/// <param name="Input">What staff entered.</param>
/// <param name="AmountOff">Amount taken off.</param>
/// <param name="NeededApproval">True when it was above the staff limit.</param>
public sealed record AppliedDiscount(OrderDiscountInput Input, Money AmountOff, bool NeededApproval);

/// <summary>The delivery charge as applied.</summary>
/// <param name="Quote">What the charge table says.</param>
/// <param name="Fee">Fee charged (the override if there is one).</param>
/// <param name="Override">Staff override, or null.</param>
public sealed record AppliedDelivery(DeliveryQuote Quote, Money Fee, DeliveryFeeOverride? Override);

/// <summary>The service charge as applied.</summary>
/// <param name="Input">Rate and optional/compulsory.</param>
/// <param name="Amount">Amount charged.</param>
public sealed record AppliedServiceCharge(ServiceChargeInput Input, Money Amount);

/// <summary>The full priced order.</summary>
public sealed record PricedOrder
{
    /// <summary>Priced lines, in entry order.</summary>
    public required IReadOnlyList<PricedLine> Lines { get; init; }

    /// <summary>Food and drink total after offers, before the order discount.</summary>
    public required Money GoodsTotal { get; init; }

    /// <summary>Order discount, or null.</summary>
    public AppliedDiscount? Discount { get; init; }

    /// <summary>Delivery charge, or null.</summary>
    public AppliedDelivery? Delivery { get; init; }

    /// <summary>Service charge, or null.</summary>
    public AppliedServiceCharge? ServiceCharge { get; init; }

    /// <summary>Amount to pay.</summary>
    public required Money Total { get; init; }

    /// <summary>Total saved through scheduled offers (not counting the order discount).</summary>
    public required Money TotalSaving { get; init; }

    /// <summary>VAT per rate. Gross amounts add up to Total minus OutsideScopeOfVat.</summary>
    public required IReadOnlyList<VatSummaryLine> VatSummary { get; init; }

    /// <summary>Amounts with no VAT at all (an optional service charge).</summary>
    public required Money OutsideScopeOfVat { get; init; }
}

/// <summary>
/// Prices an order: scheduled offers, meal deals, order discount, delivery fee, service charge and VAT.
/// Prices are VAT-inclusive, as on UK menus. The order discount, delivery fee and compulsory service charge
/// are spread across the items in proportion to their value, so each VAT rate gets its fair share.
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

    /// <summary>Prices items only (no discount, delivery or service charge).</summary>
    public PricedOrder Price(OrderType orderType, PricingContext context, IReadOnlyList<OrderLineInput> lines, string currency = "GBP") =>
        Price(new OrderPricingRequest { OrderType = orderType, Context = context, Lines = lines, Currency = currency });

    /// <summary>Prices the full order.</summary>
    public PricedOrder Price(OrderPricingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(request.Lines);

        if (request.Delivery is not null && request.OrderType != OrderType.Delivery)
        {
            throw new ArgumentException("A delivery charge can only be added to a delivery order.", nameof(request));
        }

        var zero = Money.Zero(request.Currency);
        var parts = new List<Part>();
        var priced = new List<PricedLine>(request.Lines.Count);

        // 1. Items and meal deals, after scheduled offers.
        for (var lineIndex = 0; lineIndex < request.Lines.Count; lineIndex++)
        {
            var line = request.Lines[lineIndex];
            if (line.Quantity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Quantity must be at least 1.");
            }

            priced.Add(line switch
            {
                ItemLineInput item => PriceItem(item, lineIndex, request, parts),
                MealDealLineInput deal => PriceDeal(deal, lineIndex, request, parts),
                _ => throw new ArgumentException($"Unknown line type {line.GetType().Name}.", nameof(request)),
            });
        }

        var goodsTotal = parts.Aggregate(zero, (sum, p) => sum + p.Gross);

        // 2. Order discount, spread across items by value.
        AppliedDiscount? appliedDiscount = null;
        var discountShares = parts.Select(_ => zero).ToArray();
        if (request.Discount is { } discount)
        {
            var amountOff = OrderDiscountCalculator.AmountOff(discount, goodsTotal);
            var policy = request.DiscountPolicy ?? DiscountPolicy.ManagerAlways(request.Currency);
            var needsApproval = OrderDiscountCalculator.NeedsApproval(discount, amountOff, policy);
            OrderDiscountCalculator.EnsureApproved(discount, amountOff, policy);
            if (parts.Count > 0)
            {
                discountShares = [.. ProportionalAllocator.Allocate(amountOff, [.. parts.Select(p => p.Gross)])];
            }

            appliedDiscount = new AppliedDiscount(discount, amountOff, needsApproval);
        }

        var discounted = parts.Select((p, i) => p with { Gross = p.Gross - discountShares[i] }).ToList();
        var goodsAfterDiscount = discounted.Aggregate(zero, (sum, p) => sum + p.Gross);
        var vatAmounts = discounted.Select(p => (p.Rate, p.Gross)).ToList();

        // 3. Delivery fee: VAT follows the food it delivers.
        AppliedDelivery? appliedDelivery = null;
        if (request.Delivery is { } delivery)
        {
            var quote = delivery.Table.Quote(delivery.DistanceMiles, goodsAfterDiscount);
            var fee = quote.Fee;
            if (delivery.Override is { } feeOverride)
            {
                if (string.IsNullOrWhiteSpace(feeOverride.Reason))
                {
                    throw new ArgumentException("A reason is required when changing the delivery fee.", nameof(request));
                }

                if (feeOverride.Fee.IsNegative)
                {
                    throw new ArgumentException("Delivery fee cannot be negative.", nameof(request));
                }

                fee = feeOverride.Fee;
            }

            vatAmounts.AddRange(SpreadAcross(fee, discounted, request.Currency));
            appliedDelivery = new AppliedDelivery(quote, fee, delivery.Override);
        }

        // 4. Service charge on the food and drink after discount.
        AppliedServiceCharge? appliedService = null;
        var outsideScope = zero;
        if (request.ServiceCharge is { } service)
        {
            service.Validate();
            var amount = new Money(Rounding.ApplyBasisPoints(goodsAfterDiscount.MinorUnits, service.BasisPoints), request.Currency);
            if (service.IsOptional)
            {
                outsideScope += amount;
            }
            else
            {
                vatAmounts.AddRange(SpreadAcross(amount, discounted, request.Currency));
            }

            appliedService = new AppliedServiceCharge(service, amount);
        }

        // 5. Record each line's share of the discount, for reports.
        for (var i = 0; i < priced.Count; i++)
        {
            var lineParts = Enumerable.Range(0, parts.Count).Where(p => parts[p].LineIndex == i).ToList();
            var lineShare = lineParts.Aggregate(zero, (sum, p) => sum + discountShares[p]);
            var components = priced[i].Components
                .Select((c, ci) => c with
                {
                    DiscountShare = lineParts.Where(p => parts[p].ComponentIndex == ci)
                        .Aggregate(zero, (sum, p) => sum + discountShares[p]),
                })
                .ToList();
            priced[i] = priced[i] with { DiscountShare = lineShare, Components = components };
        }

        var total = goodsAfterDiscount
            + (appliedDelivery?.Fee ?? zero)
            + (appliedService?.Amount ?? zero);

        return new PricedOrder
        {
            Lines = priced,
            GoodsTotal = goodsTotal,
            Discount = appliedDiscount,
            Delivery = appliedDelivery,
            ServiceCharge = appliedService,
            Total = total,
            TotalSaving = priced.Aggregate(zero, (sum, l) => sum + l.Saving),
            VatSummary = VatCalculator.Summarise(vatAmounts.Where(v => v.Gross.MinorUnits != 0)),
            OutsideScopeOfVat = outsideScope,
        };
    }

    private static IEnumerable<(VatRate Rate, Money Gross)> SpreadAcross(Money amount, IReadOnlyList<Part> parts, string currency)
    {
        if (amount.MinorUnits == 0)
        {
            return [];
        }

        if (parts.Count == 0)
        {
            throw new InvalidOperationException("A charge cannot be added to an order with no items.");
        }

        var shares = ProportionalAllocator.Allocate(new Money(amount.MinorUnits, currency), [.. parts.Select(p => p.Gross)]);
        return parts.Select((p, i) => (p.Rate, shares[i]));
    }

    private PricedLine PriceItem(ItemLineInput line, int lineIndex, OrderPricingRequest request, List<Part> parts)
    {
        var offer = PromotionEngine.Apply(line.Item, request.Context, _promotions);
        var unitTotal = line.Modifiers.Aggregate(offer.UnitPrice, (sum, m) => sum + m.UnitPrice);
        var lineTotal = unitTotal * line.Quantity;
        var rate = _vatPolicy.RateFor(line.VatCategory, request.OrderType);
        parts.Add(new Part(lineIndex, null, rate, lineTotal));
        return new PricedLine(line, offer, unitTotal, lineTotal, rate, []);
    }

    private PricedLine PriceDeal(MealDealLineInput line, int lineIndex, OrderPricingRequest request, List<Part> parts)
    {
        if (line.Components.Count == 0)
        {
            throw new ArgumentException("A meal deal must have its choices filled in.", nameof(line));
        }

        var offer = PromotionEngine.Apply(line.Deal, request.Context, _promotions);
        var shares = MealDealAllocator.Allocate(offer.UnitPrice, [.. line.Components.Select(c => c.Item.NormalUnitPrice)]);
        var currency = offer.UnitPrice.Currency;

        var components = new List<PricedDealComponent>(line.Components.Count);
        for (var i = 0; i < line.Components.Count; i++)
        {
            var component = line.Components[i];
            var unitTotal = shares[i] + (component.Upcharge ?? Money.Zero(currency));
            unitTotal = component.Modifiers.Aggregate(unitTotal, (sum, m) => sum + m.UnitPrice);
            var rate = _vatPolicy.RateFor(component.VatCategory, request.OrderType);
            parts.Add(new Part(lineIndex, i, rate, unitTotal * line.Quantity));
            components.Add(new PricedDealComponent(component, shares[i], unitTotal, rate) { DiscountShare = Money.Zero(currency) });
        }

        var dealUnitTotal = components.Aggregate(Money.Zero(currency), (sum, c) => sum + c.UnitTotal);
        return new PricedLine(line, offer, dealUnitTotal, dealUnitTotal * line.Quantity, null, components);
    }

    /// <summary>One VAT-able piece of the order: a normal line, or one item inside a meal deal.</summary>
    private sealed record Part(int LineIndex, int? ComponentIndex, VatRate Rate, Money Gross);
}
