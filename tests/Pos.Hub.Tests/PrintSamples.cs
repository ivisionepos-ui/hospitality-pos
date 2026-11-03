using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Pricing;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>A realistic order with Arabic names, a meal deal, an offer, a discount and payments, for print tests.</summary>
internal static class PrintSamples
{
    public static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    public static readonly OrderActor Cashier = new(Guid.NewGuid(), Guid.NewGuid());

    public static ReceiptSettings Shop => new()
    {
        BusinessName = "Olive Grill",
        BusinessNameAr = "مطعم الزيتون",
        AddressLines = ["12 High Street", "Leeds LS1 4AB"],
        Phone = "0113 496 0000",
        VatNumber = "GB 123 4567 89",
        Footer = "Thank you for your order",
        FooterAr = "شكراً لطلبكم",
        Language = "both",
    };

    public static Menu Menu()
    {
        var state = TestMenu.State();
        var products = state.Products.Select(p => p.Id == TestMenu.Doner
                ? p with { Name = new LocalizedText("Doner kebab", "دونر كباب") }
                : p.Id == TestMenu.Can
                    ? p with { Name = new LocalizedText("Can 330ml", "مشروب غازي") }
                    : p.Id == TestMenu.Margherita
                        ? p with { Name = new LocalizedText("Margherita", "بيتزا مارغريتا") }
                        : p)
            .ToList();
        var groups = state.ModifierGroups.Select(g => g with
        {
            Options = [.. g.Options.Select(o => o with { Name = new LocalizedText("Garlic sauce", "صلصة الثوم") })],
        }).ToList();
        return Domain.Menus.Menu.Import(state with { Products = products, ModifierGroups = groups }, Guid.NewGuid());
    }

    /// <summary>Builds the sample order and its payments.</summary>
    public static (Order Order, OrderPayments Payments) Order()
    {
        var menu = Menu();
        var builder = new MenuOrderBuilder(menu);
        var clock = new TestClock(new DateTimeOffset(2026, 11, 3, 18, 42, 0, TimeSpan.Zero)); // a Tuesday
        var cansOffer = new Promotion(Guid.NewGuid(), "Tuesday cans 99p", PromotionType.OfferPrice, 99,
            [new PromotionTarget(PromotionTargetKind.Product, TestMenu.Can)], [new WeeklyTimeWindow([DayOfWeek.Tuesday])]);
        var intake = new OrderIntake(new InMemoryOrderStore(), new InMemoryOrderNumberSequence(),
            new OrderPricingSetup(new UkVatPolicy(), [cansOffer], London, new DiscountPolicy(1000, Money.Gbp(500))), new TimeOnly(5, 0), clock);

        var deal = new DealSelection(TestMenu.Deal, TestMenu.DealRegular,
        [
            new SlotChoice(TestMenu.PizzaSlot, TestMenu.Margherita, TestMenu.Margherita12, []),
            new SlotChoice(TestMenu.KebabSlot, TestMenu.Doner, TestMenu.DonerRegular, [TestMenu.Garlic]),
            new SlotChoice(TestMenu.DrinkSlot, TestMenu.Can, TestMenu.CanRegular, []),
        ]);
        var cans = new ItemSelection(TestMenu.Can, TestMenu.CanRegular, [], Quantity: 2);
        var doner = new ItemSelection(TestMenu.Doner, TestMenu.DonerRegular, [TestMenu.Garlic]);

        var order = intake.Submit(new NewOrder
        {
            OrderId = DeviceIds.New(clock), IdempotencyKey = "sample", BranchId = Guid.NewGuid(), BranchCode = "OG1",
            Source = OrderSource.Phone, Type = OrderType.Takeaway, CreatedBy = Cashier,
            Lines =
            [
                new NewOrderLine(builder.ToLine(cans), builder.DisplayFor(cans)),
                new NewOrderLine(builder.ToLine(doner), builder.DisplayFor(doner), "No onions"),
                new NewOrderLine(builder.ToLine(deal), builder.DisplayFor(deal), "Cut pizza in 8"),
            ],
        }).Order;
        order.ApplyDiscount(new OrderDiscountInput(OrderDiscountType.Amount, 200, "Regular customer", Cashier.UserId), Cashier);

        var ledger = new OrderPayments(order.Id, order.Totals.Total, []);
        ledger.TakeCash(Money.Gbp(4000), new PaymentContext(Guid.NewGuid(), Cashier.UserId, Cashier.DeviceId, clock.Now));
        return (order, ledger);
    }
}
