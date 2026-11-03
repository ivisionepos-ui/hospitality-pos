using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Pricing;
using Pos.Domain.Tax;
using Pos.Domain.Tests.Menus;

namespace Pos.Domain.Tests.Payments;

public class ShiftTests
{
    private static readonly Guid Till = Guid.NewGuid();
    private static readonly Guid Cashier = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 11, 5);

    private static Shift OpenShift() => Shift.Open(Guid.NewGuid(), Till, Day, Cashier, Now, Money.Gbp(10000));

    private static Payment Pay(Shift shift, PaymentMethod method, long pence, long tip = 0) =>
        new OrderPayments(Guid.NewGuid(), Money.Gbp(pence), []) is var ledger && method == PaymentMethod.Cash
            ? ledger.TakeCash(Money.Gbp(pence), new PaymentContext(shift.Id, Cashier, Till, Now))
            : ledger.TakeOther(method, Money.Gbp(pence), new PaymentContext(shift.Id, Cashier, Till, Now), tip: tip > 0 ? Money.Gbp(tip) : null);

    [Fact]
    public void Expected_cash_is_float_plus_cash_sales_and_paid_ins_less_refunds_and_paid_outs()
    {
        var shift = OpenShift();
        var sale1 = Pay(shift, PaymentMethod.Cash, 1500);
        var sale2 = Pay(shift, PaymentMethod.Cash, 2000);
        var card = Pay(shift, PaymentMethod.Card, 3000, tip: 300);
        var refundLedger = new OrderPayments(sale2.OrderId, Money.Gbp(2000), [sale2]);
        var refund = refundLedger.Refund(sale2.Id, Money.Gbp(500), "Wrong order", Manager, new PaymentContext(shift.Id, Cashier, Till, Now));
        var payments = new[] { sale1, sale2, card, refund };

        shift = shift.Move(new CashMovement(Guid.NewGuid(), CashMovementType.PaidIn, Money.Gbp(2000), "Change from safe", Cashier, Manager, Now), payments);
        shift = shift.Move(new CashMovement(Guid.NewGuid(), CashMovementType.PaidOut, Money.Gbp(1200), "Milk", Cashier, Manager, Now), payments);
        var summary = shift.Summarise(payments);

        // 100.00 + 35.00 - 5.00 + 20.00 - 12.00 = 138.00
        Assert.Equal(Money.Gbp(13800), summary.ExpectedCash);
        Assert.Equal(Money.Gbp(3500), summary.CashSales);
        Assert.Equal(Money.Gbp(500), summary.CashRefunds);
        var cardTotals = summary.ByMethod.Single(m => m.Method == PaymentMethod.Card);
        Assert.Equal(Money.Gbp(3000), cardTotals.Taken);
        Assert.Equal(Money.Gbp(300), cardTotals.Tips);
        Assert.Null(summary.Variance);
    }

    [Fact]
    public void Closing_with_a_count_shows_over_or_short()
    {
        var shift = OpenShift();
        var sale = Pay(shift, PaymentMethod.Cash, 1500);

        var closed = shift.Close(Cashier, Now.AddHours(6), Money.Gbp(11450));
        var summary = closed.Summarise([sale]);

        Assert.False(closed.IsOpen);
        Assert.Equal(Money.Gbp(11500), summary.ExpectedCash);
        Assert.Equal(Money.Gbp(-50), summary.Variance); // 50p short
        Assert.Throws<InvalidOperationException>(() => closed.Close(Cashier, Now, Money.Gbp(0)));
    }

    [Fact]
    public void Paid_out_needs_a_reason_and_cannot_empty_the_drawer_below_zero()
    {
        var shift = OpenShift();

        Assert.Throws<ArgumentException>(() => shift.Move(new CashMovement(Guid.NewGuid(), CashMovementType.PaidOut, Money.Gbp(100), " ", Cashier, Manager, Now), []));
        Assert.Throws<InvalidOperationException>(() => shift.Move(new CashMovement(Guid.NewGuid(), CashMovementType.PaidOut, Money.Gbp(10001), "Too much", Cashier, Manager, Now), []));
        Assert.Throws<ArgumentException>(() => shift.Move(new CashMovement(Guid.NewGuid(), CashMovementType.PaidIn, Money.Gbp(0), "Nothing", Cashier, Manager, Now), []));
    }

    [Fact]
    public void Float_cannot_be_negative()
    {
        Assert.Throws<ArgumentException>(() => Shift.Open(Guid.NewGuid(), Till, Day, Cashier, Now, Money.Gbp(-1)));
    }

    [Fact]
    public void Payments_of_other_shifts_are_ignored()
    {
        var shift = OpenShift();
        var other = OpenShift();

        var summary = shift.Summarise([Pay(other, PaymentMethod.Cash, 999)]);

        Assert.Equal(Money.Gbp(10000), summary.ExpectedCash);
    }
}

