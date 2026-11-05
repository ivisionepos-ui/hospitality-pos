using System.Globalization;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Pricing;
using Pos.Hub.Api;
using Pos.Hub.Data;
using Pos.Hub.Live;

namespace Pos.Hub;

/// <summary>A payment request outcome.</summary>
/// <param name="Payment">The payment (the existing one when the till sent it again).</param>
/// <param name="Ledger">The order's payments after it.</param>
/// <param name="WasDuplicate">True when the till re-sent a payment already taken.</param>
public sealed record PaymentResult(Payment Payment, OrderPayments Ledger, bool WasDuplicate);

/// <summary>
/// Till shifts, payments and end-of-day reports, all saved straight away.
/// Every payment belongs to the open shift of the till it was taken on.
/// </summary>
public sealed class TillService(HubDatabase db, HubSettings settings, OrderService orders, TimeProvider clock, LiveUpdates live)
{
    private const string Currency = "GBP";

    // ---------- Shifts ----------

    /// <summary>The open shift on a till, or null.</summary>
    public Shift? CurrentShift(Guid deviceId) => db.Run(() =>
        db.Scalar("SELECT state_json FROM shifts WHERE device_id = $d AND is_open = 1;", ("$d", deviceId.ToString())) is string json
            ? PosJson.Deserialize<Shift>(json)
            : null);

    /// <summary>Opens a shift on a till with a cash float.</summary>
    public Shift OpenShift(Guid deviceId, Guid userId, Money openingFloat) => db.RunInTransaction(() =>
    {
        if (CurrentShift(deviceId) is { } open)
        {
            throw new InvalidOperationException(open.BusinessDate < Today()
                ? $"The shift from {open.BusinessDate:d MMM} is still open on this till. Close it first."
                : "A shift is already open on this till.");
        }

        var date = Today();
        if (ZReportExists(date))
        {
            throw new InvalidOperationException("Today's Z report has already been taken; no more shifts can be opened today.");
        }

        var shift = Shift.Open(Guid.CreateVersion7(clock.GetUtcNow()), deviceId, date, userId, clock.GetUtcNow(), openingFloat);
        db.Execute(
            "INSERT INTO shifts (id, device_id, business_date, is_open, opened_at, state_json) VALUES ($id, $d, $date, 1, $at, $json);",
            ("$id", shift.Id.ToString()), ("$d", deviceId.ToString()), ("$date", Iso(date)), ("$at", Iso(shift.OpenedAt)), ("$json", PosJson.Serialize(shift)));
        live.ShiftChanged($"Shift opened, float {openingFloat}");
        return shift;
    });

    /// <summary>Records a paid-in or paid-out on the till's open shift.</summary>
    public Shift MoveCash(Guid deviceId, CashMovementType type, Money amount, string reason, Guid userId, Guid approvedBy) => db.RunInTransaction(() =>
    {
        var shift = RequireOpenShift(deviceId);
        var movement = new CashMovement(Guid.CreateVersion7(clock.GetUtcNow()), type, amount, reason, userId, approvedBy, clock.GetUtcNow());
        var updated = shift.Move(movement, PaymentsForShift(shift.Id));
        SaveShift(updated);
        live.ShiftChanged($"{type} {amount}: {reason}");
        return updated;
    });

    /// <summary>Closes the till's shift with the counted cash, and returns its figures including over/short.</summary>
    public ShiftSummary CloseShift(Guid deviceId, Guid userId, Money countedCash) => db.RunInTransaction(() =>
    {
        var shift = RequireOpenShift(deviceId).Close(userId, clock.GetUtcNow(), countedCash);
        SaveShift(shift);
        var summary = shift.Summarise(PaymentsForShift(shift.Id));
        live.ShiftChanged($"Shift closed, counted {countedCash}");
        return summary;
    });

    /// <summary>Figures so far for the till's open shift (X report).</summary>
    public ShiftSummary XReport(Guid deviceId) => db.Run(() =>
    {
        var shift = RequireOpenShift(deviceId);
        return shift.Summarise(PaymentsForShift(shift.Id));
    });

    // ---------- Payments ----------

