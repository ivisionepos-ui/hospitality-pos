using System.Globalization;
using Pos.Hub.Data;
using Pos.Hub.Security;

namespace Pos.Hub.Printing;

/// <summary>A "No sale": the drawer opened without a sale.</summary>
/// <param name="At">When.</param>
/// <param name="Staff">Who asked.</param>
/// <param name="ApprovedBy">The manager who approved.</param>
/// <param name="Reason">Why.</param>
public sealed record DrawerOpen(DateTimeOffset At, string Staff, string ApprovedBy, string Reason);

/// <summary>Opening the cash drawer without a sale: a manager approves, and every opening is recorded.</summary>
public sealed class CashDrawerService(HubDatabase db, PrintService print, OrderService orders, AccessService access, TimeProvider clock)
{
    /// <summary>Opens the drawer and records it. Throws when the receipt printer cannot open a drawer.</summary>
    public void NoSale(Guid deviceId, Guid staffId, Guid approvedBy, string reason)
    {
        var why = (reason ?? string.Empty).Trim();
        if (why.Length == 0)
        {
            throw new ArgumentException("Say why the drawer is being opened.");
        }

        if (!print.HasDrawerFor(deviceId))
        {
            throw new InvalidOperationException("No cash drawer is set up for this till. The drawer plugs into a thermal receipt printer: choose it in More → Printers.");
        }

        db.RunInTransaction(() =>
        {
            db.Execute(
                "INSERT INTO drawer_opens (at, business_date, device_id, staff_id, approved_by, reason) VALUES ($at, $d, $dev, $s, $a, $r);",
                ("$at", clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$d", orders.Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$dev", deviceId.ToString()), ("$s", staffId.ToString()), ("$a", approvedBy.ToString()), ("$r", why.Length > 120 ? why[..120] : why));
            print.QueueDrawer(deviceId);
            return 0;
        });
    }

    /// <summary>"No sale" openings on a trading day (default today), oldest first.</summary>
    public IReadOnlyList<DrawerOpen> Opens(DateOnly? day) => db.Run(() =>
    {
        var date = (day ?? orders.Today()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var command = db.Command("SELECT at, staff_id, approved_by, reason FROM drawer_opens WHERE business_date = $d ORDER BY id;", ("$d", date));
        using var reader = command.ExecuteReader();
        var rows = new List<(DateTimeOffset At, Guid Staff, Guid Approver, string Reason)>();
        while (reader.Read())
        {
            rows.Add((DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3)));
        }

        return rows.Select(r => new DrawerOpen(r.At, access.StaffName(r.Staff), access.StaffName(r.Approver), r.Reason)).ToList();
    });
}
