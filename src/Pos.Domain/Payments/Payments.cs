using Pos.Domain.Common;
using Pos.Domain.Pricing;

namespace Pos.Domain.Payments;

/// <summary>How the customer paid.</summary>
public enum PaymentMethod
{
    /// <summary>Cash into the till drawer.</summary>
    Cash,

    /// <summary>Card on the Worldpay terminal (amount keyed in by staff for now).</summary>
    Card,

    /// <summary>Paid to Just Eat, Uber Eats or Deliveroo, who pay the shop.</summary>
    DeliveryApp,

    /// <summary>Paid online on the shop's website.</summary>
    Online,
}

/// <summary>
/// A payment or a refund. Refunds have a negative amount and point at the payment they refund.
/// Records are never changed or deleted.
/// </summary>
public sealed record Payment
{
    /// <summary>Id (made on the device).</summary>
    public required Guid Id { get; init; }

    /// <summary>The order paid for.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The till shift it was taken on (whose drawer the cash went into or came out of).</summary>
    public required Guid ShiftId { get; init; }

    /// <summary>Cash, card, delivery app or online.</summary>
    public required PaymentMethod Method { get; init; }

    /// <summary>Amount towards the order: positive for a payment, negative for a refund.</summary>
    public required Money Amount { get; init; }

    /// <summary>Card tip on top of the amount (not part of the order total). Zero for cash and refunds.</summary>
    public required Money Tip { get; init; }

    /// <summary>Cash handed over by the customer, for cash payments.</summary>
    public Money? Tendered { get; init; }

    /// <summary>Change given back, for cash payments.</summary>
    public Money? Change { get; init; }

    /// <summary>For a refund: the payment being refunded.</summary>
    public Guid? RefundOf { get; init; }

    /// <summary>Why it was refunded.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// For a refund: how much of it is money back for what was sold (a complaint, a return), which lowers the sale and
    /// its VAT. The rest only gives back an overpayment (e.g. an item removed after paying), where the order total has
    /// already gone down. Null on refunds recorded before this was kept: then all of it.
    /// </summary>
    public Money? SaleReduction { get; init; }

    /// <summary>The part of a refund that lowers the sale (zero for a payment).</summary>
    public Money SaleReductionOrAll => !IsRefund ? Money.Zero(Amount.Currency) : SaleReduction ?? Money.Zero(Amount.Currency) - Amount;

    /// <summary>Card terminal receipt number or other reference.</summary>
    public string? Reference { get; init; }

    /// <summary>For a split bill by items: the order lines this payment covers.</summary>
    public IReadOnlyList<Guid> ForLineIds { get; init; } = [];

    /// <summary>Staff member who took it.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Till it was taken on.</summary>
    public Guid? DeviceId { get; init; }

    /// <summary>Manager who approved it (refunds).</summary>
    public Guid? ApprovedByUserId { get; init; }

    /// <summary>When (UTC).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>True for a refund.</summary>
    public bool IsRefund => RefundOf is not null;
}

/// <summary>Who is taking a payment, on which shift, and when.</summary>
/// <param name="ShiftId">Open shift on the till.</param>
/// <param name="UserId">Staff member.</param>
/// <param name="DeviceId">Till.</param>
/// <param name="At">When.</param>
/// <param name="PaymentId">Id made on the till; a new one is made if empty.</param>
public sealed record PaymentContext(Guid ShiftId, Guid UserId, Guid? DeviceId, DateTimeOffset At, Guid? PaymentId = null)
{
    /// <summary>The payment id to use.</summary>
    public Guid Id => PaymentId ?? Guid.CreateVersion7(At);
}

/// <summary>
/// The payments on one order, and the rules for taking more:
/// never more than the balance due, cash change worked out exactly, refunds never more than was paid,
/// always with a reason and a manager.
/// </summary>
public sealed class OrderPayments
{
    private readonly List<Payment> _payments;

    /// <summary>Creates the ledger for an order total and the payments already taken.</summary>
    public OrderPayments(Guid orderId, Money total, IEnumerable<Payment> payments)
    {
        ArgumentNullException.ThrowIfNull(payments);
        OrderId = orderId;
        Total = total;
        _payments = [.. payments.OrderBy(p => p.At)];
        if (_payments.Any(p => p.OrderId != orderId))
        {
            throw new ArgumentException("A payment belongs to another order.", nameof(payments));
        }
    }

