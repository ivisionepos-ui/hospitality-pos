using Pos.Domain.Common;
using Pos.Domain.Delivery;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Pricing;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;
using Pos.Domain.Tests.Menus;

namespace Pos.Domain.Tests.Orders;

public class OrderTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly OrderActor Cashier = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Manager = Guid.NewGuid();

    private readonly SampleMenu _s = new();
    private readonly MenuOrderBuilder _builder;
    private readonly InMemoryOrderStore _store = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero)); // Thursday
    private readonly OrderIntake _intake;

    public OrderTests()
    {
        _builder = new MenuOrderBuilder(_s.Menu);
        _intake = NewIntake([]);
    }

    private OrderIntake NewIntake(IReadOnlyList<Promotion> promotions) =>
        new(_store, new InMemoryOrderNumberSequence(),
            new OrderPricingSetup(new UkVatPolicy(), promotions, London, new DiscountPolicy(1000, Money.Gbp(500))),
            dayStartsAt: new TimeOnly(5, 0), clock: _clock);

    private NewOrderLine Item(ItemSelection selection, string? note = null) =>
        new(_builder.ToLine(selection), _builder.DisplayFor(selection), note);

    private NewOrderLine Can => Item(new ItemSelection(_s.Can, _s.CanRegular, []));

    private NewOrderLine Doner => Item(new ItemSelection(_s.Doner, _s.DonerRegular, [_s.Garlic]));

    private NewOrder Counter(params NewOrderLine[] lines) => new()
    {
        OrderId = DeviceIds.New(_clock),
        IdempotencyKey = Guid.NewGuid().ToString(),
        BranchId = Branch,
        BranchCode = "LDN1",
        Source = OrderSource.Counter,
        Type = OrderType.Takeaway,
        CreatedBy = Cashier,
        Lines = lines,
    };

    [Fact]
    public void Counter_order_is_numbered_accepted_priced_and_recorded()
    {
        var result = _intake.Submit(Counter(Doner, Can));
        var order = result.Order;

        Assert.False(result.WasDuplicate);
        Assert.Equal("001", order.Number.Display);
        Assert.Equal(new DateOnly(2026, 11, 5), order.Number.BusinessDate);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(Money.Gbp(899 + 120), order.Totals.Total);
        Assert.Equal([OrderEventKind.Created, OrderEventKind.LineAdded, OrderEventKind.LineAdded], order.History.Select(e => e.Kind));
        Assert.All(order.History, e => Assert.Equal(Cashier.DeviceId, e.DeviceId));
        Assert.Same(order, _store.FindById(order.Id));
    }

    [Fact]
    public void Order_numbers_count_up_and_restart_on_a_new_business_day()
    {
        Assert.Equal("001", _intake.Submit(Counter(Can)).Order.Number.Display);
        Assert.Equal("002", _intake.Submit(Counter(Can)).Order.Number.Display);

        _clock.Now = new DateTimeOffset(2026, 11, 6, 1, 30, 0, TimeSpan.Zero); // after midnight, before 05:00
        Assert.Equal("003", _intake.Submit(Counter(Can)).Order.Number.Display);

        _clock.Now = new DateTimeOffset(2026, 11, 6, 11, 0, 0, TimeSpan.Zero);
        var nextDay = _intake.Submit(Counter(Can)).Order;
        Assert.Equal("001", nextDay.Number.Display);
        Assert.Equal(new DateOnly(2026, 11, 6), nextDay.Number.BusinessDate);
    }

    [Fact]
    public void Same_order_sent_twice_is_stored_once()
    {
        var request = Counter(Doner, Can);

        var first = _intake.Submit(request);
        var retry = _intake.Submit(request);

        Assert.True(retry.WasDuplicate);
        Assert.Same(first.Order, retry.Order);
        Assert.Single(_store.All);
        Assert.Equal("002", _intake.Submit(Counter(Can)).Order.Number.Display); // no number was used up by the retry
    }

    [Fact]
    public void Reusing_a_key_for_a_different_order_is_refused()
    {
        var request = Counter(Doner);
        _intake.Submit(request);

        Assert.Throws<IdempotencyConflictException>(() => _intake.Submit(request with { Lines = [Doner, Can] }));
        Assert.Throws<IdempotencyConflictException>(() => _intake.Submit(request with { IdempotencyKey = "other-key" }));
        Assert.Single(_store.All);
    }

    [Fact]
    public void Online_order_must_be_accepted_before_the_kitchen_gets_it()
    {
        var order = _intake.Submit(Counter(Doner) with { Source = OrderSource.Website }).Order;

        Assert.Equal(OrderStatus.Received, order.Status);
        Assert.Throws<InvalidOperationException>(() => order.SendToKitchen(Cashier));

        order.Accept(Cashier);
        Assert.Single(order.SendToKitchen(Cashier));
    }

    [Fact]
    public void Cloud_can_create_an_order_as_pending()
    {
        var order = _intake.Submit(Counter(Doner) with { Source = OrderSource.Website, StartingStatus = OrderStatus.Pending }).Order;

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Throws<InvalidOperationException>(() => order.Accept(Cashier)); // the shop must receive it first
        order.MoveTo(OrderStatus.Received, Cashier);
        order.Accept(Cashier);
        Assert.Equal(OrderStatus.Accepted, order.Status);
    }

    [Fact]
    public void Full_status_flow_is_recorded()
    {
        var order = _intake.Submit(Counter(Doner)).Order;

        order.StartPreparing(Cashier);
        order.MarkReady(Cashier);
        order.Complete(Cashier);

        var moves = order.History.Where(e => e.Kind == OrderEventKind.StatusChanged).Select(e => (e.FromStatus, e.ToStatus));
        Assert.Equal(
            [(OrderStatus.Accepted, OrderStatus.Preparing), (OrderStatus.Preparing, OrderStatus.Ready), (OrderStatus.Ready, OrderStatus.Completed)],
            moves);
        Assert.True(order.IsFinished);
        Assert.Throws<InvalidOperationException>(() => order.AddLine(Can.Pricing, Can.Display, Cashier));
        Assert.Throws<InvalidOperationException>(() => order.MarkReady(Cashier));
    }

    [Fact]
    public void Rejecting_needs_a_reason()
    {
        var order = _intake.Submit(Counter(Doner) with { Source = OrderSource.JustEat }).Order;

        Assert.Throws<ArgumentException>(() => order.Reject(" ", Cashier));
        order.Reject("Too busy", Cashier);

        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal("Too busy", order.History[^1].Reason);
    }

    [Fact]
    public void Removing_an_item_before_the_kitchen_needs_only_a_reason()
    {
        var order = _intake.Submit(Counter(Doner, Can)).Order;
        var can = order.Lines[1];

        order.VoidLine(can.Id, "Customer changed mind", Cashier);

        Assert.True(order.Lines[1].IsVoided);
        Assert.Single(order.ActiveLines);
        Assert.Equal(Money.Gbp(899), order.Totals.Total);
        Assert.Equal("Customer changed mind", order.History[^1].Reason);
        Assert.Throws<InvalidOperationException>(() => order.VoidLine(can.Id, "Again", Cashier));
    }

    [Fact]
    public void Removing_an_item_already_in_the_kitchen_needs_a_manager()
    {
        var order = _intake.Submit(Counter(Doner, Can)).Order;
        order.SendToKitchen(Cashier);
        var doner = order.Lines[0];

        Assert.Throws<InvalidOperationException>(() => order.VoidLine(doner.Id, "Wrong item", Cashier));
        order.VoidLine(doner.Id, "Wrong item", Cashier, approvedByUserId: Manager);

        Assert.Equal(Manager, order.History[^1].ApprovedByUserId);
        Assert.Equal(Money.Gbp(120), order.Totals.Total);
    }

    [Fact]
    public void Only_new_lines_go_to_the_kitchen_the_second_time()
    {
        var order = _intake.Submit(Counter(Doner)).Order;
        Assert.Single(order.SendToKitchen(Cashier));

        order.AddLine(Can.Pricing, Can.Display, Cashier);
        var second = order.SendToKitchen(Cashier);

        Assert.Equal("Can 330ml", Assert.Single(second).Display.Name.En);
        Assert.Empty(order.SendToKitchen(Cashier));
    }

    [Fact]
    public void Cancelling_after_food_went_to_the_kitchen_needs_a_manager()
    {
        var order = _intake.Submit(Counter(Doner)).Order;
        order.SendToKitchen(Cashier);

        Assert.Throws<InvalidOperationException>(() => order.Cancel("Customer left", Cashier));
        order.Cancel("Customer left", Cashier, Manager);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void Discount_above_the_staff_limit_is_refused_and_changes_nothing()
    {
        var order = _intake.Submit(Counter(Doner, Can)).Order;
        var totalBefore = order.Totals.Total;

        Assert.Throws<DiscountApprovalRequiredException>(() =>
            order.ApplyDiscount(new OrderDiscountInput(OrderDiscountType.Percent, 2000, "Complaint", Cashier.UserId), Cashier));
        Assert.Null(order.Discount);
        Assert.Equal(totalBefore, order.Totals.Total);

        order.ApplyDiscount(new OrderDiscountInput(OrderDiscountType.Percent, 2000, "Complaint", Cashier.UserId, Manager), Cashier);
        Assert.Equal(Money.Gbp(1019 - 204), order.Totals.Total); // 20% of £10.19 = 203.8p -> 204p
        Assert.Equal(Manager, order.History[^1].ApprovedByUserId);

        order.RemoveDiscount(Cashier);
        Assert.Equal(totalBefore, order.Totals.Total);
    }

    [Fact]
    public void A_discount_given_within_the_limit_stays_when_the_order_grows_or_the_limits_change()
    {
        var order = _intake.Submit(Counter(Can)).Order;
        order.ApplyDiscount(new OrderDiscountInput(OrderDiscountType.Percent, 1000, "Regular", Cashier.UserId), Cashier); // 10%: within the limit

        // Ten more doners: 10% is now well over the £5 staff limit, but the discount was allowed when it was given.
        for (var i = 0; i < 10; i++)
        {
            order.AddLine(Doner.Pricing, Doner.Display, Cashier);
        }

        Assert.NotNull(order.Discount);

        // The owner then makes every discount need a manager: the saved order still loads and keeps its discount.
        var reloaded = Order.Restore(order.ToState(), new OrderPricingSetup(new UkVatPolicy(), [], London, DiscountPolicy.ManagerAlways()));
        Assert.Equal(order.Totals.Total, reloaded.Totals.Total);
        Assert.Equal(order.Totals.Discount!.AmountOff, reloaded.Totals.Discount!.AmountOff);
    }

    [Fact]
    public void The_limit_in_force_when_the_discount_is_given_is_the_one_checked()
    {
        var order = _intake.Submit(Counter(Can)).Order;

        Assert.Throws<DiscountApprovalRequiredException>(() =>
            order.ApplyDiscount(new OrderDiscountInput(OrderDiscountType.Percent, 500, "Regular", Cashier.UserId), Cashier, DiscountPolicy.ManagerAlways()));
        order.ApplyDiscount(new OrderDiscountInput(OrderDiscountType.Percent, 500, "Regular", Cashier.UserId), Cashier); // the order's own: 10%
    }

    [Fact]
    public void Delivery_and_service_charge_are_added_to_the_totals()
    {
        var order = _intake.Submit(Counter(Doner, Can) with { Source = OrderSource.Phone, Type = OrderType.Delivery }).Order;
        var bands = DeliveryChargeTable.Banded([new DeliveryBand(0m, 3m, Money.Gbp(250))], maximumMiles: 3m);

        order.SetDelivery(new DeliveryInput(1.2m, bands), Cashier);
        Assert.Equal(Money.Gbp(1019 + 250), order.Totals.Total);

        order.SetServiceCharge(new ServiceChargeInput(1000), Cashier);
        Assert.Equal(Money.Gbp(1019 + 250 + 102), order.Totals.Total); // 10% of £10.19 = 101.9p -> 102p

        order.SetServiceCharge(null, Cashier);
        Assert.Equal(Money.Gbp(1019 + 250), order.Totals.Total);
    }

    [Fact]
    public void Receipt_lists_starters_to_drinks_whatever_order_items_were_entered()
    {
        var order = _intake.Submit(Counter(Can, Doner)).Order; // drink entered first

        Assert.Equal(["Doner kebab", "Can 330ml"], order.LinesForReceipt.Select(l => l.Display.Name.En));
        Assert.Equal(["Can 330ml", "Doner kebab"], order.ActiveLines.Select(l => l.Display.Name.En));
    }

    [Fact]
    public void Menu_changes_after_ordering_do_not_change_the_order()
    {
        var order = _intake.Submit(Counter(Doner)).Order;

        _s.Menu.SetPrice(_s.Doner, _s.DonerRegular, Money.Gbp(999), SampleMenu.Owner);
        _s.Menu.RenameProduct(_s.Doner, new LocalizedText("Lamb doner"), SampleMenu.Owner);
        order.AddLine(Can.Pricing, Can.Display, Cashier); // forces a recalculation

        Assert.Equal(Money.Gbp(899 + 120), order.Totals.Total);
        Assert.Equal("Doner kebab", order.Lines[0].Display.Name.En);
    }

    [Fact]
    public void Our_offers_apply_to_our_orders_but_not_to_delivery_app_orders()
    {
        var cansHalfPrice = new Promotion(Guid.NewGuid(), "Cans half price", PromotionType.PercentOff, 5000,
            [new PromotionTarget(PromotionTargetKind.Product, _s.Can)]);
        var intake = NewIntake([cansHalfPrice]);

        var ours = intake.Submit(Counter(Can)).Order;
        var justEat = intake.Submit(Counter(Can) with { Source = OrderSource.JustEat }).Order;

        Assert.Equal(Money.Gbp(60), ours.Totals.Total);
        Assert.Equal(Money.Gbp(120), justEat.Totals.Total);
    }

    [Fact]
    public void Notes_are_kept_and_meal_deal_shows_its_choices_in_order()
    {
        var deal = new DealSelection(_s.Deal, _s.DealRegular,
        [
            new SlotChoice(_s.DrinkSlot, _s.Can, _s.CanRegular, []),
            new SlotChoice(_s.KebabSlot, _s.Doner, _s.DonerRegular, [_s.Chilli]),
            new SlotChoice(_s.PizzaSlot, _s.Margherita, _s.Margherita12, []),
        ]);
        var order = _intake.Submit(Counter(
            new NewOrderLine(_builder.ToLine(deal), _builder.DisplayFor(deal), "  Extra crispy  "))).Order;

        var line = order.Lines[0];
        Assert.Equal("Extra crispy", line.Note);
        Assert.Equal(["Margherita", "Doner kebab", "Can 330ml"], line.Display.DealItems.Select(i => i.Name.En));
        Assert.Equal("12\"", line.Display.DealItems[0].Size!.En);
        Assert.Equal("Chilli", Assert.Single(line.Display.DealItems[1].Extras).En);
        Assert.Equal(1, line.Display.CourseSortOrder); // prints with the mains
        Assert.Equal(Money.Gbp(1999), order.Totals.Total);
    }
}
