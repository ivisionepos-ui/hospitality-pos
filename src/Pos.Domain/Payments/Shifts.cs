using Pos.Domain.Common;

namespace Pos.Domain.Payments;

/// <summary>Cash put into or taken out of a drawer outside a sale.</summary>
public enum CashMovementType
{
    /// <summary>Cash added, e.g. extra change from the safe.</summary>
    PaidIn,

    /// <summary>Cash taken out, e.g. paying a supplier or a drop to the safe.</summary>
    PaidOut,
}

/// <summary>A paid-in or paid-out.</summary>
/// <param name="Id">Id.</param>
/// <param name="Type">In or out.</param>
/// <param name="Amount">Amount (always positive).</param>
/// <param name="Reason">Why.</param>
/// <param name="UserId">Who.</param>
/// <param name="ApprovedByUserId">Manager who approved.</param>
/// <param name="At">When.</param>
public sealed record CashMovement(Guid Id, CashMovementType Type, Money Amount, string Reason, Guid UserId, Guid? ApprovedByUserId, DateTimeOffset At);

/// <summary>
/// A till shift: opened with a cash float, closed with a count of the drawer.
/// The cash expected in the drawer is the float, plus cash sales and paid-ins, less cash refunds and paid-outs.
/// </summary>
public sealed record Shift
{
    /// <summary>Id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The till.</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>Trading day the shift belongs to (the day it was opened).</summary>
    public required DateOnly BusinessDate { get; init; }

    /// <summary>Who opened it.</summary>
    public required Guid OpenedBy { get; init; }

    /// <summary>When it was opened.</summary>
    public required DateTimeOffset OpenedAt { get; init; }

    /// <summary>Cash in the drawer at the start.</summary>
    public required Money OpeningFloat { get; init; }

    /// <summary>Paid-ins and paid-outs.</summary>
    public IReadOnlyList<CashMovement> Movements { get; init; } = [];

    /// <summary>Who closed it.</summary>
    public Guid? ClosedBy { get; init; }

    /// <summary>When it was closed.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Cash counted in the drawer at close.</summary>
    public Money? CountedCash { get; init; }

    /// <summary>True until closed.</summary>
    public bool IsOpen => ClosedAt is null;

    /// <summary>Opens a shift.</summary>
    public static Shift Open(Guid id, Guid deviceId, DateOnly businessDate, Guid userId, DateTimeOffset at, Money openingFloat)
    {
        if (openingFloat.IsNegative)
        {
            throw new ArgumentException("The float cannot be negative.", nameof(openingFloat));
        }

        return new Shift { Id = id, DeviceId = deviceId, BusinessDate = businessDate, OpenedBy = userId, OpenedAt = at, OpeningFloat = openingFloat };
    }

    /// <summary>Adds a paid-in or paid-out. A reason is required; a paid-out cannot take the drawer below zero.</summary>
    public Shift Move(CashMovement movement, IEnumerable<Payment> payments)
    {
        ArgumentNullException.ThrowIfNull(movement);
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(movement.Reason))
        {
            throw new ArgumentException("A reason is required.", nameof(movement));
        }

        if (movement.Amount.MinorUnits <= 0 || movement.Amount.Currency != OpeningFloat.Currency)
        {
            throw new ArgumentException($"Amount must be more than zero, in {OpeningFloat.Currency}.", nameof(movement));
        }

        if (movement.Type == CashMovementType.PaidOut && movement.Amount > Summarise(payments).ExpectedCash)
        {
            throw new InvalidOperationException("There is not that much cash in the drawer.");
        }

        return this with { Movements = [.. Movements, movement with { Reason = movement.Reason.Trim() }] };
    }

    /// <summary>Closes the shift with the cash counted in the drawer.</summary>
    public Shift Close(Guid userId, DateTimeOffset at, Money countedCash)
    {
        EnsureOpen();
        if (countedCash.IsNegative || countedCash.Currency != OpeningFloat.Currency)
        {
            throw new ArgumentException("Counted cash must be zero or more.", nameof(countedCash));
        }

        return this with { ClosedBy = userId, ClosedAt = at, CountedCash = countedCash };
    }

    /// <summary>Totals for the shift (an X report while open; the final figures once closed).</summary>
    public ShiftSummary Summarise(IEnumerable<Payment> payments)
    {
        ArgumentNullException.ThrowIfNull(payments);
        var currency = OpeningFloat.Currency;
        var mine = payments.Where(p => p.ShiftId == Id).ToList();
        Money Sum(IEnumerable<Money> values) => values.Aggregate(Money.Zero(currency), (a, b) => a + b);

        var cashSales = Sum(mine.Where(p => p.Method == PaymentMethod.Cash && !p.IsRefund).Select(p => p.Amount));
        var cashRefunds = Sum(mine.Where(p => p.Method == PaymentMethod.Cash && p.IsRefund).Select(p => Money.Zero(currency) - p.Amount));
        var paidIn = Sum(Movements.Where(m => m.Type == CashMovementType.PaidIn).Select(m => m.Amount));
        var paidOut = Sum(Movements.Where(m => m.Type == CashMovementType.PaidOut).Select(m => m.Amount));
        var expected = OpeningFloat + cashSales - cashRefunds + paidIn - paidOut;

        var byMethod = Enum.GetValues<PaymentMethod>()
            .Select(method => new MethodTotals(
                method,
                mine.Count(p => p.Method == method && !p.IsRefund),
                Sum(mine.Where(p => p.Method == method && !p.IsRefund).Select(p => p.Amount)),
                mine.Count(p => p.Method == method && p.IsRefund),
                Sum(mine.Where(p => p.Method == method && p.IsRefund).Select(p => Money.Zero(currency) - p.Amount)),
                Sum(mine.Where(p => p.Method == method).Select(p => p.Tip))))
            .Where(t => t.Payments + t.Refunds > 0)
            .ToList();

        return new ShiftSummary(Id, DeviceId, BusinessDate, OpenedAt, ClosedAt, OpeningFloat, cashSales, cashRefunds, paidIn, paidOut, expected,
            CountedCash, CountedCash is { } counted ? counted - expected : null, byMethod);
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("This shift is closed.");
        }
    }
}

/// <summary>Takings for one payment method.</summary>
/// <param name="Method">Method.</param>
/// <param name="Payments">Number of payments.</param>
/// <param name="Taken">Total taken.</param>
/// <param name="Refunds">Number of refunds.</param>
/// <param name="Refunded">Total refunded.</param>
/// <param name="Tips">Tips.</param>
public sealed record MethodTotals(PaymentMethod Method, int Payments, Money Taken, int Refunds, Money Refunded, Money Tips)
{
    /// <summary>Taken less refunded.</summary>
    public Money Net => Taken - Refunded;
}

/// <summary>A shift's figures.</summary>
public sealed record ShiftSummary(
    Guid ShiftId,
    Guid DeviceId,
    DateOnly BusinessDate,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    Money OpeningFloat,
    Money CashSales,
    Money CashRefunds,
    Money PaidIn,
    Money PaidOut,
    Money ExpectedCash,
    Money? CountedCash,
    Money? Variance,
    IReadOnlyList<MethodTotals> ByMethod);
