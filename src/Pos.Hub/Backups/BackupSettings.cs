namespace Pos.Hub.Backups;

/// <summary>The "Backup" section of appsettings.json.</summary>
public sealed class BackupSettings
{
    /// <summary>Automatic backups on or off. Leave on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where backups are kept on this PC.</summary>
    public string Folder { get; set; } = "data/backups";

    /// <summary>
    /// A second place for a copy of each backup, ideally another disk, a USB stick or a network folder
    /// (e.g. "E:\PosBackups"). Empty = no second copy.
    /// </summary>
    public string CopyFolder { get; set; } = string.Empty;

    /// <summary>Minutes between automatic backups (skipped when nothing has changed).</summary>
    public double IntervalMinutes { get; set; } = 60;

    /// <summary>Keep every backup from the last this-many hours.</summary>
    public int KeepHours { get; set; } = 48;

    /// <summary>After that, keep the last backup of each day for this many days.</summary>
    public int KeepDays { get; set; } = 30;

    /// <summary>Never keep fewer than this many backups, however old.</summary>
    public int KeepAtLeast { get; set; } = 3;

    /// <summary>Time between automatic backups.</summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);
}
