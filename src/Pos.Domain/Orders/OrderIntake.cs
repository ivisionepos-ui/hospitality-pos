using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pos.Domain.Pricing;
using Pos.Domain.Tax;

namespace Pos.Domain.Orders;

/// <summary>A line in a new order submission.</summary>
/// <param name="Pricing">The priced line (from the menu builder, or the delivery app's prices).</param>
/// <param name="Display">Names and print details.</param>
/// <param name="Note">Special instructions.</param>
public sealed record NewOrderLine(OrderLineInput Pricing, LineDisplay Display, string? Note = null);

/// <summary>An order being submitted from a till, kitchen-facing website, phone screen or delivery app.</summary>
public sealed record NewOrder
{
    /// <summary>Id made on the sending device.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>
    /// Key the sending device uses for this order and reuses on every retry,
    /// so a lost connection followed by a resend never creates a second order.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Branch receiving the order.</summary>
    public required Guid BranchId { get; init; }

    /// <summary>Short branch code for order references.</summary>
    public required string BranchCode { get; init; }

    /// <summary>Where it came from.</summary>
    public required OrderSource Source { get; init; }

    /// <summary>Eat-in, takeaway or delivery.</summary>
    public required OrderType Type { get; init; }

    /// <summary>Who created it, on which device.</summary>
    public required OrderActor CreatedBy { get; init; }

    /// <summary>Items.</summary>
    public IReadOnlyList<NewOrderLine> Lines { get; init; } = [];

    /// <summary>Customer, if known.</summary>
    public Guid? CustomerId { get; init; }

    /// <summary>Customer name, phone, address and notes (phone and delivery orders).</summary>
    public OrderContact? Contact { get; init; }

    /// <summary>Table, for table service.</summary>
    public Guid? TableId { get; init; }

    /// <summary>
    /// Starting status. Null = the normal status at the shop (staff orders Accepted, online orders Received).
    /// The cloud uses Pending for online orders not yet acknowledged by the shop.
    /// </summary>
    public OrderStatus? StartingStatus { get; init; }
}

/// <summary>Where orders are kept. The Store Hub implements this with its database.</summary>
public interface IOrderStore
{
    /// <summary>Finds an order by the sending device's key, or null.</summary>
    Order? FindByIdempotencyKey(Guid branchId, string idempotencyKey);

    /// <summary>Finds an order by id, or null.</summary>
    Order? FindById(Guid orderId);

    /// <summary>Saves a new order.</summary>
    void Add(Order order);
}

/// <summary>Simple in-memory order store (tests and single-process use).</summary>
public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly Dictionary<Guid, Order> _byId = [];
    private readonly Dictionary<(Guid, string), Order> _byKey = [];

    /// <summary>All orders.</summary>
    public IReadOnlyCollection<Order> All => _byId.Values;

    /// <inheritdoc />
    public Order? FindByIdempotencyKey(Guid branchId, string idempotencyKey) => _byKey.GetValueOrDefault((branchId, idempotencyKey));

    /// <inheritdoc />
    public Order? FindById(Guid orderId) => _byId.GetValueOrDefault(orderId);

    /// <inheritdoc />
    public void Add(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (_byId.ContainsKey(order.Id) || _byKey.ContainsKey((order.BranchId, order.IdempotencyKey)))
        {
            throw new InvalidOperationException("Order already stored.");
        }

        _byId[order.Id] = order;
        _byKey[(order.BranchId, order.IdempotencyKey)] = order;
    }
}

/// <summary>Result of submitting an order.</summary>
/// <param name="Order">The stored order (the existing one for a repeat).</param>
/// <param name="WasDuplicate">True when this submission had already been received; nothing new was created.</param>
public sealed record SubmitResult(Order Order, bool WasDuplicate);

