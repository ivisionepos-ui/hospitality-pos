namespace Pos.Hub.Backups;

/// <summary>
/// Makes sure only one copy of the hub uses a data folder at a time, and that a restore
/// never replaces the database while the hub is running. Held for as long as the hub runs.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    /// <summary>Takes the lock for a data folder, or returns null when something else holds it.</summary>
    public static InstanceLock? TryAcquire(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);
        try
        {
            return new InstanceLock(new FileStream(Path.Combine(folder, "hub.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Takes the lock, or stops with a clear message.</summary>
    public static InstanceLock Acquire(string folder) =>
        TryAcquire(folder) ?? throw new InvalidOperationException($"Another copy of the hub is already running with the data folder {Path.GetFullPath(folder)}.");

    /// <summary>The data folder for a database path.</summary>
    public static string FolderFor(string databasePath) => Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".";

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose(); // safe to call more than once
}
