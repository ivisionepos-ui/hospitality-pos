using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Backups;
using Pos.Hub.Data;
using Pos.Hub.Live;

namespace Pos.Hub.Tests;

/// <summary>Backups: made, checked, copied, tidied and restored.</summary>
public sealed class BackupTests : HubTestBase
{
    private string CopyFolder => Path.Combine(Path.GetDirectoryName(DbPath)!, "usb-stick");

    internal override IReadOnlyDictionary<string, string> ExtraSettings => new Dictionary<string, string>
    {
        ["Backup:CopyFolder"] = CopyFolder,
    };

    private BackupService Backups => Host.Services.GetRequiredService<BackupService>();

    private static CreateOrderRequest EmptyOrder() =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, []);

    private async Task<OrderView> NewOrder() => await (await Cashier.Post("/api/orders", EmptyOrder())).Read<OrderView>();

    [Fact]
    public async Task Backup_holds_the_database_pin_key_and_certificates_all_checked()
    {
        await NewOrder();

        var response = await Manager.PostAsync(new Uri("/api/backups", UriKind.Relative), null);
        var result = await response.Read<BackupResult>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Ok);
        Assert.Null(result.CopyError);
        var manifest = BackupService.Verify(Path.Combine(Host.BackupFolder, result.File!));
        Assert.Equal(["hub-ca.pfx", "hub-tls.pfx", "hub.db", "hub.key"], manifest.Files.Select(f => f.Name));
        Assert.Equal(1, manifest.Orders);
        Assert.Equal("TEST1", manifest.BranchCode);
        Assert.Equal(HubDatabase.LatestSchemaVersion, manifest.SchemaVersion);

        // The second copy is there too, and is just as good.
        Assert.Equal(result.File, BackupService.List(CopyFolder)[0].Name);
        BackupService.Verify(Path.Combine(CopyFolder, result.File!));
    }

    [Fact]
    public async Task Only_managers_and_owners_can_see_or_make_backups()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.PostAsync(new Uri("/api/backups", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.GetAsync(new Uri("/api/backups", UriKind.Relative))).StatusCode);

        var status = await Owner.GetJson<BackupStatus>("/api/backups");

        Assert.True(status.Enabled);
        Assert.NotEmpty(status.Backups); // made when the hub started
    }

    [Fact]
    public async Task Scheduled_backup_is_skipped_when_nothing_changed()
    {
        var first = Backups.BackUp("Scheduled", force: false);
        var second = Backups.BackUp("Scheduled", force: false);
        await NewOrder();
        Clock.Now = Clock.Now.AddMinutes(1);
        var third = Backups.BackUp("Scheduled", force: false);

        Assert.True(second.Skipped);
        Assert.Equal(first.File, second.File);
        Assert.False(third.Skipped);
        Assert.NotEqual(first.File, third.File);
    }

    [Fact]
    public void Damaged_backup_is_detected()
    {
        var file = Path.Combine(Host.BackupFolder, Backups.BackUp("Manual", force: true).File!);
        var damaged = Path.Combine(Path.GetDirectoryName(DbPath)!, "damaged.zip");
        File.Copy(file, damaged);
        using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            using var key = zip.GetEntry("hub.key")!.Open();
            key.WriteByte(0x55); // change the first byte of the PIN key
        }

        var ex = Assert.Throws<InvalidDataException>(() => BackupService.Verify(damaged));
        Assert.Contains("hub.key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_copy_keeps_the_main_backup_and_warns_the_tills()
    {
        Directory.Delete(CopyFolder, recursive: true);
        File.WriteAllText(CopyFolder, "a file where the folder should be, like a missing USB stick");
        var before = (await TillOnly.GetJson<CatchUpResult>("/api/live/events?since=0")).LastSeq;

        var result = Backups.BackUp("Manual", force: true);

        Assert.True(result.Ok);
        Assert.NotNull(result.CopyError);
        Assert.True(File.Exists(Path.Combine(Host.BackupFolder, result.File!)));
        var events = (await TillOnly.GetJson<CatchUpResult>($"/api/live/events?since={before}")).Events;
        Assert.Contains(events, e => e.Kind == LiveEventKinds.BackupFailed && e.Summary!.Contains("copy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Z_report_starts_a_backup()
    {
        var response = await Manager.Post("/api/reports/z", new ZReportRequest(null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        for (var i = 0; i < 100 && Backups.LastResult?.Reason != "Z report"; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal("Z report", Backups.LastResult?.Reason);
        Assert.True(Backups.LastResult!.Ok);
    }

    [Fact]
    public void Second_hub_on_the_same_data_is_refused()
    {
        Assert.Null(InstanceLock.TryAcquire(Path.GetDirectoryName(DbPath)!));
    }

    [Fact]
    public async Task Restore_puts_back_the_backup_and_keeps_what_it_replaced()
    {
        var kept = await NewOrder();
        var backup = Path.Combine(Host.BackupFolder, Backups.BackUp("Manual", force: true).File!);
        var lost = await NewOrder();
        var settings = Host.Services.GetRequiredService<HubSettings>();

        // Refused while the hub is running.
        using (var console = new StringWriter())
        {
            Assert.Equal(1, BackupService.Restore(backup, settings, console));
            Assert.Contains("the hub is running", console.ToString(), StringComparison.Ordinal);
        }

        Host.Dispose();
        using (var console = new StringWriter())
        {
            Assert.Equal(0, BackupService.Restore(backup, settings, console));
            Assert.Contains("1 orders", console.ToString(), StringComparison.Ordinal);
        }

        var aside = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(DbPath)!, "before-restore-*"));
        Assert.True(File.Exists(Path.Combine(aside, "hub.db")));

        Restart(); // same PINs and device keys still work: the key came back with the database
        var orders = await TillOnly.GetJson<List<OrderView>>("/api/orders?date=2026-11-05");
        Assert.Equal([kept.Id], orders.Select(o => o.Id));
        Assert.DoesNotContain(orders, o => o.Id == lost.Id);
    }

    [Fact]
    public void Restore_refuses_a_file_that_is_not_a_backup()
    {
        var notBackup = Path.Combine(Path.GetDirectoryName(DbPath)!, "photo.zip");
        using (var zip = ZipFile.Open(notBackup, ZipArchiveMode.Create))
        {
            zip.CreateEntry("photo.jpg");
        }

        Host.Dispose();
        using var console = new StringWriter();

        Assert.Equal(1, BackupService.Restore(notBackup, new HubSettings { DatabasePath = DbPath }, console));
        Assert.Contains("not a hub backup", console.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(DbPath)!, "before-restore-*"));
        Restart();
    }
}

/// <summary>Which old backups are kept.</summary>
public sealed class BackupPruneTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pos-backup-prune", Guid.NewGuid().ToString("N"));

    public BackupPruneTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private void Make(DateTime at) => File.WriteAllText(Path.Combine(_folder, $"hub-backup-SHOP-1-{at:yyyyMMdd-HHmmss}.zip"), "x");

    [Fact]
    public void Keeps_two_days_of_hourly_then_one_a_day_for_thirty_days()
    {
        var now = new DateTime(2026, 11, 5, 23, 30, 0);
        for (var hour = 0; hour < 24 * 40; hour++)
        {
            Make(now.AddHours(-hour));
        }

        File.WriteAllText(Path.Combine(_folder, "my-notes.zip"), "not a backup");

        var deleted = BackupService.Prune(_folder, now, new BackupSettings());
        var left = BackupService.List(_folder);

        Assert.Equal(48 + 28, left.Count); // 48 hourly + the last of each of the 28 older days within 30 days
        Assert.All(left.Where(b => now - b.TakenAt >= TimeSpan.FromHours(48)), b => Assert.Equal(23, b.TakenAt.Hour));
        Assert.True(left[^1].TakenAt.Date > now.Date.AddDays(-30));
        Assert.Equal(24 * 40 - left.Count, deleted.Count);
        Assert.True(File.Exists(Path.Combine(_folder, "my-notes.zip")));
    }

    [Fact]
    public void Never_leaves_fewer_than_three_even_if_all_are_old()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        for (var day = 100; day < 105; day++)
        {
            Make(now.AddDays(-day));
        }

        BackupService.Prune(_folder, now, new BackupSettings());

        Assert.Equal(3, BackupService.List(_folder).Count);
    }
}

/// <summary>Database safety when the program is upgraded or downgraded.</summary>
public sealed class DatabaseUpgradeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pos-db-upgrade", Guid.NewGuid().ToString("N"));

    public DatabaseUpgradeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Copy_is_kept_before_an_upgrade()
    {
        var path = Path.Combine(_folder, "hub.db");
        using (var db = new HubDatabase(path))
        {
            // Make it look like a database from before the printing upgrade.
            db.Execute("DROP TABLE print_jobs; DROP TABLE order_line_requests; DROP TABLE customers; DROP TABLE postcode_cache; DELETE FROM schema_version WHERE version >= 5;");
        }

        using (var upgraded = new HubDatabase(path))
        {
            Assert.Equal(HubDatabase.LatestSchemaVersion, upgraded.SchemaVersion);
        }

        var copy = Assert.Single(Directory.GetFiles(_folder, "hub.db.before-upgrade-v4-*.db"));
        Assert.Equal(4, HubDatabase.Check(copy));
    }

    [Fact]
    public void Database_from_a_newer_hub_is_not_touched()
    {
        var path = Path.Combine(_folder, "hub.db");
        using (var db = new HubDatabase(path))
        {
            db.Execute("INSERT INTO schema_version (version) VALUES (999);");
        }

        var ex = Assert.Throws<InvalidOperationException>(() => new HubDatabase(path));
        Assert.Contains("newer version", ex.Message, StringComparison.Ordinal);
    }
}