public class DayReportTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly OrderActor Cashier = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 11, 5);

    private readonly SampleMenu _menu = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero));
    private readonly OrderIntake _intake;
    private readonly MenuOrderBuilderAlias _b;

    public DayReportTests()
    {
        _intake = new OrderIntake(new InMemoryOrderStore(), new InMemoryOrderNumberSequence(),
            new OrderPricingSetup(new UkVatPolicy(), [], London, DiscountPolicy.ManagerAlways()), new TimeOnly(5, 0), _clock);
        _b = new MenuOrderBuilderAlias(new Domain.Menus.MenuOrderBuilder(_menu.Menu));
    }

    /// <summary>Small helper to build order lines from the sample menu.</summary>
    private sealed class MenuOrderBuilderAlias(Domain.Menus.MenuOrderBuilder builder)
    {
        public NewOrderLine Line(Domain.Menus.ItemSelection s) => new(builder.ToLine(s), builder.DisplayFor(s));
    }

    private Order NewOrder(OrderSource source, params NewOrderLine[] lines) => _intake.Submit(new NewOrder
    {
        OrderId = DeviceIds.New(_clock), IdempotencyKey = Guid.NewGuid().ToString(), BranchId = Guid.NewGuid(), BranchCode = "T",
        Source = source, Type = OrderType.Takeaway, CreatedBy = Cashier, Lines = lines,
    }).Order;

    private NewOrderLine Doner => _b.Line(new Domain.Menus.ItemSelection(_menu.Doner, _menu.DonerRegular, [_menu.Garlic]));   // £8.99 hot, 20%

    private NewOrderLine Can => _b.Line(new Domain.Menus.ItemSelection(_menu.Can, _menu.CanRegular, []));                     // £1.20, 20%

    [Fact]
    public void Z_report_reconciles_sales_payments_vat_refunds_and_cash()
    {
        var shift = Shift.Open(Guid.NewGuid(), Cashier.DeviceId!.Value, Day, Cashier.UserId, _clock.Now, Money.Gbp(10000));
        var ctx = new PaymentContext(shift.Id, Cashier.UserId, Cashier.DeviceId, _clock.Now);

        // Order 1: doner + can, cash £20, change £9.81
        var o1 = NewOrder(OrderSource.Counter, Doner, Can);
        var p1 = new OrderPayments(o1.Id, o1.Totals.Total, []).TakeCash(Money.Gbp(2000), ctx);
        o1.Complete(Cashier);

        // Order 2: doner by phone, card with £1 tip, then £2.00 refunded
        var o2 = NewOrder(OrderSource.Phone, Doner);
        var ledger2 = new OrderPayments(o2.Id, o2.Totals.Total, []);
        var p2 = ledger2.TakeCard(Money.Gbp(899), ctx, tip: Money.Gbp(100));
        o2.Complete(Cashier);
        var r2 = ledger2.Refund(p2.Id, Money.Gbp(200), "Late", Manager, ctx);

        // Order 3: cancelled; order 4: a can voided, still open
        var o3 = NewOrder(OrderSource.Counter, Can);
        o3.Cancel("Customer left", Cashier);
        var o4 = NewOrder(OrderSource.Counter, Doner, Can);
        o4.VoidLine(o4.Lines[1].Id, "Out of stock", Cashier);

        var closed = shift.Close(Cashier.UserId, _clock.Now.AddHours(5), Money.Gbp(11019));
        var payments = new[] { p1, p2, r2 };
        var report = DayReport.Build(Day, 1, _clock.Now.AddHours(6), Manager, [o1, o2, o3, o4], payments, [closed],
            new Dictionary<Guid, PricedOrder> { [o2.Id] = o2.Totals });

        Assert.Equal(2, report.Sales.Orders);
        Assert.Equal(Money.Gbp(1019 + 899), report.Sales.Total);
        Assert.Equal([OrderSource.Counter, OrderSource.Phone], report.BySource.Select(s => s.Source));

        var cash = report.Payments.Single(p => p.Method == PaymentMethod.Cash);
        var card = report.Payments.Single(p => p.Method == PaymentMethod.Card);
        Assert.Equal(Money.Gbp(1019), cash.Net);
        Assert.Equal(Money.Gbp(899), card.Taken);
        Assert.Equal(Money.Gbp(200), card.Refunded);
        Assert.Equal(Money.Gbp(100), card.Tips);

        // VAT: (10.19 + 8.99 - 2.00) = 17.18 gross at 20% -> 2.86 VAT
        var vat = Assert.Single(report.Vat);
        Assert.Equal(Money.Gbp(1718), vat.Gross);
        Assert.Equal(Money.Gbp(286), vat.Vat);
        Assert.Equal(report.Payments.Aggregate(Money.Gbp(0), (s, p) => s + p.Net), vat.Gross); // money in = VAT-able sales

        Assert.Equal(1, report.Exceptions.CancelledOrders);
        Assert.Equal(Money.Gbp(120), report.Exceptions.CancelledValue);
        Assert.Equal(1, report.Exceptions.VoidedItems);
        Assert.Equal(Money.Gbp(120), report.Exceptions.VoidedValue);
        Assert.Equal(1, report.Exceptions.Refunds);
        Assert.Equal(1, report.Exceptions.OpenOrders);

        Assert.Equal(Money.Gbp(0), report.CashVariance); // 100.00 float + 10.19 cash = 110.19 counted
        Assert.Equal(Money.Gbp(100), Assert.Single(report.TipsByStaff).Tips);
    }

    [Fact]
    public void Z_report_needs_every_shift_closed()
    {
        var shift = Shift.Open(Guid.NewGuid(), Guid.NewGuid(), Day, Cashier.UserId, _clock.Now, Money.Gbp(0));

        Assert.Throws<InvalidOperationException>(() => DayReport.Build(Day, 1, _clock.Now, Manager, [], [], [shift], new Dictionary<Guid, PricedOrder>()));
    }

    [Fact]
    public void Empty_day_gives_an_empty_report()
    {
        var report = DayReport.Build(Day, 7, _clock.Now, Manager, [], [], [], new Dictionary<Guid, PricedOrder>());

        Assert.Equal(7, report.ZNumber);
        Assert.Equal(0, report.Sales.Orders);
        Assert.Empty(report.Payments);
        Assert.Empty(report.Vat);
    }
}
