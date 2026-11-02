using System.Globalization;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;

namespace Pos.Hub.Data;

/// <summary>Order numbers kept in the database, so they carry on correctly after a restart.</summary>
public sealed class SqliteOrderNumberSequence(HubDatabase db) : IOrderNumberSequence
{
    /// <inheritdoc />
    public int TakeNext(Guid branchId, DateOnly businessDate)
    {
        var date = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return db.RunInTransaction(() =>
        {
            db.Execute(
                """
                INSERT INTO order_sequences (branch_id, business_date, last_number) VALUES ($b, $d, 1)
                ON CONFLICT (branch_id, business_date) DO UPDATE SET last_number = last_number + 1;
                """,
                ("$b", branchId.ToString()), ("$d", date));
            return Convert.ToInt32(
                db.Scalar("SELECT last_number FROM order_sequences WHERE branch_id = $b AND business_date = $d;", ("$b", branchId.ToString()), ("$d", date)),
                CultureInfo.InvariantCulture);
        });
    }
}

/// <summary>Orders saved in the database as their full state, plus columns for finding them quickly.</summary>
public sealed class SqliteOrderStore(HubDatabase db, Func<OrderPricingSetup> pricing, TimeProvider clock) : IOrderStore
{
    /// <inheritdoc />
    public Order? FindByIdempotencyKey(Guid branchId, string idempotencyKey) =>
        Load("SELECT state_json FROM orders WHERE branch_id = $b AND idempotency_key = $k;", ("$b", branchId.ToString()), ("$k", idempotencyKey));

    /// <inheritdoc />
    public Order? FindById(Guid orderId) =>
        Load("SELECT state_json FROM orders WHERE id = $id;", ("$id", orderId.ToString()));

    /// <inheritdoc />
    public void Add(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var now = Iso(clock.GetUtcNow());
        db.Execute(
            """
            INSERT INTO orders (id, branch_id, idempotency_key, reference, business_date, status, created_at, updated_at, row_version, state_json)
            VALUES ($id, $b, $k, $ref, $d, $s, $c, $u, 1, $json);
            """,
            ("$id", order.Id.ToString()),
            ("$b", order.BranchId.ToString()),
            ("$k", order.IdempotencyKey),
            ("$ref", order.Number.Reference),
            ("$d", order.Number.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$s", order.Status.ToString()),
            ("$c", Iso(order.CreatedAt)),
            ("$u", now),
            ("$json", PosJson.Serialize(order.ToState())));
    }

    /// <summary>Saves changes to an existing order.</summary>
    public void Save(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var rows = db.Execute(
            "UPDATE orders SET status = $s, updated_at = $u, row_version = row_version + 1, state_json = $json WHERE id = $id;",
            ("$id", order.Id.ToString()),
            ("$s", order.Status.ToString()),
            ("$u", Iso(clock.GetUtcNow())),
            ("$json", PosJson.Serialize(order.ToState())));
        if (rows != 1)
        {
            throw new InvalidOperationException("Order not found in the database.");
        }
    }

    /// <summary>Orders for a trading day, oldest first, optionally only those with a given status.</summary>
    public IReadOnlyList<Order> ForDay(Guid branchId, DateOnly businessDate, OrderStatus? status)
    {
        using var command = db.Command(
            """
            SELECT state_json FROM orders
            WHERE branch_id = $b AND business_date = $d AND ($s IS NULL OR status = $s)
            ORDER BY created_at, reference;
            """,
            ("$b", branchId.ToString()),
            ("$d", businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$s", status?.ToString()));
        using var reader = command.ExecuteReader();
        var result = new List<Order>();
        while (reader.Read())
        {
            result.Add(Order.Restore(PosJson.Deserialize<OrderState>(reader.GetString(0)), pricing(), clock));
        }

        return result;
    }

    private Order? Load(string sql, params (string, object?)[] parameters) =>
        db.Scalar(sql, parameters) is string json
            ? Order.Restore(PosJson.Deserialize<OrderState>(json), pricing(), clock)
            : null;

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>The menu saved in the database: its current state, plus the change log.</summary>
public sealed class SqliteMenuStore(HubDatabase db)
{
    /// <summary>The saved menu, or null if none has been set up yet.</summary>
    public MenuState? Load() =>
        db.Scalar("SELECT state_json FROM menu_state WHERE id = 1;") is string json ? PosJson.Deserialize<MenuState>(json) : null;

    /// <summary>Saves the menu and any changes not yet logged.</summary>
    public void Save(Menu menu, IEnumerable<MenuChange> changes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(changes);
        db.Execute(
            """
            INSERT INTO menu_state (id, version, state_json, updated_at) VALUES (1, $v, $json, $u)
            ON CONFLICT (id) DO UPDATE SET version = $v, state_json = $json, updated_at = $u;
            """,
            ("$v", menu.Version),
            ("$json", PosJson.Serialize(menu.ToState())),
            ("$u", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));

        foreach (var change in changes)
        {
            db.Execute(
                "INSERT INTO menu_changes (version, at, user_id, kind, summary) VALUES ($v, $at, $user, $kind, $summary);",
                ("$v", change.Version),
                ("$at", change.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("$user", change.UserId.ToString()),
                ("$kind", change.Kind.ToString()),
                ("$summary", change.Summary));
        }
    }

    /// <summary>Removes the change log (used when a whole new menu is imported).</summary>
    public void ClearChangeLog() => db.Execute("DELETE FROM menu_changes;");

    /// <summary>Logged changes after a version, oldest first.</summary>
    public IReadOnlyList<MenuChangeRecord> ChangesSince(long version)
    {
        using var command = db.Command(
            "SELECT version, at, user_id, kind, summary FROM menu_changes WHERE version > $v ORDER BY version;",
            ("$v", version));
        using var reader = command.ExecuteReader();
        var result = new List<MenuChangeRecord>();
        while (reader.Read())
        {
            result.Add(new MenuChangeRecord(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return result;
    }
}

/// <summary>A menu change as kept in the log.</summary>
/// <param name="Version">Menu version after the change.</param>
/// <param name="At">When.</param>
/// <param name="UserId">Who.</param>
/// <param name="Kind">Added, Updated, Moved or Undo.</param>
/// <param name="Summary">What changed.</param>
public sealed record MenuChangeRecord(long Version, DateTimeOffset At, Guid UserId, string Kind, string Summary);
