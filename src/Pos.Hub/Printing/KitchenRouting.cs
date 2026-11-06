using Pos.Domain.Orders;
using Pos.Domain.Pricing;

namespace Pos.Hub.Printing;

/// <summary>What a kitchen printer prints.</summary>
public enum KitchenPrintMode
{
    /// <summary>Every item (e.g. the one kitchen printer, or a copy for the person checking orders).</summary>
    Everything,

    /// <summary>Only the chosen categories and items (e.g. Pizzas on the pizza printer).</summary>
    Chosen,
}

/// <summary>A kitchen printer in use: its name, where it prints, and what it prints.</summary>
public sealed class KitchenStation
{
    /// <summary>The id of the first kitchen printer when none have been set up (the one from appsettings.json).</summary>
    public static readonly Guid MainId = new("6b1c4c5e-0000-4000-8000-00000000000a");

    /// <summary>Id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Name printed at the top of its tickets, e.g. "Pizza".</summary>
    public required string Name { get; init; }

    /// <summary>Where it prints.</summary>
    public required PrinterSettings Printer { get; init; }

    /// <summary>Everything, or only what is chosen.</summary>
    public KitchenPrintMode Mode { get; init; } = KitchenPrintMode.Everything;

    /// <summary>Categories it prints (Mode = Chosen).</summary>
    public IReadOnlySet<Guid> CategoryIds { get; init; } = new HashSet<Guid>();

    /// <summary>Single items it also prints, whatever their category (Mode = Chosen).</summary>
    public IReadOnlySet<Guid> ProductIds { get; init; } = new HashSet<Guid>();

    /// <summary>Single items it does not print, although their category is chosen (Mode = Chosen).</summary>
    public IReadOnlySet<Guid> ExcludedProductIds { get; init; } = new HashSet<Guid>();

    /// <summary>True when this printer (set to "Only what I choose") prints the item.</summary>
    public bool Prints(Guid productId, Guid categoryId) =>
        Mode == KitchenPrintMode.Chosen && !ExcludedProductIds.Contains(productId) && (ProductIds.Contains(productId) || CategoryIds.Contains(categoryId));
}

/// <summary>
/// Which kitchen printers print which items. An item prints on every printer that chose its category or the item
/// itself ("also here"), except printers where it is marked "not here"; printers set to "Everything" always print it;
/// so one item can print on several printers. An item that no printer prints goes to the first printer, so nothing is
/// ever lost. A meal deal goes whole to a printer that chose the deal (or its category); otherwise each
/// item in it is routed on its own, and each printer gets the deal with only its items.
/// </summary>
public static class KitchenRouting
{
    /// <summary>The lines each printer prints, in printer order; printers with nothing to print are left out.</summary>
    public static IReadOnlyList<(KitchenStation Station, IReadOnlyList<OrderLine> Lines)> Route(IReadOnlyList<OrderLine> lines, IReadOnlyList<KitchenStation> stations)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(stations);
        if (stations.Count == 0)
        {
            return [];
        }

        var result = stations.ToDictionary(s => s.Id, _ => new List<OrderLine>());
        foreach (var line in lines)
        {
            if (line.Pricing is MealDealLineInput deal)
            {
                var whole = Targets(deal.Deal.ProductId, deal.Deal.CategoryId, stations, includeFallback: false);
                if (whole.Count > 0)
                {
                    foreach (var s in whole.Union(Everything(stations)))
                    {
                        result[s.Id].Add(line);
                    }

                    continue;
                }

                // Each item of the deal on its own printer; "Everything" printers get the whole deal.
                var parts = deal.Components.Select((c, i) => (Index: i, Targets: Targets(c.Item.ProductId, c.Item.CategoryId, stations, includeFallback: true))).ToList();
                foreach (var s in stations)
                {
                    if (s.Mode == KitchenPrintMode.Everything)
                    {
                        result[s.Id].Add(line);
                        continue;
                    }

                    var mine = parts.Where(p => p.Targets.Contains(s)).Select(p => p.Index).ToList();
                    if (mine.Count > 0)
                    {
                        result[s.Id].Add(mine.Count == deal.Components.Count ? line : Part(line, mine));
                    }
                }

                continue;
            }

            var item = line.Pricing is ItemLineInput i ? i.Item : null;
            foreach (var s in Targets(item?.ProductId ?? Guid.Empty, item?.CategoryId ?? Guid.Empty, stations, includeFallback: true).Union(Everything(stations)))
            {
                result[s.Id].Add(line);
            }
        }

        return [.. stations.Where(s => result[s.Id].Count > 0).Select(s => (s, (IReadOnlyList<OrderLine>)result[s.Id]))];
    }

    /// <summary>A meal deal line showing only some of its items, marked as part of the deal.</summary>
    private static OrderLine Part(OrderLine line, IReadOnlyList<int> indexes)
    {
        var items = line.Display.DealItems;
        return line with
        {
            Display = line.Display with
            {
                Name = new Domain.Menus.LocalizedText($"{line.Display.Name.En} (part)", line.Display.Name.Ar),
                DealItems = [.. indexes.Where(i => i < items.Count).Select(i => items[i])],
            },
        };
    }

    private static IEnumerable<KitchenStation> Everything(IReadOnlyList<KitchenStation> stations) => stations.Where(s => s.Mode == KitchenPrintMode.Everything);

    // Every "only what I choose" printer that prints this item; the first printer if none does and none prints everything.
    private static List<KitchenStation> Targets(Guid productId, Guid categoryId, IReadOnlyList<KitchenStation> stations, bool includeFallback)
    {
        var targets = stations.Where(s => s.Prints(productId, categoryId)).ToList();
        return targets.Count > 0 || !includeFallback || stations.Any(s => s.Mode == KitchenPrintMode.Everything) ? targets : [stations[0]];
    }
}
