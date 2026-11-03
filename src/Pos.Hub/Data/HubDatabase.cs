using Microsoft.Data.Sqlite;

namespace Pos.Hub.Data;

/// <summary>
/// The hub's SQLite database. One connection, used by one request at a time (through <see cref="Run{T}"/>),
/// which keeps writes strictly in order — plenty for a shop's tills and screens.
/// Write-ahead logging and full sync make sure a completed write survives a power cut.
/// </summary>
public sealed class HubDatabase : IDisposable
{
    /// <summary>Schema changes, in order. Never edit a released one; add a new version instead.</summary>
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            CREATE TABLE menu_state (
                id          INTEGER PRIMARY KEY CHECK (id = 1),
                version     INTEGER NOT NULL,
                state_json  TEXT    NOT NULL,
                updated_at  TEXT    NOT NULL
            );
            CREATE TABLE menu_changes (
                version  INTEGER PRIMARY KEY,
                at       TEXT NOT NULL,
                user_id  TEXT NOT NULL,
                kind     TEXT NOT NULL,
                summary  TEXT NOT NULL
            );
            CREATE TABLE orders (
                id               TEXT    PRIMARY KEY,
                branch_id        TEXT    NOT NULL,
                idempotency_key  TEXT    NOT NULL,
                reference        TEXT    NOT NULL,
                business_date    TEXT    NOT NULL,
                status           TEXT    NOT NULL,
                created_at       TEXT    NOT NULL,
                updated_at       TEXT    NOT NULL,
                row_version      INTEGER NOT NULL,
                state_json       TEXT    NOT NULL,
                UNIQUE (branch_id, idempotency_key),
                UNIQUE (reference)
            );
            CREATE INDEX ix_orders_day ON orders (branch_id, business_date, status);
            CREATE TABLE order_sequences (
                branch_id      TEXT    NOT NULL,
                business_date  TEXT    NOT NULL,
                last_number    INTEGER NOT NULL,
                PRIMARY KEY (branch_id, business_date)
            );
            """),
        (2, """
            CREATE TABLE staff (
                id          TEXT    PRIMARY KEY,
                name        TEXT    NOT NULL,
                role        TEXT    NOT NULL,
                pin_lookup  TEXT    NOT NULL UNIQUE,
                is_active   INTEGER NOT NULL,
                created_at  TEXT    NOT NULL
            );
            CREATE TABLE devices (
                id            TEXT    PRIMARY KEY,
                name          TEXT    NOT NULL,
                type          TEXT    NOT NULL,
                key_hash      TEXT    NOT NULL,
                is_active     INTEGER NOT NULL,
                created_at    TEXT    NOT NULL,
                last_seen_at  TEXT
            );
            CREATE TABLE pairing_codes (
                code_hash   TEXT    PRIMARY KEY,
                expires_at  TEXT    NOT NULL,
                created_by  TEXT    NOT NULL
            );
            CREATE TABLE sessions (
                token_hash  TEXT    PRIMARY KEY,
                staff_id    TEXT    NOT NULL REFERENCES staff (id),
                device_id   TEXT    NOT NULL REFERENCES devices (id),
                expires_at  TEXT    NOT NULL,
                revoked     INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE pin_failures (
                device_id  TEXT NOT NULL,
                at         TEXT NOT NULL
            );
            CREATE INDEX ix_pin_failures ON pin_failures (device_id, at);
            """),
        (3, """
            CREATE TABLE live_events (
                seq           INTEGER PRIMARY KEY AUTOINCREMENT,
                at            TEXT    NOT NULL,
                kind          TEXT    NOT NULL,
                for_kitchen   INTEGER NOT NULL,
                for_display   INTEGER NOT NULL,
                payload_json  TEXT    NOT NULL
            );
            """),
    ];

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private SqliteTransaction? _transaction;
    private List<Action> _afterCommit = [];

    /// <summary>Opens (and if needed creates and upgrades) the database file.</summary>
    public HubDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA synchronous = FULL;");
        Execute("PRAGMA foreign_keys = ON;");
        Migrate();
    }

    /// <summary>Highest schema version applied.</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>Runs work with sole use of the database.</summary>
    public T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            return work();
        }
    }

    /// <summary>Runs work with sole use of the database, inside a transaction: all of it is saved, or none.</summary>
    public T RunInTransaction<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_transaction is not null)
            {
                return work(); // already inside one
            }

            _transaction = _connection.BeginTransaction();
            T result;
            try
            {
                result = work();
                _transaction.Commit();
            }
            catch
            {
                _transaction.Rollback();
                _afterCommit = [];
                throw;
            }
            finally
            {
                _transaction.Dispose();
                _transaction = null;
            }

            // Only now is the change safely saved: tell the screens.
            var actions = _afterCommit;
            _afterCommit = [];
            foreach (var action in actions)
            {
                action();
            }

            return result;
        }
    }

    /// <summary>
    /// Runs an action once the current transaction has been saved (straight away if there is none).
    /// If the transaction is rolled back, the action never runs.
    /// </summary>
    public void AfterCommit(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_transaction is null)
            {
                action();
            }
            else
            {
                _afterCommit.Add(action);
            }
        }
    }

    /// <summary>Creates a command joined to the current transaction, if any.</summary>
    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var command = _connection.CreateCommand();
#pragma warning disable CA2100 // SQL text is fixed in code; values always go in parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Runs a statement and returns the rows changed.</summary>
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a query returning one value.</summary>
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _transaction?.Dispose();
        _connection.Dispose();
    }

    private void Migrate()
    {
        Execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");
        SchemaVersion = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(version), 0) FROM schema_version;"), System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (version, sql) in Migrations.Where(m => m.Version > SchemaVersion).OrderBy(m => m.Version))
        {
            RunInTransaction(() =>
            {
                Execute(sql);
                Execute("INSERT INTO schema_version (version) VALUES ($v);", ("$v", version));
                return 0;
            });
            SchemaVersion = version;
        }
    }
}