    /// <summary>The order.</summary>
    public Guid OrderId { get; }

    /// <summary>Amount the order comes to now.</summary>
    public Money Total { get; }

    /// <summary>Payments and refunds, oldest first.</summary>
    public IReadOnlyList<Payment> Payments => _payments;

    /// <summary>Paid so far, after refunds.</summary>
    public Money Paid => _payments.Aggregate(Money.Zero(Total.Currency), (sum, p) => sum + p.Amount);

    /// <summary>Money given back for what was sold (complaints, returns): the customer no longer owes it.</summary>
    public Money RefundedForSale => _payments.Aggregate(Money.Zero(Total.Currency), (sum, p) => sum + p.SaleReductionOrAll);

    /// <summary>What the customer owes in all: the order total less money given back for what was sold.</summary>
    public Money Due => Total - RefundedForSale;

    /// <summary>
    /// Still to pay. Negative when more was paid than the order now comes to (a refund is due).
    /// A refund for a complaint does not make the order owe that money again.
    /// </summary>
    public Money Balance => Due - Paid;

    /// <summary>Card tips taken.</summary>
    public Money Tips => _payments.Aggregate(Money.Zero(Total.Currency), (sum, p) => sum + p.Tip);

    /// <summary>True when nothing is left to pay and nothing is owed back.</summary>
    public bool IsSettled => Balance.MinorUnits == 0;

    /// <summary>
    /// Takes cash. With no amount given, the cash covers as much of the balance as it can and the rest is change.
    /// With an amount (e.g. one person's share of a split bill), the cash must cover that amount.
    /// </summary>
    public Payment TakeCash(Money tendered, PaymentContext context, Money? amount = null, IReadOnlyList<Guid>? forLineIds = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequirePositive(tendered, "Cash handed over");
        if (Balance.MinorUnits <= 0)
        {
            throw new InvalidOperationException("This order is already paid.");
        }

        var applied = amount ?? Money.Min(tendered, Balance);
        CheckAmount(applied);
        if (tendered < applied)
        {
            throw new ArgumentException($"Cash handed over ({tendered.Display()}) is less than the amount to pay ({applied.Display()}).");
        }

        return Add(new Payment
        {
            Id = context.Id,
            OrderId = OrderId,
            ShiftId = context.ShiftId,
            Method = PaymentMethod.Cash,
            Amount = applied,
            Tip = Money.Zero(Total.Currency),
            Tendered = tendered,
            Change = tendered - applied,
            ForLineIds = forLineIds ?? [],
            UserId = context.UserId,
            DeviceId = context.DeviceId,
            At = context.At,
        });
    }

    /// <summary>Records a card payment taken on the terminal, with an optional tip and the terminal's receipt number.</summary>
    public Payment TakeCard(Money amount, PaymentContext context, Money? tip = null, string? reference = null, IReadOnlyList<Guid>? forLineIds = null) =>
        TakeOther(PaymentMethod.Card, amount, context, tip, reference, forLineIds);

    /// <summary>Records a payment made elsewhere (delivery app, online) or by card.</summary>
    public Payment TakeOther(PaymentMethod method, Money amount, PaymentContext context, Money? tip = null, string? reference = null, IReadOnlyList<Guid>? forLineIds = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (method == PaymentMethod.Cash)
        {
            throw new ArgumentException("Use TakeCash for cash.", nameof(method));
        }

        CheckAmount(amount);
        var tipAmount = tip ?? Money.Zero(Total.Currency);
        if (tipAmount.IsNegative)
        {
            throw new ArgumentException("A tip cannot be negative.", nameof(tip));
        }

        if (tipAmount.MinorUnits > 0 && method != PaymentMethod.Card)
        {
            throw new ArgumentException("Tips can only be added to card payments.", nameof(tip));
        }

        return Add(new Payment
        {
            Id = context.Id,
            OrderId = OrderId,
            ShiftId = context.ShiftId,
            Method = method,
            Amount = amount,
            Tip = tipAmount,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            ForLineIds = forLineIds ?? [],
            UserId = context.UserId,
            DeviceId = context.DeviceId,
            At = context.At,
        });
    }

    /// <summary>How much of a payment can still be refunded.</summary>
    public Money Refundable(Guid paymentId)
    {
        var original = _payments.FirstOrDefault(p => p.Id == paymentId && !p.IsRefund)
            ?? throw new KeyNotFoundException("Payment not found on this order.");
        var refunded = _payments.Where(p => p.RefundOf == paymentId).Aggregate(Money.Zero(Total.Currency), (sum, p) => sum + p.Amount);
        return original.Amount + refunded;
    }

