using System.Globalization;
using Pos.Domain.Common;
using Pos.Hub.Backups;
using Pos.Hub.Data;
using Pos.Hub.Live;

namespace Pos.Hub;

/// <summary>One time the test sales were cleared (kept for ever, so it is always clear what happened).</summary>
/// <param name="At">When.</param>
/// <param name="ByUserId">Owner who did it.</param>
/// <param name="ByName">Their name at the time.</param>
/// <param name="Orders">Orders removed.</param>
/// <param name="Payments">Payments removed.</param>
/// <param name="Customers">Customers removed (0 if they were kept).</param>
/// <param name="Backup">The backup taken just before, holding everything that was removed.</param>
public sealed record TestDataCleared(DateTimeOffset At, Guid ByUserId, string ByName, long Orders, long Payments, long Customers, string? Backup);

/// <summary>
/// Whether the shop is still testing or trading for real. While testing, the owner can clear the test sales and start
/// from zero; going live is one-way, and after it nothing can delete a sale (UK record-keeping and the law against
/// sales-suppression tools: sales records stay for at least six years).
/// </summary>
/// <param name="Live">True once the owner has pressed Go live.</param>
/// <param name="WentLiveAt">When.</param>
/// <param name="WentLiveBy">Who.</param>
/// <param name="Cleared">Every time test data was cleared.</param>
public sealed record TradingStatus(bool Live, DateTimeOffset? WentLiveAt, string? WentLiveBy, IReadOnlyList<TestDataCleared> Cleared)
{
    /// <summary>A new shop starts in testing.</summary>
    public static TradingStatus Testing { get; } = new(false, null, null, []);
}

/// <summary>What the owner sends to clear the test data.</summary>
/// <param name="Confirm">Must be the word CLEAR, typed by the owner.</param>
/// <param name="AlsoCustomers">Also remove customers (names, phones, addresses) saved while testing.</param>
public sealed record ClearTestDataRequest(string? Confirm, bool AlsoCustomers);

/// <summary>What the owner sends to go live.</summary>
/// <param name="Confirm">Must be the words GO LIVE, typed by the owner.</param>
public sealed record GoLiveRequest(string? Confirm);

/// <summary>Testing or live, clearing test data, going live.</summary>
public sealed class TradingService(HubDatabase db, BackupService backups, LiveUpdates live, PopularItemService popular, TimeProvider clock)
{
    private const string Key = "trading";

    // Every table holding sales, payments or anything made from them. Menu, staff, tills, printers and settings stay.
    private static readonly string[] SalesTables =
        ["payments", "order_line_requests", "kitchen_part_ready", "online_orders", "print_jobs", "drawer_opens", "z_reports", "shifts", "order_sequences", "orders", "live_events"];

    /// <summary>Testing or live, and the history.</summary>
    public TradingStatus Status() => db.Run(() =>
        db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) is string json
            ? PosJson.Deserialize<TradingStatus>(json)
            : TradingStatus.Testing);

    /// <summary>
    /// Removes every order, payment, till session, Z report and print job made while testing, so order numbers and
    /// reports start from zero. A backup is taken first and nothing is removed if it fails.
    /// </summary>
    public TestDataCleared ClearTestData(ClearTestDataRequest request, Guid byUserId, string byName)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Status().Live)
        {
            throw new InvalidOperationException("The shop is live: sales can no longer be deleted. UK law requires sales records to be kept for at least six years.");
        }

        if (!string.Equals(request.Confirm?.Trim(), "CLEAR", StringComparison.Ordinal))
        {
            throw new ArgumentException("Type CLEAR to confirm.");
        }

        var backup = backups.BackUp("before-clearing-test-data", force: true);
        if (!backup.Ok)
        {
            throw new InvalidOperationException($"Nothing was cleared: the backup before clearing failed ({backup.Error}).");
        }

        return db.RunInTransaction(() =>
        {
            long Count(string table) => Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM {table};"), CultureInfo.InvariantCulture);
            var orders = Count("orders");
            var payments = Count("payments");
            var customers = request.AlsoCustomers ? Count("customers") : 0;
            foreach (var table in SalesTables)
            {
                db.Execute($"DELETE FROM {table};");
            }

            if (request.AlsoCustomers)
            {
                db.Execute("DELETE FROM customers;");
            }
            else
            {
                db.Execute("UPDATE customers SET order_count = 0, last_order_at = NULL;"); // their test orders are gone
            }

            var cleared = new TestDataCleared(clock.GetUtcNow(), byUserId, byName, orders, payments, customers, backup.File is null ? null : Path.GetFileName(backup.File));
            var status = Status();
            Save(status with { Cleared = [.. status.Cleared, cleared] }, byUserId);
            live.SalesCleared($"Test sales cleared by {byName}");
            popular.Forget();
            return cleared;
        });
    }

    /// <summary>Starts trading for real: from now on no sale can be deleted. Cannot be undone.</summary>
    public TradingStatus GoLive(GoLiveRequest request, Guid byUserId, string byName)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Confirm?.Trim(), "GO LIVE", StringComparison.Ordinal))
        {
            throw new ArgumentException("Type GO LIVE to confirm.");
        }

        return db.RunInTransaction(() =>
        {
            var status = Status();
            if (status.Live)
            {
                return status;
            }

            var live = status with { Live = true, WentLiveAt = clock.GetUtcNow(), WentLiveBy = byName };
            Save(live, byUserId);
            return live;
        });
    }

    private void Save(TradingStatus status, Guid byUserId) => db.Execute(
        "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
        ("$k", Key), ("$v", PosJson.Serialize(status)), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString()));
}
