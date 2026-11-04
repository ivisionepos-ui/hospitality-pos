using Pos.Domain.Orders;

namespace Pos.Domain.Tests.Orders;

public class OrderBasicsTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Received, true)]
    [InlineData(OrderStatus.Pending, OrderStatus.Accepted, false)]  // must reach the shop first
    [InlineData(OrderStatus.Received, OrderStatus.Accepted, true)]
    [InlineData(OrderStatus.Received, OrderStatus.Rejected, true)]
    [InlineData(OrderStatus.Accepted, OrderStatus.Rejected, false)] // once accepted it can only be cancelled
    [InlineData(OrderStatus.Accepted, OrderStatus.Preparing, true)]
    [InlineData(OrderStatus.Accepted, OrderStatus.Completed, true)] // counter sale paid and handed over
    [InlineData(OrderStatus.Preparing, OrderStatus.Ready, true)]
    [InlineData(OrderStatus.Ready, OrderStatus.Preparing, true)]   // the kitchen recalls an order marked ready by mistake
    [InlineData(OrderStatus.Completed, OrderStatus.Ready, false)]
    [InlineData(OrderStatus.Ready, OrderStatus.Completed, true)]
    [InlineData(OrderStatus.Completed, OrderStatus.Cancelled, false)] // refunds handle completed orders
    [InlineData(OrderStatus.Cancelled, OrderStatus.Accepted, false)]
    public void Status_moves_follow_the_rules(OrderStatus from, OrderStatus to, bool allowed)
    {
        Assert.Equal(allowed, OrderStatusRules.CanMove(from, to));
    }

    [Fact]
    public void Finished_statuses()
    {
        Assert.True(OrderStatusRules.IsFinished(OrderStatus.Completed));
        Assert.True(OrderStatusRules.IsFinished(OrderStatus.Cancelled));
        Assert.True(OrderStatusRules.IsFinished(OrderStatus.Rejected));
        Assert.False(OrderStatusRules.IsFinished(OrderStatus.Ready));
    }

    [Theory]
    [InlineData(OrderSource.Counter, OrderStatus.Accepted)]
    [InlineData(OrderSource.Table, OrderStatus.Accepted)]
    [InlineData(OrderSource.Phone, OrderStatus.Accepted)]
    [InlineData(OrderSource.Website, OrderStatus.Received)]
    [InlineData(OrderSource.JustEat, OrderStatus.Received)]
    [InlineData(OrderSource.Deliveroo, OrderStatus.Received)]
    public void Staff_orders_start_accepted_and_online_orders_wait(OrderSource source, OrderStatus expected)
    {
        Assert.Equal(expected, OrderStatusRules.StartingStatusAtShop(source));
    }

    [Fact]
    public void Order_number_formats()
    {
        var number = new OrderNumber("LDN1", new DateOnly(2026, 11, 3), 45);

        Assert.Equal("045", number.Display);
        Assert.Equal("LDN1-261103-0045", number.Reference);
    }

    [Fact]
    public void Numbers_restart_each_day_and_are_separate_per_branch()
    {
        var seq = new InMemoryOrderNumberSequence();
        var branchA = Guid.NewGuid();
        var branchB = Guid.NewGuid();
        var monday = new DateOnly(2026, 11, 2);
        var tuesday = new DateOnly(2026, 11, 3);

        Assert.Equal(1, seq.TakeNext(branchA, monday));
        Assert.Equal(2, seq.TakeNext(branchA, monday));
        Assert.Equal(1, seq.TakeNext(branchB, monday));
        Assert.Equal(1, seq.TakeNext(branchA, tuesday));
    }

    [Theory]
    [InlineData(2026, 11, 4, 1, 30, 2026, 11, 3)]  // 01:30 counts towards the night before
    [InlineData(2026, 11, 4, 4, 59, 2026, 11, 3)]
    [InlineData(2026, 11, 4, 5, 0, 2026, 11, 4)]   // new day from 05:00
    [InlineData(2026, 11, 4, 18, 0, 2026, 11, 4)]
    public void Business_day_starts_at_the_set_time(int y, int m, int d, int h, int min, int ey, int em, int ed)
    {
        var at = new DateTimeOffset(y, m, d, h, min, 0, TimeSpan.Zero); // November: UK time = UTC

        Assert.Equal(new DateOnly(ey, em, ed), BusinessDay.For(at, London, new TimeOnly(5, 0)));
    }

    [Fact]
    public void Business_day_uses_uk_summer_time()
    {
        // 2 Jun 2026 23:30 UTC is 00:30 on 3 June in London; with a 05:00 day start it is still 2 June's trading.
        var at = new DateTimeOffset(2026, 6, 2, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 6, 2), BusinessDay.For(at, London, new TimeOnly(5, 0)));
        Assert.Equal(new DateOnly(2026, 6, 3), BusinessDay.For(at, London, new TimeOnly(0, 0)));
    }

    [Fact]
    public void Device_ids_are_version_7_and_sort_by_time()
    {
        var early = DeviceIds.New(new Menus.FixedClock(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero)));
        var late = DeviceIds.New(new Menus.FixedClock(new DateTimeOffset(2026, 11, 3, 10, 0, 1, TimeSpan.Zero)));

        Assert.Equal(7, early.Version);
        Assert.True(string.CompareOrdinal(early.ToString(), late.ToString()) < 0);
    }
}