    /// <summary>
    /// Refunds all or part of a payment, the same way it was paid (cash from the drawer, card on the terminal).
    /// A reason and a manager are always required. Tips are not refunded here.
    /// </summary>
    public Payment Refund(Guid paymentId, Money amount, string reason, Guid approvedByUserId, PaymentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required for a refund.", nameof(reason));
        }

        if (approvedByUserId == Guid.Empty)
        {
            throw new ArgumentException("A refund needs a manager's approval.", nameof(approvedByUserId));
        }

        RequirePositive(amount, "Refund");
        var refundable = Refundable(paymentId);
        if (amount > refundable)
        {
            throw new InvalidOperationException($"Only {refundable.Display()} of this payment can be refunded.");
        }

        // The first part of a refund gives back any overpayment; only the rest lowers the sale.
        var zero = Money.Zero(Total.Currency);
        var overpaid = Money.Max(zero, zero - Balance);
        var saleReduction = amount - Money.Min(amount, overpaid);
        var original = _payments.Single(p => p.Id == paymentId);
        return Add(new Payment
        {
            SaleReduction = saleReduction,
            Id = context.Id,
            OrderId = OrderId,
            ShiftId = context.ShiftId,
            Method = original.Method,
            Amount = Money.Zero(Total.Currency) - amount,
            Tip = Money.Zero(Total.Currency),
            RefundOf = paymentId,
            Reason = reason.Trim(),
            UserId = context.UserId,
            DeviceId = context.DeviceId,
            ApprovedByUserId = approvedByUserId,
            At = context.At,
        });
    }

    private Payment Add(Payment payment)
    {
        if (_payments.Any(p => p.Id == payment.Id))
        {
            throw new InvalidOperationException("A payment with this id has already been taken.");
        }

        _payments.Add(payment);
        return payment;
    }

    private void CheckAmount(Money amount)
    {
        RequirePositive(amount, "Amount");
        if (Balance.MinorUnits <= 0)
        {
            throw new InvalidOperationException("This order is already paid.");
        }

        if (amount > Balance)
        {
            throw new InvalidOperationException($"The amount ({amount.Display()}) is more than is left to pay ({Balance.Display()}).");
        }
    }

    private void RequirePositive(Money amount, string what)
    {
        if (amount.Currency != Total.Currency)
        {
            throw new ArgumentException($"{what} must be in {Total.Currency}.");
        }

        if (amount.MinorUnits <= 0)
        {
            throw new ArgumentException($"{what} must be more than zero.");
        }
    }
}

/// <summary>Working out shares when a bill is split.</summary>
public static class BillSplitter
{
    /// <summary>Splits an amount into equal parts to the penny (the first parts take any extra penny).</summary>
    public static IReadOnlyList<Money> Equally(Money amount, int parts)
    {
        if (parts is < 2 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(parts), "Split between 2 and 50 people.");
        }

        return ProportionalAllocator.Allocate(amount, [.. Enumerable.Repeat(new Money(1, amount.Currency), parts)]);
    }

    /// <summary>
    /// The amount for chosen lines of a priced order: each line after its share of the order discount,
    /// plus its fair share of the delivery fee and service charge. Every line's share added together
    /// comes to the order total exactly.
    /// </summary>
    public static Money ForLines(PricedOrder order, IEnumerable<int> lineIndexes)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(lineIndexes);
        var currency = order.Total.Currency;
        if (order.Lines.Count == 0)
        {
            return Money.Zero(currency);
        }

        var net = order.Lines.Select(l => l.LineTotal - l.DiscountShare).ToList();
        var goods = net.Aggregate(Money.Zero(currency), (sum, m) => sum + m);
        var extras = order.Total - goods;
        var extraShares = extras.IsNegative
            ? [.. net.Select(_ => Money.Zero(currency))]
            : ProportionalAllocator.Allocate(extras, net);

        var chosen = lineIndexes.Distinct().ToList();
        if (chosen.Any(i => i < 0 || i >= net.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(lineIndexes), "A chosen line is not on the order.");
        }

        return chosen.Aggregate(Money.Zero(currency), (sum, i) => sum + net[i] + extraShares[i]);
    }
}