    /// <summary>The order's payments and balance.</summary>
    public OrderPayments Ledger(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return db.Run(() => new OrderPayments(order.Id, order.Totals.Total, PaymentsForOrder(order.Id)));
    }

    /// <summary>Takes cash on an order. Safe to send again with the same payment id.</summary>
    public PaymentResult TakeCash(Guid orderId, Guid deviceId, Guid userId, Guid? paymentId, Money tendered, Money? amount, IReadOnlyList<Guid>? forLineIds) =>
        Take(orderId, deviceId, userId, paymentId, (ledger, ctx) => ledger.TakeCash(tendered, ctx, amount, forLineIds), forLineIds);

    /// <summary>Records a card payment taken on the terminal. Safe to send again with the same payment id.</summary>
    public PaymentResult TakeCard(Guid orderId, Guid deviceId, Guid userId, Guid? paymentId, Money amount, Money? tip, string? reference, IReadOnlyList<Guid>? forLineIds) =>
        Take(orderId, deviceId, userId, paymentId, (ledger, ctx) => ledger.TakeCard(amount, ctx, tip, reference, forLineIds), forLineIds);

    /// <summary>Refunds all or part of a payment, from this till's open shift.</summary>
    public PaymentResult Refund(Guid orderId, Guid paymentIdToRefund, Guid deviceId, Guid userId, Guid approvedBy, Money amount, string reason, Guid? refundId) =>
        Take(orderId, deviceId, userId, refundId, (ledger, ctx) => ledger.Refund(paymentIdToRefund, amount, reason, approvedBy, ctx), null, allowFinished: true);

    /// <summary>Amount for chosen lines of an order (split by items).</summary>
    public Money AmountForLines(Order order, IReadOnlyList<Guid> lineIds)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(lineIds);
        var active = order.ActiveLines.Select(l => l.Id).ToList();
        var indexes = lineIds.Select(id => active.IndexOf(id)).ToList();
        if (indexes.Any(i => i < 0))
        {
            throw new KeyNotFoundException("A chosen item is not on this order (or was removed).");
        }

