using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Pricing;
using Pos.Domain.Tax;
using Pos.Domain.Tests.Menus;

namespace Pos.Domain.Tests.Orders;

/// <summary>Deliveries going out with a driver and coming back.</summary>
public class DriverTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly OrderActor Cashier = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Ali = Guid.NewGuid();
    private static readonly Guid Ben = Guid.NewGuid();

    private readonly SampleMenu _s = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero));
    private readonly OrderIntake _intake;
    private readonly MenuOrderBuilder _builder;

    public DriverTests()
    {
        _builder = new MenuOrderBuilder(_s.Menu);
        _intake = new OrderIntake(new InMemoryOrderStore(), new InMemoryOrderNumberSequence(),
            new OrderPricingSetup(new UkVatPolicy(), [], London, DiscountPolicy.ManagerAlways()), new TimeOnly(5, 0), _clock);
    }

    private Order NewOrder(OrderType type, bool sent = true)
    {
        var selection = new ItemSelection(_s.Can, _s.CanRegular, []);
        var order = _intake.Submit(new NewOrder
        {
            OrderId = DeviceIds.New(_clock), IdempotencyKey = Guid.NewGuid().ToString(), BranchId = Guid.NewGuid(), BranchCode = "T",
            Source = OrderSource.Phone, Type = type, CreatedBy = Cashier,
            Lines = [new NewOrderLine(_builder.ToLine(selection), _builder.DisplayFor(selection))],
        }).Order;
        if (sent)
        {
            order.SendToKitchen(Cashier);
        }

        return order;
    }

    [Fact]
    public void A_delivery_goes_out_with_a_driver_and_is_recorded()
    {
        var order = NewOrder(OrderType.Delivery);
        order.MarkReady(Cashier);

        order.SendOut(Ali, "Ali", Cashier);

        Assert.Equal(OrderStatus.OutForDelivery, order.Status);
        Assert.Equal(Ali, order.DriverId);
        Assert.Equal("Out with driver Ali", order.History[^1].Description);
        Assert.Equal(OrderStatus.OutForDelivery, Order.Restore(order.ToState(), new OrderPricingSetup(new UkVatPolicy(), [], London, DiscountPolicy.ManagerAlways())).Status);
        Assert.Equal(Ali, Order.Restore(order.ToState(), new OrderPricingSetup(new UkVatPolicy(), [], London, DiscountPolicy.ManagerAlways())).DriverId);
    }

    [Fact]
    public void Small_shops_without_a_kitchen_screen_send_straight_from_accepted()
    {
        var order = NewOrder(OrderType.Delivery);

        order.SendOut(Ali, "Ali", Cashier);

        Assert.Equal(OrderStatus.OutForDelivery, order.Status);
    }

    [Fact]
    public void Handing_to_another_driver_keeps_it_out_and_is_recorded()
    {
        var order = NewOrder(OrderType.Delivery);
        order.SendOut(Ali, "Ali", Cashier);

        order.SendOut(Ben, "Ben", Cashier);
        order.SendOut(Ben, "Ben", Cashier); // tapped twice: nothing more

        Assert.Equal(Ben, order.DriverId);
        Assert.Equal("Handed to driver Ben", order.History[^1].Description);
        Assert.Equal(2, order.History.Count(h => h.Kind == OrderEventKind.DriverChanged));
    }

    [Fact]
    public void Back_to_shop_makes_it_ready_again_with_no_driver_and_needs_a_reason()
    {
        var order = NewOrder(OrderType.Delivery);
        order.SendOut(Ali, "Ali", Cashier);

        Assert.Throws<ArgumentException>(() => order.BackToShop(" ", Cashier));
        order.BackToShop("Nobody home", Cashier);

        Assert.Equal(OrderStatus.Ready, order.Status);
        Assert.Null(order.DriverId);
        Assert.Equal("Nobody home", order.History[^1].Reason);
    }

    [Fact]
    public void Delivered_completes_it_and_keeps_who_delivered()
    {
        var order = NewOrder(OrderType.Delivery);
        order.SendOut(Ali, "Ali", Cashier);

        order.Complete(Cashier);

        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.Equal(Ali, order.DriverId);
    }

    [Fact]
    public void Only_sent_delivery_orders_go_out_and_only_with_a_driver()
    {
        Assert.Throws<InvalidOperationException>(() => NewOrder(OrderType.Takeaway).SendOut(Ali, "Ali", Cashier));
        Assert.Contains("not sent to the kitchen", Assert.Throws<InvalidOperationException>(() => NewOrder(OrderType.Delivery, sent: false).SendOut(Ali, "Ali", Cashier)).Message, StringComparison.Ordinal);

        var order = NewOrder(OrderType.Delivery);
        Assert.Throws<InvalidOperationException>(() => order.MoveTo(OrderStatus.OutForDelivery, Cashier)); // no driver
        order.SendOut(Ali, "Ali", Cashier);
        Assert.Throws<InvalidOperationException>(() => order.MoveTo(OrderStatus.Ready, Cashier));         // use Back to shop
        Assert.Throws<InvalidOperationException>(() => NewOrder(OrderType.Delivery).BackToShop("Nobody home", Cashier)); // not out

        order.Complete(Cashier);
        Assert.Throws<InvalidOperationException>(() => order.SendOut(Ben, "Ben", Cashier)); // finished
    }
}
