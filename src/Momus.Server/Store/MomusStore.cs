using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Momus.Core;

namespace Momus.Server.Store;

/// <summary>
/// One SQLite file on a volume, no ORM. Everything the server remembers lives here: which
/// databases to scan, every scan it ran, and how long each finding has been true.
/// </summary>
/// <remarks>
/// Writes are serialized through a semaphore. SQLite allows one writer, and a server that scans a
/// handful of targets every minute has no reason to fight over that: it keeps the code free of
/// retry loops and "database is locked" surprises.
/// </remarks>
public sealed partial class MomusStore : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public MomusStore(string databasePath)
    {
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>An in-memory store for tests. Kept alive by the connection held open here.</summary>
    public static async Task<MomusStore> InMemoryAsync()
    {
        var store = new MomusStore($"file:momus-{Guid.NewGuid():N}?mode=memory&cache=shared");
        store._keepAlive = new SqliteConnection(store._connectionString);
        await store._keepAlive.OpenAsync();
        await store.InitializeAsync();
        return store;
    }

    private SqliteConnection? _keepAlive;

    // ---- schema ------------------------------------------------------------------------

    /// <summary>Applies every schema script not yet applied. Safe to call on every start.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await ExecuteAsync(connection, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);
            """, ct);

        var applied = new HashSet<long>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM schema_version";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) applied.Add(reader.GetInt64(0));
        }

        foreach (var (version, name, sql) in SchemaScripts())
        {
            if (!applied.Add(version)) continue;

            await using var tx = await connection.BeginTransactionAsync(ct);
            await ExecuteAsync(connection, sql, ct, tx);
            await ExecuteAsync(connection,
                "INSERT INTO schema_version (version, name, applied_at) VALUES ($v, $n, $t)", ct, tx,
                ("$v", version), ("$n", name), ("$t", Now()));
            await tx.CommitAsync(ct);
        }
    }

    /// <summary>Embedded <c>NNN_name.sql</c> scripts, in numeric order.</summary>
    private static IEnumerable<(long Version, string Name, string Sql)> SchemaScripts()
    {
        var assembly = typeof(MomusStore).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Schema.") && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name =>
            {
                var file = name[(name.LastIndexOf(".Schema.", StringComparison.Ordinal) + ".Schema.".Length)..];
                var version = long.Parse(file[..file.IndexOf('_')]);
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var text = new StreamReader(stream);
                return (version, file, text.ReadToEnd());
            })
            .OrderBy(s => s.version)
            .ToList();
    }

    // ---- targets -----------------------------------------------------------------------

    public async Task<IReadOnlyList<StoredTarget>> TargetsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, provider, connection_string, source, database_name, server_version,
                   created_at, last_scan_at, last_error
            FROM targets ORDER BY created_at
            """;

        var targets = new List<StoredTarget>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            targets.Add(new StoredTarget
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Provider = reader.GetString(2),
                ConnectionString = reader.GetString(3),
                Source = reader.GetString(4),
                DatabaseName = reader.IsDBNull(5) ? null : reader.GetString(5),
                ServerVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = Read(reader.GetString(7)),
                LastScanAt = reader.IsDBNull(8) ? null : Read(reader.GetString(8)),
                LastError = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return targets;
    }

    /// <summary>Adds a target, or updates the connection string of one already known by that id.</summary>
    public async Task UpsertTargetAsync(StoredTarget target, CancellationToken ct = default)
    {
        await WriteAsync(async connection =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO targets (id, name, provider, connection_string, source, created_at)
                VALUES ($id, $name, $provider, $cs, $source, $now)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    provider = excluded.provider,
                    connection_string = excluded.connection_string,
                    source = excluded.source
                """, ct, null,
                ("$id", target.Id), ("$name", target.Name), ("$provider", target.Provider),
                ("$cs", target.ConnectionString), ("$source", target.Source), ("$now", Now()));
        }, ct);
    }

    public async Task RemoveTargetAsync(string id, CancellationToken ct = default) =>
        await WriteAsync(async connection =>
            await ExecuteAsync(connection, "DELETE FROM targets WHERE id = $id", ct, null, ("$id", id)), ct);

    /// <summary>Records that a scan could not be run at all — a wrong password, a dead host.</summary>
    public async Task RecordScanFailureAsync(string targetId, string error, CancellationToken ct = default) =>
        await WriteAsync(async connection =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO scans (target_id, started_at, duration_ms, succeeded, error)
                VALUES ($id, $now, 0, 0, $error)
                """, ct, null, ("$id", targetId), ("$now", Now()), ("$error", error));

            await ExecuteAsync(connection,
                "UPDATE targets SET last_scan_at = $now, last_error = $error WHERE id = $id", ct, null,
                ("$id", targetId), ("$now", Now()), ("$error", error));
        }, ct);

    // ---- scans -------------------------------------------------------------------------

    /// <summary>
    /// Persists a whole report and updates the lifetime of every finding in it: a finding already
    /// known keeps its first-seen date and gets a new last-seen; a finding that stopped appearing
    /// simply stops being updated, and the UI shows how long ago that was.
    /// </summary>
    public async Task<long> SaveScanAsync(string targetId, ScanReport report, CancellationToken ct = default)
    {
        long scanId = 0;

        await WriteAsync(async connection =>
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            var startedAt = Write(report.StartedAt);

            scanId = Convert.ToInt64(await ScalarAsync(connection, """
                INSERT INTO scans (target_id, started_at, duration_ms, succeeded, server_version, database_name)
                VALUES ($target, $started, $duration, 1, $version, $database)
                RETURNING id
                """, ct, tx,
                ("$target", targetId), ("$started", startedAt),
                ("$duration", report.Duration.TotalMilliseconds),
                ("$version", report.Target.ServerVersion), ("$database", report.Target.DatabaseName)));

            foreach (var check in report.Checks)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO check_results (scan_id, check_id, title, category, succeeded, duration_ms, error)
                    VALUES ($scan, $check, $title, $category, $ok, $duration, $error)
                    """, ct, tx,
                    ("$scan", scanId), ("$check", check.CheckId), ("$title", check.Title),
                    ("$category", check.Category), ("$ok", check.Succeeded ? 1 : 0),
                    ("$duration", check.Duration.TotalMilliseconds), ("$error", check.Error));

                foreach (var finding in check.Findings)
                {
                    var identity = FindingIdentity.For(finding);

                    var findingId = Convert.ToInt64(await ScalarAsync(connection, """
                        INSERT INTO findings (scan_id, target_id, check_id, identity_key, severity,
                                              title, detail, recommendation, evidence, created_at)
                        VALUES ($scan, $target, $check, $identity, $severity, $title, $detail,
                                $recommendation, $evidence, $now)
                        RETURNING id
                        """, ct, tx,
                        ("$scan", scanId), ("$target", targetId), ("$check", finding.CheckId),
                        ("$identity", identity), ("$severity", (int)finding.Severity),
                        ("$title", finding.Title), ("$detail", finding.Detail),
                        ("$recommendation", finding.Recommendation),
                        ("$evidence", JsonSerializer.Serialize(finding.Evidence, EvidenceJson)),
                        ("$now", startedAt)));

                    foreach (var subject in finding.Subjects)
                    {
                        await ExecuteAsync(connection, """
                            INSERT OR IGNORE INTO finding_subjects (finding_id, kind, key)
                            VALUES ($finding, $kind, $key)
                            """, ct, tx,
                            ("$finding", findingId), ("$kind", subject.Kind), ("$key", subject.Key));
                    }

                    // First-seen is set once and never moved; a finding that was marked fixed and
                    // came back reopens itself rather than staying quietly closed.
                    await ExecuteAsync(connection, """
                        INSERT INTO finding_state (target_id, identity_key, check_id, category, severity,
                                                   title, subjects, first_seen, last_seen, seen_count,
                                                   status, last_finding_id)
                        VALUES ($target, $identity, $check, $category, $severity, $title, $subjects,
                                $now, $now, 1, 'open', $finding)
                        ON CONFLICT(target_id, identity_key) DO UPDATE SET
                            last_seen = excluded.last_seen,
                            seen_count = finding_state.seen_count + 1,
                            severity = excluded.severity,
                            title = excluded.title,
                            category = excluded.category,
                            last_finding_id = excluded.last_finding_id,
                            status = CASE WHEN finding_state.status = 'fixed' THEN 'open'
                                          ELSE finding_state.status END
                        """, ct, tx,
                        ("$target", targetId), ("$identity", identity), ("$check", finding.CheckId),
                        ("$category", check.Category), ("$severity", (int)finding.Severity),
                        ("$title", finding.Title),
                        ("$subjects", string.Join(' ', finding.Subjects.Select(s => s.ToString()))),
                        ("$now", startedAt), ("$finding", findingId));
                }
            }

            await ExecuteAsync(connection, """
                UPDATE targets
                SET last_scan_at = $now, last_error = NULL, server_version = $version, database_name = $database
                WHERE id = $id
                """, ct, tx,
                ("$id", targetId), ("$now", startedAt),
                ("$version", report.Target.ServerVersion), ("$database", report.Target.DatabaseName));

            await tx.CommitAsync(ct);
        }, ct);

        return scanId;
    }

    public async Task<StoredScan?> LatestScanAsync(string targetId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.target_id, s.started_at, s.duration_ms, s.succeeded, s.error,
                   s.server_version, s.database_name,
                   (SELECT count(*) FROM check_results c WHERE c.scan_id = s.id),
                   (SELECT count(*) FROM check_results c WHERE c.scan_id = s.id AND c.succeeded = 0),
                   (SELECT count(*) FROM findings f WHERE f.scan_id = s.id)
            FROM scans s
            WHERE s.target_id = $target
            ORDER BY s.started_at DESC, s.id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$target", targetId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new StoredScan
        {
            Id = reader.GetInt64(0),
            TargetId = reader.GetString(1),
            StartedAt = Read(reader.GetString(2)),
            Duration = TimeSpan.FromMilliseconds(reader.GetDouble(3)),
            Succeeded = reader.GetInt64(4) == 1,
            Error = reader.IsDBNull(5) ? null : reader.GetString(5),
            ServerVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
            DatabaseName = reader.IsDBNull(7) ? null : reader.GetString(7),
            CheckCount = reader.GetInt32(8),
            FailedCheckCount = reader.GetInt32(9),
            FindingCount = reader.GetInt32(10),
        };
    }

    public async Task<int> ScanCountAsync(string targetId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return Convert.ToInt32(await ScalarAsync(connection,
            "SELECT count(*) FROM scans WHERE target_id = $target", ct, null, ("$target", targetId)));
    }

    // ---- findings ----------------------------------------------------------------------

    /// <summary>
    /// The findings of the newest scan, each with the first and last time Momus saw it.
    /// This join is the difference between the CLI and the server.
    /// </summary>
    public async Task<IReadOnlyList<StoredFinding>> LatestFindingsAsync(string targetId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.id, f.check_id, c.category, f.severity, f.title, f.detail, f.recommendation, f.evidence,
                   st.first_seen, st.last_seen, st.seen_count, st.status,
                   (SELECT group_concat(fs.kind || ':' || fs.key, ' ')
                    FROM finding_subjects fs WHERE fs.finding_id = f.id)
            FROM findings f
            JOIN check_results c ON c.scan_id = f.scan_id AND c.check_id = f.check_id
            JOIN finding_state st ON st.target_id = f.target_id AND st.identity_key = f.identity_key
            WHERE f.scan_id = (
                SELECT id FROM scans
                WHERE target_id = $target AND succeeded = 1
                ORDER BY started_at DESC, id DESC LIMIT 1)
            ORDER BY f.severity DESC, st.first_seen DESC
            """;
        cmd.Parameters.AddWithValue("$target", targetId);

        var findings = new List<StoredFinding>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            findings.Add(new StoredFinding
            {
                Id = reader.GetInt64(0),
                CheckId = reader.GetString(1),
                Category = reader.GetString(2),
                Severity = (Severity)reader.GetInt32(3),
                Title = reader.GetString(4),
                Detail = reader.GetString(5),
                Recommendation = reader.IsDBNull(6) ? null : reader.GetString(6),
                EvidenceJson = reader.GetString(7),
                FirstSeen = Read(reader.GetString(8)),
                LastSeen = Read(reader.GetString(9)),
                SeenCount = reader.GetInt32(10),
                Status = reader.GetString(11),
                Subjects = reader.IsDBNull(12)
                    ? []
                    : reader.GetString(12).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(Subject.Parse).ToList(),
            });
        }
        return findings;
    }

    /// <summary>Checks that could not run in the newest scan. A silent permission error is a lie.</summary>
    public async Task<IReadOnlyList<StoredCheckFailure>> LatestCheckFailuresAsync(
        string targetId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT check_id, title, COALESCE(error, '')
            FROM check_results
            WHERE succeeded = 0 AND scan_id = (
                SELECT id FROM scans WHERE target_id = $target AND succeeded = 1
                ORDER BY started_at DESC, id DESC LIMIT 1)
            ORDER BY check_id
            """;
        cmd.Parameters.AddWithValue("$target", targetId);

        var failures = new List<StoredCheckFailure>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            failures.Add(new StoredCheckFailure(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return failures;
    }

    /// <summary>
    /// Returns the pages that deleted rows left behind. SQLite reuses freed pages but never gives
    /// them back on its own, so without this the file keeps the high-water mark of a busy week.
    /// </summary>
    public async Task VacuumAsync(CancellationToken ct = default) =>
        await WriteAsync(async connection => await ExecuteAsync(connection, "VACUUM", ct), ct);

    /// <summary>
    /// How much disk the store is actually using, for the diagnostics page and the vacuum log
    /// line. The write-ahead log counts: in WAL mode recent writes live there, so the main file
    /// alone can read as almost empty on a server that has been busy all morning.
    /// </summary>
    public long FileSizeBytes
    {
        get
        {
            long total = 0;

            foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path)) total += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    // An in-memory store has no files at all, and a locked one is not worth a page.
                }
            }

            return total;
        }
    }

    // ---- plumbing ----------------------------------------------------------------------

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private async Task WriteAsync(Func<SqliteConnection, Task> work, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await work(connection);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct,
        System.Data.Common.DbTransaction? tx = null, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(connection, sql, tx, parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct,
        System.Data.Common.DbTransaction? tx = null, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(connection, sql, tx, parameters);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql,
        System.Data.Common.DbTransaction? tx, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx as SqliteTransaction;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    /// <summary>ISO-8601 UTC, so string ordering in SQLite is chronological ordering.</summary>
    private static string Now() => Write(DateTimeOffset.UtcNow);

    private static string Write(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

    private static DateTimeOffset Read(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal |
                                          System.Globalization.DateTimeStyles.AdjustToUniversal);

    public async ValueTask DisposeAsync()
    {
        if (_keepAlive is not null) await _keepAlive.DisposeAsync();
        _writeLock.Dispose();
    }
}
