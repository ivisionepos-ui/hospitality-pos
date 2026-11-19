using System.Globalization;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Pricing;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Data;

namespace Pos.Hub;

/// <summary>A customer's earlier order, as listed on the customer screen.</summary>
/// <param name="Id">Order id.</param>
/// <param name="Number">Order number, e.g. "014".</param>
/// <param name="CreatedAt">When it was placed.</param>
/// <param name="Type">Takeaway, delivery…</param>
/// <param name="Total">What it cost.</param>
/// <param name="Summary">"2 × Doner kebab, Chips, Can".</param>
public sealed record PastOrder(Guid Id, string Number, DateTimeOffset CreatedAt, OrderType Type, Money Total, string Summary);

/// <summary>A past order ready to be added again, with the items that cannot be added any more.</summary>
/// <param name="Lines">Items and meal deals as they would be picked on the till today.</param>
/// <param name="Unavailable">Names of items left out: no longer on the menu, sold out, or changed.</param>
public sealed record RepeatView(IReadOnlyList<OrderLineRequest> Lines, IReadOnlyList<string> Unavailable);

/// <summary>
/// "Same as last time": a regular customer's recent orders on the customer screen, and the choices of one of them
/// rebuilt against today's menu (today's prices; anything gone or sold out is left out and named).
/// </summary>
public sealed class RepeatOrderService(HubDatabase db, OrderService orders, MenuService menu)
{
    /// <summary>How many earlier orders the customer screen shows.</summary>
    public const int Shown = 5;

