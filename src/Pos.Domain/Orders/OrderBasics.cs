using System.Globalization;

namespace Pos.Domain.Orders;

/// <summary>Where an order came from.</summary>
public enum OrderSource
{
    /// <summary>Taken at the counter.</summary>
    Counter,

    /// <summary>Taken at a table.</summary>
    Table,

    /// <summary>Taken by phone.</summary>
    Phone,

    /// <summary>The restaurant's own website.</summary>
    Website,

    /// <summary>The central multi-restaurant site.</summary>
    Marketplace,

    /// <summary>Just Eat.</summary>
    JustEat,

    /// <summary>Uber Eats.</summary>
    UberEats,

    /// <summary>Deliveroo.</summary>
    Deliveroo,
}

/// <summary>Where an order is in its life.</summary>
public enum OrderStatus
{
    /// <summary>Submitted online; not yet received by the shop. Never shown to the customer as confirmed.</summary>
    Pending,

    /// <summary>Received by the shop's hub; waiting for staff to accept.</summary>
    Received,

    /// <summary>Accepted by staff (orders taken by staff start here).</summary>
    Accepted,

    /// <summary>Being prepared in the kitchen.</summary>
    Preparing,

    /// <summary>Ready for collection, delivery or serving.</summary>
    Ready,

    /// <summary>Handed over and finished.</summary>
    Completed,

    /// <summary>Cancelled after being accepted, or by the customer.</summary>
    Cancelled,

    /// <summary>Refused by the shop (e.g. too busy, item unavailable).</summary>
    Rejected,
}

/// <summary>The allowed moves between statuses.</summary>
public static class OrderStatusRules
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Allowed = new()
    {
        [OrderStatus.Pending] = [OrderStatus.Received, OrderStatus.Rejected, OrderStatus.Cancelled],
        [OrderStatus.Received] = [OrderStatus.Accepted, OrderStatus.Rejected, OrderStatus.Cancelled],
        [OrderStatus.Accepted] = [OrderStatus.Preparing, OrderStatus.Ready, OrderStatus.Completed, OrderStatus.Cancelled],
        [OrderStatus.Preparing] = [OrderStatus.Ready, OrderStatus.Cancelled],
        [OrderStatus.Ready] = [OrderStatus.Completed, OrderStatus.Cancelled],
        [OrderStatus.Completed] = [],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Rejected] = [],
    };

    /// <summary>True when an order may move from one status to another.</summary>
    public static bool CanMove(OrderStatus from, OrderStatus to) => Allowed[from].Contains(to);

    /// <summary>True for Completed, Cancelled and Rejected: nothing more can happen to the order.</summary>
    public static bool IsFinished(OrderStatus status) => Allowed[status].Length == 0;

    /// <summary>
    /// Starting status when the order reaches the shop's hub:
    /// orders taken by staff are already accepted; online and delivery-app orders wait to be accepted.
    /// </summary>
    public static OrderStatus StartingStatusAtShop(OrderSource source) => source switch
    {
        OrderSource.Counter or OrderSource.Table or OrderSource.Phone => OrderStatus.Accepted,
        _ => OrderStatus.Received,
    };
}

/// <summary>
/// An order number staff and customers can read, restarting each business day per branch, e.g. "045".
/// The full reference adds branch and date, e.g. "LDN1-261103-0045", and is unique.
/// </summary>
/// <param name="BranchCode">Short branch code.</param>
/// <param name="BusinessDate">Trading day.</param>
/// <param name="Sequence">Number within the day, from 1.</param>
public sealed record OrderNumber(string BranchCode, DateOnly BusinessDate, int Sequence)
{
    /// <summary>Short number for screens, tickets and calling out: "045".</summary>
    public string Display => Sequence.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>Unique reference: "LDN1-261103-0045".</summary>
    public string Reference => string.Create(CultureInfo.InvariantCulture, $"{BranchCode}-{BusinessDate:yyMMdd}-{Sequence:0000}");

    /// <inheritdoc />
    public override string ToString() => Reference;
}

/// <summary>Gives out order numbers. The Store Hub keeps the counter in its database.</summary>
public interface IOrderNumberSequence
{
    /// <summary>Returns the next number for a branch and trading day, starting at 1.</summary>
    int TakeNext(Guid branchId, DateOnly businessDate);
}

/// <summary>Simple in-memory number sequence (tests and single-process use).</summary>
public sealed class InMemoryOrderNumberSequence : IOrderNumberSequence
{
    private readonly Dictionary<(Guid, DateOnly), int> _last = [];
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public int TakeNext(Guid branchId, DateOnly businessDate)
    {
        lock (_lock)
        {
            var next = _last.GetValueOrDefault((branchId, businessDate)) + 1;
            _last[(branchId, businessDate)] = next;
            return next;
        }
    }
}

/// <summary>
/// The trading day an order belongs to. A shop open past midnight sets the day to start later
/// (e.g. 05:00), so a 01:30 order counts towards the evening before.
/// </summary>
public static class BusinessDay
{
    /// <summary>Returns the trading day for a moment, in the branch's time zone.</summary>
    public static DateOnly For(DateTimeOffset at, TimeZoneInfo branchTimeZone, TimeOnly dayStartsAt)
    {
        ArgumentNullException.ThrowIfNull(branchTimeZone);
        var local = TimeZoneInfo.ConvertTime(at, branchTimeZone).DateTime;
        var date = DateOnly.FromDateTime(local);
        return TimeOnly.FromDateTime(local) < dayStartsAt ? date.AddDays(-1) : date;
    }
}

/// <summary>Creates ids on the device (UUID version 7): unique without asking a server, and sortable by time.</summary>
public static class DeviceIds
{
    /// <summary>A new time-ordered id.</summary>
    public static Guid New(TimeProvider? clock = null) => Guid.CreateVersion7((clock ?? TimeProvider.System).GetUtcNow());
}
