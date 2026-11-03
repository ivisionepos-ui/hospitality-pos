using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Pos.Domain.Common;
using Pos.Hub.Data;
using Pos.Hub.Live;

namespace Pos.Hub.Backups;

/// <summary>One file listed in a backup and its fingerprint.</summary>
/// <param name="Name">File name inside the backup.</param>
/// <param name="Size">Bytes.</param>
/// <param name="Sha256">SHA-256 of the file, used to prove the backup is undamaged.</param>
public sealed record BackupManifestFile(string Name, long Size, string Sha256);

/// <summary>What a backup contains ("backup.json" inside the zip).</summary>
/// <param name="Format">Backup format version.</param>
/// <param name="CreatedAt">When it was made (UTC).</param>
/// <param name="BranchId">Branch.</param>
/// <param name="BranchCode">Branch code.</param>
/// <param name="SchemaVersion">Database schema version.</param>
/// <param name="Reason">Why it was made: Scheduled, Start-up, Z report, Manual.</param>
/// <param name="Orders">Orders in the database.</param>
/// <param name="Files">The files and their fingerprints.</param>
public sealed record BackupManifest(int Format, DateTimeOffset CreatedAt, Guid BranchId, string BranchCode, int SchemaVersion, string Reason, long Orders, IReadOnlyList<BackupManifestFile> Files);

/// <summary>A backup file in a folder.</summary>
/// <param name="Name">File name.</param>
/// <param name="TakenAt">Shop time it was made (from the name).</param>
/// <param name="Size">Bytes.</param>
public sealed record BackupFile(string Name, DateTime TakenAt, long Size);

/// <summary>Result of one backup attempt.</summary>
/// <param name="Ok">True when the backup was made and checked (or skipped because nothing changed).</param>
/// <param name="Skipped">True when nothing had changed since the last backup.</param>
/// <param name="File">The backup file name.</param>
/// <param name="Reason">Why it was made.</param>
/// <param name="Error">What went wrong, if the backup failed.</param>
/// <param name="CopyError">What went wrong with the second copy, if it failed.</param>
public sealed record BackupResult(bool Ok, bool Skipped, string? File, string Reason, string? Error, string? CopyError);

/// <summary>Backup state for the back office.</summary>
/// <param name="Enabled">Automatic backups on.</param>
/// <param name="Folder">Main backup folder.</param>
/// <param name="CopyFolder">Second-copy folder, if set.</param>
/// <param name="LastResult">The latest attempt since the hub started.</param>
/// <param name="Backups">Backups in the main folder, newest first.</param>
/// <param name="Copies">Backups in the second-copy folder, newest first.</param>
public sealed record BackupStatus(bool Enabled, string Folder, string? CopyFolder, BackupResult? LastResult, IReadOnlyList<BackupFile> Backups, IReadOnlyList<BackupFile> Copies);

/// <summary>
/// Makes, checks, copies and tidies backups of the hub's data, and restores them.
/// A backup is one zip holding the database (a consistent snapshot), the PIN key and the shop certificates,
/// with a list of SHA-256 fingerprints so damage is detected before anyone relies on it.
/// </summary>
public sealed partial class BackupService
{
    /// <summary>Name of the list of files inside each backup.</summary>
    public const string ManifestName = "backup.json";

    private const string DatabaseEntry = "hub.db";
    private const string KeyEntry = "hub.key";
    private static readonly string[] CertificateEntries = ["hub-ca.pfx", "hub-tls.pfx"];

    private readonly HubDatabase _db;
    private readonly HubSettings _hub;
    private readonly BackupSettings _settings;
    private readonly TimeProvider _clock;
    private readonly LiveUpdates _live;
    private readonly ILogger<BackupService> _log;
    private readonly Lock _gate = new();
    private readonly Channel<string> _requests = Channel.CreateUnbounded<string>();
    private long? _changesAtLastBackup;

    /// <summary>Creates the service.</summary>
    public BackupService(HubDatabase db, HubSettings hub, BackupSettings settings, TimeProvider clock, LiveUpdates live, ILogger<BackupService> log)
    {
        _db = db;
        _hub = hub;
        _settings = settings;
        _clock = clock;
        _live = live;
        _log = log;
    }