    /// <summary>The customer's latest orders (newest first), by phone number. Cancelled and turned-down ones are left out.</summary>
    public IReadOnlyList<PastOrder> ForPhone(string? phone)
    {
        if (!UkPhone.TryNormalise(phone, out var wanted))
        {
            return [];
        }

        // The phone is inside the stored order; a text match narrows the search before each order is read properly.
        var ids = db.Run(() =>
        {
            using var command = db.Command("SELECT id FROM orders WHERE state_json LIKE $p ORDER BY created_at DESC, rowid DESC LIMIT 100;", ("$p", $"%{wanted}%"));
            using var reader = command.ExecuteReader();
            var list = new List<Guid>();
            while (reader.Read())
            {
                list.Add(Guid.Parse(reader.GetString(0)));
            }

            return list;
        });

        var found = new List<PastOrder>();
        foreach (var id in ids)
        {
            var order = orders.Get(id);
            if (order.Contact?.Phone != wanted || order.Status is OrderStatus.Cancelled or OrderStatus.Rejected || order.ActiveLines.Count == 0)
            {
                continue;
            }

            found.Add(new PastOrder(order.Id, order.Number.Display, order.CreatedAt, order.Type, order.Totals.Total, Summary(order)));
            if (found.Count == Shown)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>The order's items as they would be picked today.</summary>
    public RepeatView Repeat(Guid orderId)
    {
        var order = orders.Get(orderId);
        return menu.Read(current =>
        {
            var builder = new MenuOrderBuilder(current);
            var lines = new List<OrderLineRequest>();
            var unavailable = new List<string>();
            foreach (var line in order.ActiveLines.OrderBy(l => l.EntryNumber))
            {
                var request = line.Pricing switch
                {
                    ItemLineInput item => Item(current, builder, item, line.Note),
                    MealDealLineInput deal => Deal(current, builder, deal, line.Note),
                    _ => null,
                };
                if (request is null)
                {
                    unavailable.Add(line.Display.Name.En);
                }
                else
                {
                    lines.Add(request);
                }
            }

            return new RepeatView(lines, unavailable);
        });
    }

    private static OrderLineRequest? Item(Menu menu, MenuOrderBuilder builder, ItemLineInput item, string? note)
    {
        if (!menu.HasProduct(item.Item.ProductId) || item.Item.VariantId is not { } variant)
        {
            return null;
        }

        var options = item.Modifiers.Select(m => m.OptionId).ToList();
        var selection = new ItemSelection(item.Item.ProductId, variant, options, item.Quantity);
        return builder.Check(selection).Count == 0
            ? new OrderLineRequest(item.Item.ProductId, variant, item.Quantity, options, null, note)
            : null;
    }

    /// <summary>The order keeps a deal's items in slot order; they are put back into the slots that allow them.</summary>
    private static OrderLineRequest? Deal(Menu menu, MenuOrderBuilder builder, MealDealLineInput deal, string? note)
    {
        if (!menu.HasProduct(deal.Deal.ProductId) || deal.Deal.VariantId is not { } dealVariant)
        {
            return null;
        }

        var slots = menu.GetProduct(deal.Deal.ProductId).DealSlots;
        var used = slots.ToDictionary(s => s.Id, _ => 0);
        var slotIndex = 0;
        var choices = new List<DealChoiceRequest>();
        foreach (var component in deal.Components)
        {
            if (!menu.HasProduct(component.Item.ProductId) || component.Item.VariantId is not { } variant)
            {
                return null;
            }

            var product = menu.GetProduct(component.Item.ProductId);
            while (slotIndex < slots.Count && (used[slots[slotIndex].Id] >= slots[slotIndex].MaxChoices || !Allows(slots[slotIndex], product, variant)))
            {
                slotIndex++;
            }

            if (slotIndex == slots.Count)
            {
                return null; // the deal has changed so much that this item no longer fits
            }

            used[slots[slotIndex].Id]++;
            choices.Add(new DealChoiceRequest(slots[slotIndex].Id, product.Id, variant, [.. component.Modifiers.Select(m => m.OptionId)]));
        }

        var selection = new DealSelection(deal.Deal.ProductId, dealVariant,
            [.. choices.Select(c => new SlotChoice(c.SlotId, c.ProductId, c.VariantId, c.Extras ?? []))], deal.Quantity);
        return builder.Check(selection).Count == 0
            ? new OrderLineRequest(deal.Deal.ProductId, dealVariant, deal.Quantity, null, choices, note)
            : null;
    }

    private static bool Allows(MealDealSlot slot, Product product, Guid variantId) => slot.Options.Any(o =>
        (o.Kind == SlotOptionKind.Variant && o.TargetId == variantId)
        || (o.Kind == SlotOptionKind.Product && o.TargetId == product.Id)
        || (o.Kind == SlotOptionKind.Category && o.TargetId == product.CategoryId));

    private static string Summary(Order order)
    {
        var parts = order.ActiveLines.OrderBy(l => l.EntryNumber).Select(l =>
        {
            var name = l.Display.Size is { } size ? $"{size.En} {l.Display.Name.En}" : l.Display.Name.En;
            return l.Pricing.Quantity > 1 ? $"{l.Pricing.Quantity.ToString(CultureInfo.InvariantCulture)} × {name}" : name;
        }).ToList();
        var text = string.Join(", ", parts);
        return text.Length > 120 ? $"{text[..117]}…" : text;
    }
}

/// <summary>
/// The till's "Popular" tab: the items sold most in the last 30 days (by quantity; a meal deal counts as the deal),
/// so the busiest buttons are one tap away. Worked out at most every 10 minutes.
/// </summary>
public sealed class PopularItemService(HubDatabase db, OrderService orders, TimeProvider clock, IConfiguration config)
{
    private readonly TimeSpan _refresh = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Hub:PopularRefreshSeconds", 600), 1, 3600));

    /// <summary>How many items the Popular tab shows.</summary>
    public const int Count = 12;

    private readonly Lock _gate = new();
    private (DateTimeOffset At, IReadOnlyList<Guid> Ids)? _cached;

    /// <summary>Product ids, best seller first. Empty while the shop has sold too little to tell.</summary>
    public IReadOnlyList<Guid> Ids()
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_cached is { } c && now - c.At < _refresh)
            {
                return c.Ids;
            }
        }

        var since = now.AddDays(-30).ToString("O", CultureInfo.InvariantCulture);
        var ids = db.Run(() =>
        {
            using var command = db.Command("SELECT id FROM orders WHERE created_at >= $since ORDER BY created_at DESC LIMIT 3000;", ("$since", since));
            using var reader = command.ExecuteReader();
            var list = new List<Guid>();
            while (reader.Read())
            {
                list.Add(Guid.Parse(reader.GetString(0)));
            }

            return list;
        });

        var sold = new Dictionary<Guid, int>();
        foreach (var id in ids)
        {
            var order = orders.Get(id);
            if (order.Status is OrderStatus.Cancelled or OrderStatus.Rejected)
            {
                continue;
            }

            foreach (var line in order.ActiveLines)
            {
                var product = line.Pricing switch
                {
                    ItemLineInput item => item.Item.ProductId,
                    MealDealLineInput deal => deal.Deal.ProductId,
                    _ => Guid.Empty,
                };
                sold[product] = sold.GetValueOrDefault(product) + line.Pricing.Quantity;
            }
        }

        var top = sold.Where(kv => kv.Key != Guid.Empty && kv.Value >= 2).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(Count).Select(kv => kv.Key).ToList();
        lock (_gate)
        {
            _cached = (now, top);
        }

        return top;
    }

    /// <summary>Forgets the worked-out list (after test sales are cleared).</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }
}

