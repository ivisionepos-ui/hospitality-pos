using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Pricing;
using Pos.Domain.Reports;
using Pos.Domain.Tax;
using Pos.Domain.Tests.Menus;

namespace Pos.Domain.Tests.Reports;

/// <summary>Back-office sales reports for a range of days.</summary>
public class SalesReportTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly OrderActor Cashier = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly OrderActor Manager = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly DateOnly Day = new(2026, 11, 5);

    private readonly SampleMenu _s = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 5, 18, 10, 0, TimeSpan.Zero)); // 18:10 in London (GMT)
    private readonly OrderIntake _intake;
    private readonly MenuOrderBuilder _builder;

    public SalesReportTests()
    {
        _builder = new MenuOrderBuilder(_s.Menu);
        _intake = new OrderIntake(new InMemoryOrderStore(), new InMemoryOrderNumberSequence(),
            new OrderPricingSetup(new UkVatPolicy(), [], London, DiscountPolicy.ManagerAlways()), new TimeOnly(5, 0), _clock);
    }

    private NewOrderLine Line(ItemSelection s) => new(_builder.ToLine(s), _builder.DisplayFor(s));

    private NewOrderLine Doner => Line(new ItemSelection(_s.Doner, _s.DonerRegular, [_s.Garlic])); // £8.99, hot food 20%

    private NewOrderLine Can => Line(new ItemSelection(_s.Can, _s.CanRegular, []));                 // £1.20, 20%

    private Order NewOrder(OrderActor by, params NewOrderLine[] lines) => _intake.Submit(new NewOrder
    {
        OrderId = DeviceIds.New(_clock), IdempotencyKey = Guid.NewGuid().ToString(), BranchId = Guid.NewGuid(), BranchCode = "T",
        Source = OrderSource.Counter, Type = OrderType.Takeaway, CreatedBy = by, Lines = lines,
    }).Order;

    private SalesReport Report()
    {
        var shift = Guid.NewGuid();
        PaymentContext Ctx(OrderActor who) => new(shift, who.UserId, who.DeviceId, _clock.Now);

        // 1. Cashier: doner + can, £10.19 cash.
        var o1 = NewOrder(Cashier, Doner, Can);
        var p1 = new OrderPayments(o1.Id, o1.Totals.Total, []).TakeCash(Money.Gbp(1019), Ctx(Cashier));
        o1.Complete(Cashier);

        // 2. Manager: doner with £1 off (£7.99) by card; later £2 refunded by the cashier for a complaint.
        var o2 = NewOrder(Manager, Doner);
        o2.ApplyDiscount(new OrderDiscountInput(OrderDiscountType.Amount, 100, "Regular", Manager.UserId, Manager.UserId), Manager);
        var ledger2 = new OrderPayments(o2.Id, o2.Totals.Total, []);
        var p2 = ledger2.TakeCard(Money.Gbp(799), Ctx(Manager));
        o2.Complete(Manager);
        var r2 = ledger2.Refund(p2.Id, Money.Gbp(200), "Cold", Manager.UserId, Ctx(Cashier));

        // 3. A can, cancelled. 4. Doner + can, the can removed by the cashier, still open.
        var o3 = NewOrder(Cashier, Can);
        o3.Cancel("Left", Cashier);
        var o4 = NewOrder(Cashier, Doner, Can);
        o4.VoidLine(o4.Lines[1].Id, "Out of stock", Cashier);

        var names = new Dictionary<Guid, string> { [Cashier.UserId] = "Cara", [Manager.UserId] = "Max" };
        var categories = new Dictionary<Guid, string> { [_s.Kebabs] = "Kebabs", [_s.Drinks] = "Drinks" };
        return SalesReport.Build(Day, Day, [o1, o2, o3, o4], [p1, p2, r2], new Dictionary<Guid, PricedOrder> { [o2.Id] = o2.Totals }, London,
            id => categories.GetValueOrDefault(id, "Other"), id => names.GetValueOrDefault(id, "Unknown"));
    }

    [Fact]
    public void Summary_counts_completed_sales_discounts_refunds_cancels_and_removed_items()
    {
        var s = Report().Summary;

        Assert.Equal((2, 3), (s.Orders, s.ItemsSold));
        Assert.Equal(Money.Gbp(1019 + 799), s.Sales);
        Assert.Equal(Money.Gbp(909), s.AverageOrder);
        Assert.Equal(Money.Gbp(100), s.Discounts);
        Assert.Equal(Money.Gbp(200), s.Refunds);
        Assert.Equal(Money.Gbp(1618), s.NetSales);
        Assert.Equal((1, Money.Gbp(120), 1), (s.CancelledOrders, s.CancelledValue, s.VoidedItems));
    }

    [Fact]
    public void Items_and_categories_show_sales_after_their_share_of_discounts()
    {
        var report = Report();

        Assert.Equal((2, Money.Gbp(899 + 799)), (report.Items[0].Quantity, report.Items[0].Sales)); // the doners first
        Assert.Contains("Doner", report.Items[0].Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((1, Money.Gbp(120)), (report.Items[1].Quantity, report.Items[1].Sales));
        Assert.Equal([new SalesRow("Kebabs", 2, Money.Gbp(1698)), new SalesRow("Drinks", 1, Money.Gbp(120))], report.Categories);
    }

    [Fact]
    public void Hours_days_and_types_count_orders()
    {
        var report = Report();

        Assert.Equal([new SalesRow("18:00", 2, Money.Gbp(1818))], report.Hours);
        Assert.Equal([new SalesRow("2026-11-05", 2, Money.Gbp(1818))], report.Days);
        Assert.Equal([new SalesRow("Takeaway", 2, Money.Gbp(1818)), new SalesRow("From Counter", 2, Money.Gbp(1818))], report.Types);
    }

    [Fact]
    public void Staff_show_orders_taken_discounts_given_items_removed_and_refunds_made()
    {
        var staff = Report().Staff;

        Assert.Equal(new StaffRow("Cara", 1, Money.Gbp(1019), 0, Money.Gbp(0), 1, 1, Money.Gbp(200)), staff.Single(s => s.Name == "Cara"));
        Assert.Equal(new StaffRow("Max", 1, Money.Gbp(799), 1, Money.Gbp(100), 0, 0, Money.Gbp(0)), staff.Single(s => s.Name == "Max"));
    }

    [Fact]
    public void Payments_and_vat_reconcile_with_the_money_kept()
    {
        var report = Report();

        var cash = report.Payments.Single(p => p.Method == PaymentMethod.Cash);
        var card = report.Payments.Single(p => p.Method == PaymentMethod.Card);
        Assert.Equal(Money.Gbp(1019), cash.Net);
        Assert.Equal((Money.Gbp(799), Money.Gbp(200), Money.Gbp(599)), (card.Taken, card.Refunded, card.Net));
        var vat = Assert.Single(report.Vat);
        Assert.Equal(cash.Net + card.Net, vat.Gross); // money kept = VAT-able sales
        Assert.Equal(report.Summary.NetSales, vat.Gross);
    }

    [Fact]
    public void An_empty_range_gives_zeros_and_a_backwards_range_is_refused()
    {
        var empty = SalesReport.Build(Day, Day.AddDays(6), [], [], new Dictionary<Guid, PricedOrder>(), London, _ => "", _ => "");

        Assert.Equal(0, empty.Summary.Orders);
        Assert.Equal(Money.Gbp(0), empty.Summary.AverageOrder);
        Assert.Empty(empty.Items);
        Assert.Throws<ArgumentException>(() => SalesReport.Build(Day, Day.AddDays(-1), [], [], new Dictionary<Guid, PricedOrder>(), London, _ => "", _ => ""));
    }
}