/// <summary>Thrown when an idempotency key is reused for a different order.</summary>
public sealed class IdempotencyConflictException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public IdempotencyConflictException()
        : base("This order key was already used for a different order.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public IdempotencyConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public IdempotencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Accepts new orders into the single order list. The same submission sent twice (for example after a
/// dropped connection) returns the order already stored instead of creating a duplicate.
/// </summary>
public sealed class OrderIntake
{
    private readonly IOrderStore _store;
    private readonly IOrderNumberSequence _numbers;
    private readonly OrderPricingSetup _pricing;
    private readonly TimeOnly _dayStartsAt;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();

    /// <summary>Creates the intake for a branch.</summary>
    public OrderIntake(IOrderStore store, IOrderNumberSequence numbers, OrderPricingSetup pricing, TimeOnly dayStartsAt, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _numbers = numbers ?? throw new ArgumentNullException(nameof(numbers));
        _pricing = pricing ?? throw new ArgumentNullException(nameof(pricing));
        _dayStartsAt = dayStartsAt;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Prices an order that is still being built on the till, exactly as <see cref="Submit"/> would,
    /// without saving it or using up an order number. The order number shown is 000.
    /// </summary>
    public Order Quote(NewOrder request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var businessDate = BusinessDay.For(_clock.GetUtcNow(), _pricing.TimeZone, _dayStartsAt);
        var order = new Order(
            request.OrderId,
            "quote",
            string.Empty,
            request.BranchId,
            new OrderNumber(string.IsNullOrWhiteSpace(request.BranchCode) ? "QUOTE" : request.BranchCode, businessDate, 0),
            request.Source,
            request.Type,
            request.StartingStatus ?? OrderStatusRules.StartingStatusAtShop(request.Source),
            _pricing,
            _clock,
            request.CreatedBy,
            request.CustomerId,
            request.TableId);
        if (request.Contact is { } contact)
        {
            order.SetContactAtCreation(contact);
        }

        foreach (var line in request.Lines)
        {
            order.AddLine(line.Pricing, line.Display, request.CreatedBy, line.Note);
        }

        return order;
    }

    /// <summary>Submits an order. Safe to call again with the same submission.</summary>
    public SubmitResult Submit(NewOrder request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ArgumentException("An order key is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.BranchCode))
        {
            throw new ArgumentException("A branch code is required.", nameof(request));
        }

        var fingerprint = Fingerprint(request);

        lock (_lock)
        {
            var existing = _store.FindByIdempotencyKey(request.BranchId, request.IdempotencyKey);
            if (existing is not null)
            {
                if (existing.Fingerprint != fingerprint)
                {
                    throw new IdempotencyConflictException();
                }

                return new SubmitResult(existing, WasDuplicate: true);
            }

            if (_store.FindById(request.OrderId) is not null)
            {
                throw new IdempotencyConflictException("An order with this id already exists under a different key.");
            }

            var businessDate = BusinessDay.For(_clock.GetUtcNow(), _pricing.TimeZone, _dayStartsAt);
            var number = new OrderNumber(request.BranchCode, businessDate, _numbers.TakeNext(request.BranchId, businessDate));
            var status = request.StartingStatus ?? OrderStatusRules.StartingStatusAtShop(request.Source);

            var order = new Order(
                request.OrderId,
                request.IdempotencyKey,
                fingerprint,
                request.BranchId,
                number,
                request.Source,
                request.Type,
                status,
                _pricing,
                _clock,
                request.CreatedBy,
                request.CustomerId,
                request.TableId);

            if (request.Contact is { } contact)
            {
                order.SetContactAtCreation(contact);
            }

            foreach (var line in request.Lines)
            {
                order.AddLine(line.Pricing, line.Display, request.CreatedBy, line.Note);
            }

            _store.Add(order);
            return new SubmitResult(order, WasDuplicate: false);
        }
    }

    /// <summary>
    /// A short summary of what was ordered, used to tell a genuine retry (same content) from a reused key.
    /// </summary>
    public static string Fingerprint(NewOrder request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{request.OrderId}|{request.BranchId}|{request.Source}|{request.Type}|{request.CustomerId}|{request.TableId}");
        if (request.Contact is { } c)
        {
            text.Append(CultureInfo.InvariantCulture, $"|C:{c.Name}|{c.Phone}|{c.Address}|{c.Notes}");
        }

        foreach (var line in request.Lines)
        {
            text.Append('#');
            AppendLine(text, line.Pricing);
            text.Append('|').Append(line.Note);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void AppendLine(StringBuilder text, OrderLineInput line)
    {
        switch (line)
        {
            case ItemLineInput item:
                text.Append(CultureInfo.InvariantCulture, $"I:{item.Item.ProductId}:{item.Item.VariantId}:{item.Item.NormalUnitPrice.MinorUnits}:{item.Quantity}");
                foreach (var m in item.Modifiers)
                {
                    text.Append(CultureInfo.InvariantCulture, $":m{m.OptionId}:{m.UnitPrice.MinorUnits}");
                }

                break;
            case MealDealLineInput deal:
                text.Append(CultureInfo.InvariantCulture, $"D:{deal.Deal.ProductId}:{deal.Deal.VariantId}:{deal.Deal.NormalUnitPrice.MinorUnits}:{deal.Quantity}");
                foreach (var c in deal.Components)
                {
                    text.Append(CultureInfo.InvariantCulture, $":c{c.Item.ProductId}:{c.Item.VariantId}:{c.Upcharge?.MinorUnits}");
                    foreach (var m in c.Modifiers)
                    {
                        text.Append(CultureInfo.InvariantCulture, $":m{m.OptionId}:{m.UnitPrice.MinorUnits}");
                    }
                }

                break;
            default:
                throw new ArgumentException($"Unknown line type {line.GetType().Name}.", nameof(line));
        }
    }
}