    /// <summary>Latest attempt since start-up.</summary>
    public BackupResult? LastResult { get; private set; }

    /// <summary>The settings in use.</summary>
    public BackupSettings Settings => _settings;

    /// <summary>Asks the background worker to make a backup soon (e.g. after the Z report).</summary>
    public void Request(string reason) => _requests.Writer.TryWrite(reason);

    /// <summary>Waits for a request, or until the next scheduled backup is due ("Scheduled").</summary>
    public async Task<string> WaitForNext(TimeSpan interval, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(interval);
        try
        {
            return await _requests.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return "Scheduled";
        }
    }

    /// <summary>Shop time now.</summary>
    private DateTime LocalNow => TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _hub.TimeZone).DateTime;

    /// <summary>True when there is no backup yet, or the newest is older than the interval.</summary>
    public bool IsDue()
    {
        var backups = List(_settings.Folder);
        return backups.Count == 0 || LocalNow - backups[0].TakenAt >= _settings.Interval;
    }

    /// <summary>
    /// Makes a backup now. With <paramref name="force"/> false, skips it when nothing has changed since the last one.
    /// Never throws: failures are reported in the result, logged, and sent to the tills.
    /// </summary>
    public BackupResult BackUp(string reason, bool force)
    {
        lock (_gate)
        {
            var result = MakeBackup(reason, force);
            LastResult = result;
            if (result.Error is not null)
            {
                HubLog.BackupFailed(_log, reason, result.Error);
                _live.BackupFailed($"Backup failed: {result.Error}");
            }
            else if (result.CopyError is not null)
            {
                HubLog.BackupCopyFailed(_log, _settings.CopyFolder, result.CopyError);
                _live.BackupFailed($"Backup made, but the copy to {_settings.CopyFolder} failed: {result.CopyError}");
            }
            else if (!result.Skipped)
            {
                HubLog.BackupMade(_log, result.File!, reason);
            }

            return result;
        }
    }

    /// <summary>Backups in both folders, newest first.</summary>
    public BackupStatus Status() => new(
        _settings.Enabled,
        Path.GetFullPath(_settings.Folder),
        string.IsNullOrWhiteSpace(_settings.CopyFolder) ? null : _settings.CopyFolder,
        LastResult,
        List(_settings.Folder),
        string.IsNullOrWhiteSpace(_settings.CopyFolder) ? [] : SafeList(_settings.CopyFolder));

    /// <summary>Backups in a folder, newest first. Files not named like backups are ignored.</summary>
    public static IReadOnlyList<BackupFile> List(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "hub-backup-*.zip")
            .Select(path => (Path: path, Match: BackupName().Match(Path.GetFileName(path))))
            .Where(f => f.Match.Success)
            .Select(f => (
                Name: Path.GetFileName(f.Path),
                TakenAt: DateTime.ParseExact(f.Match.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                Size: new FileInfo(f.Path).Length,
                Number: f.Match.Groups["n"].Success ? int.Parse(f.Match.Groups["n"].Value, CultureInfo.InvariantCulture) : 1))
            .OrderByDescending(f => f.TakenAt)
            .ThenByDescending(f => f.Number)
            .Select(f => new BackupFile(f.Name, f.TakenAt, f.Size))];
    }

    /// <summary>
    /// Deletes old backups: keeps the newest <see cref="BackupSettings.KeepAtLeast"/>, everything from the last
    /// <see cref="BackupSettings.KeepHours"/> hours, and the last backup of each of the last <see cref="BackupSettings.KeepDays"/> days.
    /// Returns the names deleted.
    /// </summary>
    public static IReadOnlyList<string> Prune(string folder, DateTime localNow, BackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var backups = List(folder);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var backup in backups.Take(settings.KeepAtLeast))
        {
            keep.Add(backup.Name);
        }

        foreach (var backup in backups.Where(b => localNow - b.TakenAt < TimeSpan.FromHours(settings.KeepHours)))
        {
            keep.Add(backup.Name);
        }

        foreach (var day in backups.GroupBy(b => b.TakenAt.Date).Where(d => (localNow.Date - d.Key).TotalDays < settings.KeepDays))
        {
            keep.Add(day.First().Name); // newest of that day
        }

        var deleted = new List<string>();
        foreach (var backup in backups.Where(b => !keep.Contains(b.Name)))
        {
            File.Delete(Path.Combine(folder, backup.Name));
            deleted.Add(backup.Name);
        }

        return deleted;
    }

    /// <summary>
    /// Opens a backup and checks every file against its fingerprint. Throws <see cref="InvalidDataException"/>
    /// if anything is missing, extra or damaged.
    /// </summary>
    public static BackupManifest Verify(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException($"{Path.GetFileName(zipPath)} is not a hub backup (no {ManifestName}).");
        BackupManifest manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifest = PosJson.Deserialize<BackupManifest>(reader.ReadToEnd());
        }

        if (manifest.Files.All(f => f.Name != DatabaseEntry))
        {
            throw new InvalidDataException("The backup has no database.");
        }

        var listed = manifest.Files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var extra = zip.Entries.Select(e => e.FullName).Where(n => n != ManifestName && !listed.Contains(n)).ToList();
        if (extra.Count > 0)
        {
            throw new InvalidDataException($"The backup holds files it does not list: {string.Join(", ", extra)}.");
        }

        foreach (var file in manifest.Files)
        {
            var entry = zip.GetEntry(file.Name) ?? throw new InvalidDataException($"The backup is missing {file.Name}.");
            using var stream = entry.Open();
            if (entry.Length != file.Size || Hash(stream) != file.Sha256)
            {
                throw new InvalidDataException($"{file.Name} in the backup is damaged (fingerprint does not match).");
            }
        }

        return manifest;
    }

    /// <summary>
    /// "Pos.Hub --restore file.zip": checks the backup, moves the current data aside into a "before-restore" folder,
    /// and puts the backup in its place. Refuses while the hub is running. Returns 0 when restored.
    /// </summary>
    public static int Restore(string zipPath, HubSettings hub, TextWriter console)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(console);
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            console.WriteLine($"Restore FAILED: backup file not found: {zipPath}");
            return 1;
        }

        var dataFolder = InstanceLock.FolderFor(hub.DatabasePath);
        using var instance = InstanceLock.TryAcquire(dataFolder);
        if (instance is null)
        {
            console.WriteLine("Restore FAILED: the hub is running. Close it first, then run the restore again.");
            return 1;
        }

        var staging = Path.Combine(dataFolder, $".restore-{Guid.NewGuid():N}");
        try
        {
            var manifest = Verify(zipPath);
            if (manifest.SchemaVersion > HubDatabase.LatestSchemaVersion)
            {
                console.WriteLine($"Restore FAILED: the backup was made by a newer version of the hub (schema {manifest.SchemaVersion}). Update the hub program first.");
                return 1;
            }

            ZipFile.ExtractToDirectory(zipPath, staging);
            _ = HubDatabase.Check(Path.Combine(staging, DatabaseEntry));

            var databasePath = Path.GetFullPath(hub.DatabasePath);
            var replacements = new List<(string From, string To)> { (DatabaseEntry, databasePath) };
            if (manifest.Files.Any(f => f.Name == KeyEntry))
            {
                replacements.Add((KeyEntry, Path.GetFullPath(hub.KeyFilePath)));
            }

            replacements.AddRange(CertificateEntries.Where(c => manifest.Files.Any(f => f.Name == c)).Select(c => (c, Path.Combine(dataFolder, c))));

            // Move the current files aside (never deleted), including the database's journal files.
            var aside = Path.Combine(dataFolder, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(aside);
            foreach (var current in replacements.Select(r => r.To).Concat([databasePath + "-wal", databasePath + "-shm"]).Where(File.Exists))
            {
                File.Move(current, Path.Combine(aside, Path.GetFileName(current)));
            }

            foreach (var (from, to) in replacements)
            {
                File.Move(Path.Combine(staging, from), to);
            }

            var taken = TimeZoneInfo.ConvertTime(manifest.CreatedAt, hub.TimeZone);
            console.WriteLine($"Restored the backup from {taken:dd/MM/yyyy HH:mm} ({manifest.Orders} orders, branch {manifest.BranchCode}).");
            console.WriteLine($"The data it replaced is kept in {aside}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or SqliteException)
        {
            console.WriteLine($"Restore FAILED: {ex.Message}");
            console.WriteLine("Nothing was changed unless stated above.");
            return 1;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private BackupResult MakeBackup(string reason, bool force)
    {
        var changes = _db.ChangeCount;
        if (!force && _changesAtLastBackup == changes && List(_settings.Folder).Count > 0)
        {
            return new BackupResult(true, true, List(_settings.Folder)[0].Name, reason, null, null);
        }

        string name;
        try
        {
            name = Write(reason);
            _changesAtLastBackup = changes;
            _ = Prune(_settings.Folder, LocalNow, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
        {
            return new BackupResult(false, false, null, reason, ex.Message, null);
        }

        string? copyError = null;
        if (!string.IsNullOrWhiteSpace(_settings.CopyFolder))
        {
            try
            {
                CopyAndCheck(Path.Combine(_settings.Folder, name), _settings.CopyFolder);
                _ = Prune(_settings.CopyFolder, LocalNow, _settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                copyError = ex.Message;
            }
        }

        return new BackupResult(true, false, name, reason, null, copyError);
    }

    /// <summary>Writes one checked backup into the main folder and returns its name.</summary>
    private string Write(string reason)
    {
        var folder = _settings.Folder;
        Directory.CreateDirectory(folder);
        foreach (var leftover in Directory.EnumerateFiles(folder, "*.partial"))
        {
            File.Delete(leftover); // from an attempt cut short, e.g. by a power cut
        }

        var name = $"hub-backup-{_hub.BranchCode}-{LocalNow:yyyyMMdd-HHmmss}.zip";
        for (var n = 2; File.Exists(Path.Combine(folder, name)); n++)
        {
            name = $"hub-backup-{_hub.BranchCode}-{LocalNow:yyyyMMdd-HHmmss}-{n}.zip";
        }

        var work = Path.Combine(folder, $".work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var databaseCopy = Path.Combine(work, DatabaseEntry);
            _db.BackupTo(databaseCopy); // consistent snapshot, integrity-checked
            var dataFolder = InstanceLock.FolderFor(_hub.DatabasePath);
            CopyIfExists(_hub.KeyFilePath, Path.Combine(work, KeyEntry));
            foreach (var certificate in CertificateEntries)
            {
                CopyIfExists(Path.Combine(dataFolder, certificate), Path.Combine(work, certificate));
            }

            var files = Directory.EnumerateFiles(work).Order(StringComparer.Ordinal).Select(path =>
            {
                using var stream = File.OpenRead(path);
                return new BackupManifestFile(Path.GetFileName(path), stream.Length, Hash(stream));
            }).ToList();
            var manifest = new BackupManifest(1, _clock.GetUtcNow(), _hub.BranchId, _hub.BranchCode, HubDatabase.Check(databaseCopy), reason, CountOrders(databaseCopy), files);
            File.WriteAllText(Path.Combine(work, ManifestName), PosJson.Serialize(manifest));

            var partial = Path.Combine(folder, name + ".partial");
            ZipFile.CreateFromDirectory(work, partial, CompressionLevel.Optimal, includeBaseDirectory: false);
            _ = Verify(partial);
            File.Move(partial, Path.Combine(folder, name));
            return name;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void CopyAndCheck(string source, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(source);
        var partial = Path.Combine(folder, name + ".partial");
        File.Copy(source, partial, overwrite: true);
        _ = Verify(partial);
        File.Move(partial, Path.Combine(folder, name), overwrite: true);
    }

    private static void CopyIfExists(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Copy(from, to);
        }
    }

    private static long CountOrders(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM orders;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<BackupFile> SafeList(string folder)
    {
        try
        {
            return List(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // e.g. the USB stick is not plugged in
        }
    }

    private static string Hash(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    [GeneratedRegex(@"^hub-backup-.+-(?<stamp>\d{8}-\d{6})(-(?<n>\d+))?\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupName();
}

/// <summary>Makes a backup when the hub starts (if one is due), every interval when something changed, and when asked.</summary>
public sealed class BackupWorker(BackupService backups) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!backups.Settings.Enabled)
        {
            return;
        }

        await Task.Yield();
        if (backups.IsDue())
        {
            backups.BackUp("Start-up", force: true);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            string reason;
            try
            {
                reason = await backups.WaitForNext(backups.Settings.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backups.BackUp(reason, force: reason != "Scheduled");
        }
    }
}
