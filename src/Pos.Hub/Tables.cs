using System.Globalization;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Data;

namespace Pos.Hub;

/// <summary>
/// How eat-in orders work at this shop (More → Eat-in and tables): with no tables an eat-in order goes straight
/// to the menu; with tables the till asks which table first, and the table is printed on tickets and receipts.
/// </summary>
/// <param name="UseTables">True when eat-in orders are taken for a table.</param>
/// <param name="Tables">The tables, in the order the till shows them (e.g. "1", "2", "Window 1").</param>
public sealed record TableSettings(bool UseTables, IReadOnlyList<string> Tables)
{
    /// <summary>Most tables.</summary>
    public const int MaxTables = 200;

    /// <summary>A new shop: no tables (eat-in goes straight to the menu).</summary>
    public static readonly TableSettings Default = new(false, []);

    /// <summary>Tables numbered 1 to <paramref name="count"/>.</summary>
    public static IReadOnlyList<string> Numbered(int count) =>
        [.. Enumerable.Range(1, count).Select(i => i.ToString(CultureInfo.InvariantCulture))];

    /// <summary>Checked and tidied: names trimmed, no blanks or repeats, at least one table when tables are used.</summary>
    /// <exception cref="ArgumentException">The settings cannot be used.</exception>
    public TableSettings Checked()
    {
        var names = (Tables ?? []).Select(t => t?.Trim() ?? string.Empty).Where(t => t.Length > 0).ToList();
        if (names.Count > MaxTables)
        {
            throw new ArgumentException($"At most {MaxTables} tables.");
        }

        if (names.FirstOrDefault(t => t.Length > Order.MaxTableLength) is { } tooLong)
        {
            throw new ArgumentException($"Table \"{tooLong[..Order.MaxTableLength]}…\" is longer than {Order.MaxTableLength} characters.");
        }

        if (names.GroupBy(t => t, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } repeat)
        {
            throw new ArgumentException($"Table \"{repeat.Key}\" is listed twice.");
        }

        if (UseTables && names.Count == 0)
        {
            throw new ArgumentException("Give the number of tables (or their names) to use tables.");
        }

        return new TableSettings(UseTables, names);
    }
}

/// <summary>Save the eat-in settings: tables on or off, and either a number of tables or their names.</summary>
/// <param name="UseTables">Use tables for eat-in orders.</param>
/// <param name="Count">Number the tables 1 to this (used when <paramref name="Tables"/> is empty).</param>
/// <param name="Tables">Table names, when they are not just numbers.</param>
public sealed record SaveTablesRequest(bool UseTables, int? Count = null, IReadOnlyList<string>? Tables = null);

/// <summary>A table on the till's table picker, with the open order at it, if any.</summary>
/// <param name="Name">Table name, e.g. "5".</param>
/// <param name="OrderId">The open order at this table.</param>
/// <param name="OrderNumber">Its number.</param>
/// <param name="Total">Its total.</param>
public sealed record TableStatus(string Name, Guid? OrderId, string? OrderNumber, Money? Total);

/// <summary>The tables for the till: on or off, and each table with its open order.</summary>
/// <param name="UseTables">True when eat-in orders need a table.</param>
/// <param name="Tables">The tables.</param>
public sealed record TablesView(bool UseTables, IReadOnlyList<TableStatus> Tables);

/// <summary>Reads and saves the eat-in settings, and checks the table on new eat-in orders.</summary>
public sealed class TableService(HubDatabase db, TimeProvider clock)
{
    private const string Key = "tables";

    /// <summary>The settings in force now.</summary>
    public TableSettings Get()
    {
        var saved = db.Run(() => db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) as string);
        return saved is null ? TableSettings.Default : PosJson.Deserialize<TableSettings>(saved);
    }

    /// <summary>Saves the settings; the next eat-in order uses them.</summary>
    public TableSettings Save(SaveTablesRequest request, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> tables;
        if (request.Tables is { Count: > 0 } names)
        {
            tables = names;
        }
        else if (request.Count is { } count)
        {
            tables = count is >= 1 and <= TableSettings.MaxTables
                ? TableSettings.Numbered(count)
                : throw new ArgumentException($"The number of tables must be 1 to {TableSettings.MaxTables}.");
        }
        else
        {
            tables = Get().Tables; // switching tables on or off keeps the list
        }

        var settings = new TableSettings(request.UseTables, tables).Checked();
        db.Run(() => db.Execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
            ("$k", Key), ("$v", PosJson.Serialize(settings)), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString())));
        return settings;
    }

    /// <summary>
    /// The table to save on a new order: an eat-in order needs one of the shop's tables when tables are used;
    /// otherwise no table is allowed.
    /// </summary>
    /// <exception cref="ArgumentException">The table is missing, unknown, or not allowed.</exception>
    public string? CheckForNewOrder(OrderType type, string? table)
    {
        var settings = Get();
        var given = string.IsNullOrWhiteSpace(table) ? null : table.Trim();
        if (type != OrderType.EatIn || !settings.UseTables)
        {
            return given is null ? null
                : type != OrderType.EatIn ? throw new ArgumentException("Only eat-in orders have a table.")
                : throw new ArgumentException("This shop does not use tables now. Take the order without a table.");
        }

        return given is null
            ? throw new ArgumentException("Choose the table for this eat-in order.")
            : Known(settings, given);
    }

    /// <summary>The table to move an order to: one of the shop's tables.</summary>
    public string CheckForMove(string? table)
    {
        var settings = Get();
        return !settings.UseTables
            ? throw new ArgumentException("This shop does not use tables now.")
            : Known(settings, string.IsNullOrWhiteSpace(table) ? throw new ArgumentException("Choose a table.") : table.Trim());
    }

    /// <summary>The tables with the open order at each (today's orders not yet finished).</summary>
    public TablesView View(IEnumerable<Order> todaysOrders)
    {
        var settings = Get();
        var open = todaysOrders
            .Where(o => o.Table is not null && !o.IsFinished && o.Type == OrderType.EatIn)
            .GroupBy(o => o.Table!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.CreatedAt).First(), StringComparer.OrdinalIgnoreCase);
        return new TablesView(settings.UseTables, [.. settings.Tables.Select(t =>
            open.TryGetValue(t, out var o) ? new TableStatus(t, o.Id, o.Number.Display, o.Totals.Total) : new TableStatus(t, null, null, null))]);
    }

    private static string Known(TableSettings settings, string table) =>
        settings.Tables.FirstOrDefault(t => string.Equals(t, table, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"There is no table \"{table}\". Choose one of the shop's tables.");
}
