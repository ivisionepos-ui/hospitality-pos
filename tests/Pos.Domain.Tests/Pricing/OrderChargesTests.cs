using Pos.Domain.Common;
using Pos.Domain.Delivery;
using Pos.Domain.Pricing;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;

namespace Pos.Domain.Tests.Pricing;

/// <summary>Order discounts, delivery fees and service charges on top of item pricing.</summary>
public class OrderChargesTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly Guid Cashier = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();

    private static readonly PricingItem Pizza = new(Guid.NewGuid(), null, Guid.NewGuid(), Money.Gbp(1200));
    private static readonly PricingItem Salad = new(Guid.NewGuid(), null, Guid.NewGuid(), Money.Gbp(300));
    private static readonly PricingItem Deal = new(Guid.NewGuid(), null, Guid.NewGuid(), Money.Gbp(1000));

    private static readonly PricingContext Thursday = new(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero), London, Guid.NewGuid(), SalesChannel.Phone);

    // Staff may give up to 10% or £5 without a manager.
    private static readonly DiscountPolicy Policy = new(1000, Money.Gbp(500));

    private static readonly DeliveryChargeTable Bands = DeliveryChargeTable.Banded(
        [new DeliveryBand(0m, 1m, Money.Gbp(150)), new DeliveryBand(1m, 3m, Money.Gbp(250)), new DeliveryBand(3m, 5m, Money.Gbp(350))],
        maximumMiles: 5m);

    private static readonly OrderPricer Pricer = new(new UkVatPolicy(), []);

    // Hot pizza £12.00 and cold salad £3.00 = £15.00.
    private static List<OrderLineInput> PizzaAndSalad() =>
    [
        new ItemLineInput { Item = Pizza, VatCategory = VatCategory.HotFood, Quantity = 1 },
        new ItemLineInput { Item = Salad, VatCategory = VatCategory.ColdFood, Quantity = 1 },
    ];

    private static OrderPricingRequest Request(OrderType type) =>
        new() { OrderType = type, Context = Thursday, Lines = PizzaAndSalad(), DiscountPolicy = Policy };

    private static Money SumGross(PricedOrder order) => order.VatSummary.Aggregate(Money.Gbp(0), (s, l) => s + l.Gross);

    [Fact]
    public void Delivery_order_with_discount_and_fee_spreads_both_across_vat_rates()
    {
        var order = Pricer.Price(Request(OrderType.Delivery) with
        {
            Discount = new OrderDiscountInput(OrderDiscountType.Percent, 1000, "Regular customer", Cashier),
            Delivery = new DeliveryInput(2.3m, Bands),
        });

        // 10% of £15.00 = £1.50 off, split 120p / 30p by value; delivery £2.50 split 200p / 50p.
        Assert.Equal(Money.Gbp(1500), order.GoodsTotal);
        Assert.Equal(Money.Gbp(150), order.Discount!.AmountOff);
        Assert.False(order.Discount.NeededApproval);
        Assert.Equal(Money.Gbp(250), order.Delivery!.Fee);
        Assert.Equal(Money.Gbp(1600), order.Total);

        Assert.Equal(Money.Gbp(120), order.Lines[0].DiscountShare);
        Assert.Equal(Money.Gbp(30), order.Lines[1].DiscountShare);

        var standard = order.VatSummary.Single(v => v.Rate.BasisPoints == 2000);
        var zero = order.VatSummary.Single(v => v.Rate.BasisPoints == 0);
        Assert.Equal(Money.Gbp(1280), standard.Gross);  // 1080 food + 200 delivery
        Assert.Equal(Money.Gbp(213), standard.Vat);     // 213.33p
        Assert.Equal(Money.Gbp(320), zero.Gross);       // 270 food + 50 delivery
        Assert.Equal(order.Total, SumGross(order));
    }

    [Fact]
    public void Discount_above_staff_limit_needs_a_manager()
    {
        var request = Request(OrderType.Takeaway) with
        {
            Discount = new OrderDiscountInput(OrderDiscountType.Percent, 1500, "Complaint", Cashier),
        };

        Assert.Throws<DiscountApprovalRequiredException>(() => Pricer.Price(request));

        var approved = Pricer.Price(request with { Discount = request.Discount with { ApprovedByUserId = Manager } });
        Assert.True(approved.Discount!.NeededApproval);
        Assert.Equal(Money.Gbp(225), approved.Discount.AmountOff); // 15% of £15.00
    }

    [Fact]
    public void Amount_discount_over_the_staff_limit_needs_a_manager()
    {
        var request = Request(OrderType.Takeaway) with
        {
            Discount = new OrderDiscountInput(OrderDiscountType.Amount, 600, "Late order", Cashier),
        };

        Assert.Throws<DiscountApprovalRequiredException>(() => Pricer.Price(request));
    }

    [Fact]
    public void Default_policy_requires_a_manager_for_every_discount()
    {
        var request = Request(OrderType.Takeaway) with
        {
            DiscountPolicy = null,
            Discount = new OrderDiscountInput(OrderDiscountType.Amount, 10, "Small", Cashier),
        };

        Assert.Throws<DiscountApprovalRequiredException>(() => Pricer.Price(request));
    }

    [Fact]
    public void Amount_discount_is_capped_so_the_order_never_goes_negative()
    {
        var order = Pricer.Price(Request(OrderType.Takeaway) with
        {
            Discount = new OrderDiscountInput(OrderDiscountType.Amount, 5000, "Free meal", Cashier, Manager),
        });

        Assert.Equal(Money.Gbp(1500), order.Discount!.AmountOff);
        Assert.Equal(Money.Gbp(0), order.Total);
        Assert.Empty(order.VatSummary);
    }

    [Fact]
    public void Discount_requires_a_reason_and_a_sensible_value()
    {
        Assert.Throws<ArgumentException>(() => Pricer.Price(Request(OrderType.Takeaway) with
        {
            Discount = new OrderDiscountInput(OrderDiscountType.Amount, 100, " ", Cashier, Manager),
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pricer.Price(Request(OrderType.Takeaway) with
        {
            Discount = new OrderDiscountInput(OrderDiscountType.Percent, 10_001, "Too much", Cashier, Manager),
        }));
    }

    [Fact]
    public void Discount_share_is_recorded_on_each_meal_deal_item()
    {
        var lines = new List<OrderLineInput>
        {
            new MealDealLineInput
            {
                Deal = Deal, Quantity = 1,
                Components =
                [
                    new DealComponentInput { Item = Pizza, VatCategory = VatCategory.HotFood },
                    new DealComponentInput { Item = Salad, VatCategory = VatCategory.ColdFood },
                ],
            },
        };

        var order = Pricer.Price(new OrderPricingRequest
        {
            OrderType = OrderType.Takeaway, Context = Thursday, Lines = lines, DiscountPolicy = Policy,
            Discount = new OrderDiscountInput(OrderDiscountType.Amount, 100, "Voucher", Cashier),
        });

        // Deal £10.00 shared 800 / 200; £1.00 off shared 80 / 20.
        var deal = order.Lines[0];
        Assert.Equal(Money.Gbp(100), deal.DiscountShare);
        Assert.Equal(Money.Gbp(80), deal.Components[0].DiscountShare);
        Assert.Equal(Money.Gbp(20), deal.Components[1].DiscountShare);
        Assert.Equal(Money.Gbp(900), order.Total);
    }

    [Fact]
    public void Staff_can_override_the_delivery_fee_with_a_reason()
    {
        var order = Pricer.Price(Request(OrderType.Delivery) with
        {
            Delivery = new DeliveryInput(2.3m, Bands, new DeliveryFeeOverride(Money.Gbp(0), "Regular customer, free delivery", Manager)),
        });

        Assert.Equal(Money.Gbp(250), order.Delivery!.Quote.Fee);
        Assert.Equal(Money.Gbp(0), order.Delivery.Fee);
        Assert.Equal(Money.Gbp(1500), order.Total);
    }

    [Fact]
    public void Delivery_fee_override_needs_a_reason()
    {
        Assert.Throws<ArgumentException>(() => Pricer.Price(Request(OrderType.Delivery) with
        {
            Delivery = new DeliveryInput(2.3m, Bands, new DeliveryFeeOverride(Money.Gbp(0), "", Manager)),
        }));
    }

    [Fact]
    public void Too_far_is_reported_and_no_fee_is_charged()
    {
        var order = Pricer.Price(Request(OrderType.Delivery) with { Delivery = new DeliveryInput(6m, Bands) });

        Assert.Equal(DeliveryQuoteStatus.BeyondMaximumDistance, order.Delivery!.Quote.Status);
        Assert.Equal(Money.Gbp(1500), order.Total);
    }

    [Fact]
    public void Delivery_charge_only_on_delivery_orders()
    {
        Assert.Throws<ArgumentException>(() => Pricer.Price(Request(OrderType.Takeaway) with { Delivery = new DeliveryInput(1m, Bands) }));
    }

    [Fact]
    public void Optional_service_charge_is_outside_the_scope_of_vat()
    {
        var order = Pricer.Price(Request(OrderType.EatIn) with { ServiceCharge = new ServiceChargeInput(1250) });

        // 12.5% of £15.00 = 187.5p -> 188p
        Assert.Equal(Money.Gbp(188), order.ServiceCharge!.Amount);
        Assert.Equal(Money.Gbp(1688), order.Total);
        Assert.Equal(Money.Gbp(188), order.OutsideScopeOfVat);

        var vat = Assert.Single(order.VatSummary);
        Assert.Equal(Money.Gbp(1500), vat.Gross);
        Assert.Equal(Money.Gbp(250), vat.Vat);
        Assert.Equal(order.Total, SumGross(order) + order.OutsideScopeOfVat);
    }

    [Fact]
    public void Compulsory_service_charge_carries_vat()
    {
        var order = Pricer.Price(Request(OrderType.EatIn) with { ServiceCharge = new ServiceChargeInput(1250, IsOptional: false) });

        var vat = Assert.Single(order.VatSummary);
        Assert.Equal(Money.Gbp(1688), vat.Gross);
        Assert.Equal(Money.Gbp(281), vat.Vat); // 281.33p
        Assert.Equal(Money.Gbp(0), order.OutsideScopeOfVat);
    }

    [Fact]
    public void Service_charge_is_worked_out_after_the_discount()
    {
        var order = Pricer.Price(Request(OrderType.EatIn) with
        {
            Discount = new OrderDiscountInput(OrderDiscountType.Amount, 300, "Birthday", Cashier),
            ServiceCharge = new ServiceChargeInput(1000),
        });

        // £15.00 - £3.00 = £12.00; 10% service = £1.20
        Assert.Equal(Money.Gbp(120), order.ServiceCharge!.Amount);
        Assert.Equal(Money.Gbp(1320), order.Total);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void Rejects_invalid_service_charge(int basisPoints)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Pricer.Price(Request(OrderType.EatIn) with { ServiceCharge = new ServiceChargeInput(basisPoints) }));
    }

    [Fact]
    public void Totals_and_vat_always_reconcile_across_random_orders()
    {
        var random = new Random(2026);
        for (var run = 0; run < 1000; run++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => (OrderLineInput)new ItemLineInput
                {
                    Item = new PricingItem(Guid.NewGuid(), null, Guid.NewGuid(), Money.Gbp(random.Next(0, 3000))),
                    VatCategory = (VatCategory)random.Next(0, 3),
                    Quantity = random.Next(1, 4),
                })
                .ToList();

            var order = Pricer.Price(new OrderPricingRequest
            {
                OrderType = OrderType.Delivery,
                Context = Thursday,
                Lines = lines,
                DiscountPolicy = Policy,
                Discount = new OrderDiscountInput(OrderDiscountType.Percent, random.Next(1, 10_001), "Test", Cashier, Manager),
                Delivery = new DeliveryInput((decimal)random.Next(0, 500) / 100m, Bands),
                ServiceCharge = new ServiceChargeInput(random.Next(1, 2000), IsOptional: random.Next(2) == 0),
            });

            Assert.Equal(order.Total, SumGross(order) + order.OutsideScopeOfVat);
            Assert.Equal(order.Discount!.AmountOff, order.Lines.Aggregate(Money.Gbp(0), (s, l) => s + l.DiscountShare));
            Assert.False(order.Total.IsNegative);
        }
    }
}
