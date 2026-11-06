using System.Globalization;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Hub.Backups;
using Pos.Hub.Data;

namespace Pos.Hub.MenuSheets;

/// <summary>The last import, for the screen and for undo.</summary>
/// <param name="At">When it was imported (UTC).</param>
/// <param name="ByUserId">Who imported it.</param>
/// <param name="FileName">The spreadsheet's name.</param>
/// <param name="Summary">What it did.</param>
/// <param name="MenuVersion">The menu version straight after the import.</param>
/// <param name="CanUndo">True while nothing else has changed the menu since.</param>
public sealed record LastImport(DateTimeOffset At, Guid ByUserId, string FileName, string Summary, long MenuVersion, bool CanUndo);

/// <summary>
/// Menu spreadsheet import: preview first, then import after a backup, with the menu as it was kept so the
/// import can be undone. Nothing changes until the preview has no problems and the person presses Import.
/// </summary>
public sealed class MenuImportService(HubDatabase db, MenuService menu, BackupService backups, TimeProvider clock)
{
    private const string Key = "menu.lastImport";

    /// <summary>What importing the file would do.</summary>
    public ImportPreview Preview(byte[] file, string fileName, MissingItems missing, Guid userId) =>
        Plan(file, fileName, missing, userId).Preview;

    /// <summary>Imports the file if it has no problems: backs up, keeps the menu as it was, then imports.</summary>
    /// <exception cref="InvalidOperationException">The sheet has problems, there is nothing to change, or the backup failed.</exception>
    public ImportPreview Import(byte[] file, string fileName, MissingItems missing, Guid userId)
    {
        var before = menu.Read(m => m.ToState());
        var plan = Plan(file, fileName, missing, userId);
        if (plan.State is null)
        {
            throw new InvalidOperationException($"The spreadsheet has {Count(plan.Preview.Problems.Count, "problem")} to fix first. Nothing was changed.");
        }

        if (!plan.Preview.CanImport)
        {
            throw new InvalidOperationException("The spreadsheet matches the menu already. Nothing was changed.");
        }

        var backup = backups.BackUp("Before menu import", force: true);
        if (!backup.Ok)
        {
            throw new InvalidOperationException($"A backup could not be made first, so nothing was changed: {backup.Error}");
        }

        try
        {
            return Save(plan, before, fileName, userId);
        }
        catch
        {
            menu.Reload(); // nothing was saved, so the hub must not keep the imported menu in memory either
            throw;
        }
    }

    private ImportPreview Save(ImportPlan plan, MenuState before, string fileName, Guid userId) =>
        db.RunInTransaction(() =>
        {
            // The menu must not have changed between the preview check and now (another till editing it).
            if (menu.Read(m => m.Version) != before.Version)
            {
                throw new InvalidOperationException("The menu was changed on another till just now. Check the preview again.");
            }

            var after = menu.Import(plan.State!, userId);
            var record = new SavedImport(clock.GetUtcNow(), userId, Path.GetFileName(fileName), MenuSheetImport.Summary(plan.Preview), after.Version, before);
            db.Execute(
                "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
                ("$k", Key), ("$v", PosJson.Serialize(record)), ("$at", record.At.ToString("O", CultureInfo.InvariantCulture)), ("$by", userId.ToString()));
            return plan.Preview;
        });

    /// <summary>The last import, or null.</summary>
    public LastImport? Last()
    {
        var saved = Saved();
        return saved is null ? null : new LastImport(saved.At, saved.ByUserId, saved.FileName, saved.Summary, saved.MenuVersion, menu.Read(m => m.Version) == saved.MenuVersion);
    }

    /// <summary>Puts the menu back as it was before the last import, if nothing has changed it since.</summary>
    public MenuState UndoLast(Guid userId)
    {
        var saved = Saved() ?? throw new InvalidOperationException("There is no import to undo.");
        if (menu.Read(m => m.Version) != saved.MenuVersion)
        {
            throw new InvalidOperationException("The menu has been changed since the import, so it cannot be undone in one step. Change it in Edit menu, or import the old spreadsheet again.");
        }

        var backup = backups.BackUp("Before undoing a menu import", force: true);
        if (!backup.Ok)
        {
            throw new InvalidOperationException($"A backup could not be made first, so nothing was changed: {backup.Error}");
        }

        try
        {
            return db.RunInTransaction(() =>
            {
                if (menu.Read(m => m.Version) != saved.MenuVersion)
                {
                    throw new InvalidOperationException("The menu was changed on another till just now, so the import cannot be undone in one step.");
                }

                var restored = menu.Import(saved.Before, userId);
                db.Execute("DELETE FROM settings WHERE key = $k;", ("$k", Key));
                return restored;
            });
        }
        catch
        {
            menu.Reload(); // after the rollback: the menu as it is saved
            throw;
        }
    }

    private ImportPlan Plan(byte[] file, string fileName, MissingItems missing, Guid userId)
    {
        var sheets = Spreadsheet.Read(file, fileName);
        var current = menu.Read(m => m.ToState());
        return MenuSheetImport.Plan(current, sheets, missing, userId, clock);
    }

    private SavedImport? Saved() =>
        db.Run(() => db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) as string) is { } json ? PosJson.Deserialize<SavedImport>(json) : null;

    private static string Count(int n, string word) => $"{n} {word}{(n == 1 ? string.Empty : "s")}";

    /// <summary>What is kept of the last import: its details and the whole menu as it was before.</summary>
    private sealed record SavedImport(DateTimeOffset At, Guid ByUserId, string FileName, string Summary, long MenuVersion, MenuState Before);
}