        return BillSplitter.ForLines(order.Totals, indexes);
    }

    // ---------- End of day ----------

    /// <summary>
    /// Takes the Z report for a trading day (default: today). Every shift of that day must be closed.
    /// Taking it again returns the same report.
    /// </summary>
    public DayReport ZReport(DateOnly? businessDate, Guid userId) => db.RunInTransaction(() =>
    {
        var date = businessDate ?? Today();
        if (db.Scalar("SELECT report_json FROM z_reports WHERE business_date = $d;", ("$d", Iso(date))) is string existing)
        {
            return PosJson.Deserialize<DayReport>(existing);
        }

        var shifts = ShiftsForDay(date);
        var payments = shifts.SelectMany(s => PaymentsForShift(s.Id)).ToList();
        var dayOrders = orders.ForDay(date, null);
        // Cancelled and rejected orders were never sales, so refunding them takes nothing off the day's sales or VAT.
        var refunded = payments.Where(p => p.IsRefund).Select(p => orders.Get(p.OrderId)).DistinctBy(o => o.Id)
            .Where(o => o.Status is not (OrderStatus.Cancelled or OrderStatus.Rejected))
            .ToDictionary(o => o.Id, o => o.Totals);
        var number = Convert.ToInt32(db.Scalar("SELECT COALESCE(MAX(z_number), 0) + 1 FROM z_reports;"), CultureInfo.InvariantCulture);

        var report = DayReport.Build(date, number, clock.GetUtcNow(), userId, dayOrders, payments, shifts, refunded, Currency);
        db.Execute(
            "INSERT INTO z_reports (business_date, z_number, created_at, report_json) VALUES ($d, $n, $at, $json);",
            ("$d", Iso(date)), ("$n", number), ("$at", Iso(report.CreatedAt)), ("$json", PosJson.Serialize(report)));
        return report;
    });

    /// <summary>A Z report already taken, or null.</summary>
    public DayReport? FindZReport(DateOnly businessDate) => db.Run(() =>
        db.Scalar("SELECT report_json FROM z_reports WHERE business_date = $d;", ("$d", Iso(businessDate))) is string json
            ? PosJson.Deserialize<DayReport>(json)
            : null);

    // ---------- Internals ----------

    private PaymentResult Take(
        Guid orderId, Guid deviceId, Guid userId, Guid? paymentId, Func<OrderPayments, PaymentContext, Payment> take, IReadOnlyList<Guid>? forLineIds, bool allowFinished = false) =>
        db.RunInTransaction(() =>
        {
            var order = orders.Get(orderId);
            if (paymentId is { } id && db.Scalar("SELECT state_json FROM payments WHERE id = $id;", ("$id", id.ToString())) is string json)
            {
                var previous = PosJson.Deserialize<Payment>(json);
                if (previous.OrderId != orderId)
                {
                    throw new IdempotencyConflictException("This payment id was already used on another order.");
                }

                return new PaymentResult(previous, Ledger(order), WasDuplicate: true);
            }

            if (!allowFinished && order.IsFinished)
            {
                throw new InvalidOperationException($"This order is {order.Status} and cannot take payments.");
            }

            if (forLineIds is { Count: > 0 } && forLineIds.Except(order.ActiveLines.Select(l => l.Id)).Any())
            {
                throw new KeyNotFoundException("A chosen item is not on this order (or was removed).");
            }

            var shift = RequireOpenShift(deviceId);
            var ledger = Ledger(order);
            var payment = take(ledger, new PaymentContext(shift.Id, userId, deviceId, clock.GetUtcNow(), paymentId));
            db.Execute(
                "INSERT INTO payments (id, order_id, shift_id, business_date, method, amount, at, state_json) VALUES ($id, $o, $s, $d, $m, $a, $at, $json);",
                ("$id", payment.Id.ToString()), ("$o", orderId.ToString()), ("$s", shift.Id.ToString()), ("$d", Iso(shift.BusinessDate)),
                ("$m", payment.Method.ToString()), ("$a", payment.Amount.MinorUnits), ("$at", Iso(payment.At)), ("$json", PosJson.Serialize(payment)));

            var what = payment.IsRefund ? $"Refund {Money.Zero(Currency) - payment.Amount} {payment.Method}" : $"Paid {payment.Amount} {payment.Method}";
            live.PaymentChanged(OrderView.From(order) with { Paid = ledger.Paid, Balance = ledger.Balance }, $"{order.Number.Display}: {what}");
            return new PaymentResult(payment, ledger, WasDuplicate: false);
        });

    private Shift RequireOpenShift(Guid deviceId) =>
        CurrentShift(deviceId) ?? throw new InvalidOperationException("Open a shift on this till first.");

    private void SaveShift(Shift shift) =>
        db.Execute(
            "UPDATE shifts SET is_open = $open, state_json = $json WHERE id = $id;",
            ("$open", shift.IsOpen ? 1 : 0), ("$json", PosJson.Serialize(shift)), ("$id", shift.Id.ToString()));

    private List<Shift> ShiftsForDay(DateOnly date) => ReadAll<Shift>("SELECT state_json FROM shifts WHERE business_date = $k ORDER BY opened_at;", Iso(date));

    private List<Payment> PaymentsForShift(Guid shiftId) => ReadAll<Payment>("SELECT state_json FROM payments WHERE shift_id = $k ORDER BY at;", shiftId.ToString());

    private List<Payment> PaymentsForOrder(Guid orderId) => ReadAll<Payment>("SELECT state_json FROM payments WHERE order_id = $k ORDER BY at;", orderId.ToString());

    private List<T> ReadAll<T>(string sql, string key) => db.Run(() =>
    {
        using var command = db.Command(sql, ("$k", key));
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            result.Add(PosJson.Deserialize<T>(reader.GetString(0)));
        }

        return result;
    });

    private bool ZReportExists(DateOnly date) =>
        db.Scalar("SELECT 1 FROM z_reports WHERE business_date = $d;", ("$d", Iso(date))) is not null;

    private DateOnly Today() => BusinessDay.For(clock.GetUtcNow(), settings.TimeZone, settings.DayStartsAt);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
