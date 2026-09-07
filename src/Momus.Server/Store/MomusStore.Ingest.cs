using Microsoft.Data.Sqlite;
using Momus.Core.Ingest;

namespace Momus.Server.Store;

/// <summary>
/// The application half of the store. A batch arrives every few seconds per process; it is one
/// insert of a window plus its rows, and it never touches the scan tables — the two halves meet
/// only when something reads them, on the fingerprint.
/// </summary>
public sealed partial class MomusStore
{
    /// <summary>Raw windows older than this are folded into hourly rows.</summary>
    public static readonly TimeSpan RawRetention = TimeSpan.FromHours(24);

    /// <summary>Hourly rows older than this are deleted. Both numbers are the free tier's (D10).</summary>
    public static readonly TimeSpan RollupRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Stores one posted batch and returns the window id. The app, the instance and the version
    /// are upserted first, because the first sighting of a version is the deploy marker M3 needs.
    /// </summary>
    public async Task<long> SaveIngestBatchAsync(IngestBatch batch, CancellationToken ct = default)
    {
        var appId = AppId(batch.App.Name);
        long windowId = 0;

        await WriteAsync(async connection =>
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            var now = Now();
            var version = batch.App.Version ?? "";

            await ExecuteAsync(connection, """
                INSERT INTO apps (id, name, environment, last_version, first_seen, last_seen)
                VALUES ($id, $name, $env, $version, $now, $now)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    environment = COALESCE(excluded.environment, apps.environment),
                    last_version = COALESCE(excluded.last_version, apps.last_version),
                    last_seen = excluded.last_seen
                """, ct, tx,
                ("$id", appId), ("$name", batch.App.Name), ("$env", batch.App.Environment),
                ("$version", batch.App.Version), ("$now", now));

            if (batch.App.Instance is { Length: > 0 } instance)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO app_instances (app_id, instance, version, first_seen, last_seen)
                    VALUES ($app, $instance, $version, $now, $now)
                    ON CONFLICT(app_id, instance, version) DO UPDATE SET last_seen = excluded.last_seen
                    """, ct, tx,
                    ("$app", appId), ("$instance", instance), ("$version", version), ("$now", now));
            }

            windowId = Convert.ToInt64(await ScalarAsync(connection, """
                INSERT INTO windows (app_id, instance, version, kind, from_at, to_at, overflow,
                                     received_at, client_version, dropped)
                VALUES ($app, $instance, $version, 'raw', $from, $to, $overflow, $now,
                        $client, $dropped)
                RETURNING id
                """, ct, tx,
                ("$app", appId), ("$instance", batch.App.Instance), ("$version", batch.App.Version),
                ("$from", Write(batch.Window.From)), ("$to", Write(batch.Window.To)),
                ("$overflow", batch.Overflow), ("$now", now),
                ("$client", batch.Client?.Version), ("$dropped", batch.Client?.Dropped ?? 0)));

            foreach (var operation in batch.Operations)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO operation_stats (window_id, name, kind, calls, duration_sum, duration_max,
                                                 query_sum, query_max, db_ms)
                    VALUES ($window, $name, $kind, $calls, $sum, $max, $qsum, $qmax, $db)
                    """, ct, tx,
                    ("$window", windowId), ("$name", operation.Name), ("$kind", operation.Kind),
                    ("$calls", operation.Count), ("$sum", operation.DurationMs.Sum),
                    ("$max", operation.DurationMs.Max), ("$qsum", operation.Queries.Sum),
                    ("$qmax", operation.Queries.Max), ("$db", operation.DbMs.Sum));
            }

            foreach (var query in batch.Queries)
            {
                var targetId = query.Target ?? "";

                await ExecuteAsync(connection, """
                    INSERT INTO query_stats (window_id, fingerprint, target_id, operation, call_site, calls,
                                             duration_sum, duration_max, hist, rows_read, max_repeats, errors)
                    VALUES ($window, $key, $target, $operation, $site, $calls, $sum, $max, $hist,
                            $rows, $repeats, $errors)
                    """, ct, tx,
                    ("$window", windowId), ("$key", query.Key), ("$target", targetId),
                    ("$operation", query.Operation), ("$site", query.CallSite ?? ""),
                    ("$calls", query.Count), ("$sum", query.DurationMs.Sum), ("$max", query.DurationMs.Max),
                    ("$hist", Histogram(query.DurationMs.Hist)), ("$rows", query.Rows),
                    ("$repeats", query.MaxRepeatsPerOperation), ("$errors", query.Errors));

                // The text is kept once per key, not once per window: it is the same statement.
                await ExecuteAsync(connection, """
                    INSERT INTO query_texts (fingerprint, target_id, sample, first_seen, last_seen)
                    VALUES ($key, $target, $sample, $now, $now)
                    ON CONFLICT(fingerprint, target_id) DO UPDATE SET last_seen = excluded.last_seen
                    """, ct, tx,
                    ("$key", query.Key), ("$target", targetId), ("$sample", query.Sample), ("$now", now));
            }

            await tx.CommitAsync(ct);
        }, ct);

        return windowId;
    }

    /// <summary>Every application that has ever reported, newest sighting first.</summary>
    public async Task<IReadOnlyList<StoredApp>> AppsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.name, a.environment, a.last_version, a.first_seen, a.last_seen,
                   (SELECT count(DISTINCT i.instance) FROM app_instances i WHERE i.app_id = a.id)
            FROM apps a ORDER BY a.last_seen DESC
            """;

        var apps = new List<StoredApp>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            apps.Add(new StoredApp
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Environment = reader.IsDBNull(2) ? null : reader.GetString(2),
                Version = reader.IsDBNull(3) ? null : reader.GetString(3),
                FirstSeen = Read(reader.GetString(4)),
                LastSeen = Read(reader.GetString(5)),
                InstanceCount = reader.GetInt32(6),
            });
        }
        return apps;
    }

    /// <summary>
    /// The newest window, for the "the client stopped sending" pill. That is the first-run
    /// problem, so it has to be visible rather than inferred from an empty table.
    /// </summary>
    public async Task<StoredWindow?> LatestWindowAsync(string? appId = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, app_id, from_at, to_at, version, overflow, received_at, client_version, dropped
            FROM windows
            WHERE ($app IS NULL OR app_id = $app)
            ORDER BY received_at DESC, id DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$app", (object?)appId ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new StoredWindow
        {
            Id = reader.GetInt64(0),
            AppId = reader.GetString(1),
            From = Read(reader.GetString(2)),
            To = Read(reader.GetString(3)),
            Version = reader.IsDBNull(4) ? null : reader.GetString(4),
            Overflow = reader.GetInt64(5),
            ReceivedAt = Read(reader.GetString(6)),
            ClientVersion = reader.IsDBNull(7) ? null : reader.GetString(7),
            Dropped = reader.GetInt64(8),
        };
    }

    /// <summary>
    /// One row per statement per database since <paramref name="since"/>, carrying the operation
    /// that runs it most often. This is the app side of the Queries tab; the database side is a
    /// separate read joined on the fingerprint.
    /// </summary>
    public async Task<IReadOnlyList<AppQueryStat>> AppQueryStatsAsync(
        DateTimeOffset since, string? appId = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var samples = await SamplesAsync(connection, ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT q.fingerprint, q.target_id, q.operation, q.call_site,
                   SUM(q.calls), SUM(q.duration_sum), MAX(q.duration_max),
                   SUM(q.rows_read), MAX(q.max_repeats), SUM(q.errors),
                   MIN(w.from_at), MAX(w.to_at)
            FROM query_stats q
            JOIN windows w ON w.id = q.window_id
            WHERE w.to_at >= $since AND ($app IS NULL OR w.app_id = $app)
            GROUP BY q.fingerprint, q.target_id, q.operation, q.call_site
            """;
        cmd.Parameters.AddWithValue("$since", Write(since));
        cmd.Parameters.AddWithValue("$app", (object?)appId ?? DBNull.Value);

        // Folded here rather than in SQL: picking the busiest operation for a statement is one
        // line of C# and three of window functions.
        var byKey = new Dictionary<(string, string), AppQueryStat>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fingerprint = reader.GetString(0);
            var targetId = reader.GetString(1);
            var row = new AppQueryStat
            {
                Fingerprint = fingerprint,
                TargetId = targetId,
                Operation = reader.GetString(2),
                OperationCount = 1,
                CallSite = reader.GetString(3) is { Length: > 0 } site ? site : null,
                Sample = samples.GetValueOrDefault((fingerprint, targetId)) ??
                         samples.GetValueOrDefault((fingerprint, "")),
                Calls = reader.GetInt64(4),
                DurationSum = reader.GetDouble(5),
                DurationMax = reader.GetDouble(6),
                Rows = reader.GetInt64(7),
                MaxRepeats = reader.GetInt32(8),
                Errors = reader.GetInt64(9),
                From = Read(reader.GetString(10)),
                To = Read(reader.GetString(11)),
            };

            var id = (fingerprint, targetId);
            byKey[id] = byKey.TryGetValue(id, out var existing) ? Merge(existing, row) : row;
        }

        return byKey.Values.OrderByDescending(q => q.DurationSum).ToList();
    }

    /// <summary>Keeps the busiest operation's name and call site, and sums everything else.</summary>
    private static AppQueryStat Merge(AppQueryStat a, AppQueryStat b)
    {
        var busiest = b.Calls > a.Calls ? b : a;
        return busiest with
        {
            OperationCount = a.OperationCount + b.OperationCount,
            Calls = a.Calls + b.Calls,
            DurationSum = a.DurationSum + b.DurationSum,
            DurationMax = Math.Max(a.DurationMax, b.DurationMax),
            Rows = a.Rows + b.Rows,
            MaxRepeats = Math.Max(a.MaxRepeats, b.MaxRepeats),
            Errors = a.Errors + b.Errors,
            From = a.From < b.From ? a.From : b.From,
            To = a.To > b.To ? a.To : b.To,
        };
    }

    private static async Task<Dictionary<(string, string), string>> SamplesAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT fingerprint, target_id, sample FROM query_texts";

        var samples = new Dictionary<(string, string), string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }
        return samples;
    }

    /// <summary>One row per operation since <paramref name="since"/>, busiest by total time first.</summary>
    public async Task<IReadOnlyList<AppOperationStat>> AppOperationStatsAsync(
        DateTimeOffset since, string? appId = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT o.name, o.kind, SUM(o.calls), SUM(o.duration_sum), MAX(o.duration_max),
                   SUM(o.query_sum), MAX(o.query_max), SUM(o.db_ms)
            FROM operation_stats o
            JOIN windows w ON w.id = o.window_id
            WHERE w.to_at >= $since AND ($app IS NULL OR w.app_id = $app)
            GROUP BY o.name, o.kind
            ORDER BY SUM(o.duration_sum) DESC
            """;
        cmd.Parameters.AddWithValue("$since", Write(since));
        cmd.Parameters.AddWithValue("$app", (object?)appId ?? DBNull.Value);

        var operations = new List<AppOperationStat>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            operations.Add(new AppOperationStat
            {
                Name = reader.GetString(0),
                Kind = reader.GetString(1),
                Calls = reader.GetInt64(2),
                DurationSum = reader.GetDouble(3),
                DurationMax = reader.GetDouble(4),
                QuerySum = reader.GetInt64(5),
                QueryMax = reader.GetInt64(6),
                DbMs = reader.GetDouble(7),
            });
        }
        return operations;
    }

    /// <summary>
    /// Counts that say whether the application half is healthy, rather than whether it exists.
    /// Dropped operations and unattributed executions are the two ways the client loses data on
    /// purpose, and both are invisible from anywhere but here.
    /// </summary>
    public async Task<IngestHealth> IngestHealthAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT count(*) FROM windows WHERE kind = 'raw'),
              (SELECT count(*) FROM windows WHERE kind = 'hourly'),
              (SELECT count(*) FROM windows WHERE to_at >= $since),
              (SELECT COALESCE(SUM(overflow), 0) FROM windows WHERE to_at >= $since),
              (SELECT COALESCE(SUM(dropped), 0) FROM windows WHERE to_at >= $since),
              (SELECT client_version FROM windows
                WHERE client_version IS NOT NULL ORDER BY received_at DESC LIMIT 1),
              (SELECT count(*) FROM query_stats q JOIN windows w ON w.id = q.window_id
                WHERE w.to_at >= $since),
              (SELECT count(DISTINCT q.fingerprint) FROM query_stats q JOIN windows w ON w.id = q.window_id
                WHERE w.to_at >= $since),
              (SELECT count(DISTINCT q.fingerprint) FROM query_stats q JOIN windows w ON w.id = q.window_id
                WHERE w.to_at >= $since AND q.call_site <> '')
            """;
        cmd.Parameters.AddWithValue("$since", Write(since));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new IngestHealth();

        return new IngestHealth
        {
            RawWindows = reader.GetInt32(0),
            HourlyWindows = reader.GetInt32(1),
            WindowsSince = reader.GetInt32(2),
            Overflow = reader.GetInt64(3),
            Dropped = reader.GetInt64(4),
            ClientVersion = reader.IsDBNull(5) ? null : reader.GetString(5),
            StatementRows = reader.GetInt64(6),
            DistinctStatements = reader.GetInt32(7),
            StatementsWithCallSite = reader.GetInt32(8),
        };
    }

    /// <summary>Which schema scripts have been applied. The first line of any bug report.</summary>
    public async Task<long> SchemaVersionAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return Convert.ToInt64(await ScalarAsync(connection,
            "SELECT COALESCE(MAX(version), 0) FROM schema_version", ct));
    }

    // ---- retention ---------------------------------------------------------------------

    /// <summary>
    /// Folds every raw window older than <paramref name="olderThan"/> into one hourly window per
    /// app and version, then deletes the raw ones. Histogram buckets are summed in SQL through
    /// JSON1, so a day of five-second windows never has to be loaded into memory to be folded.
    /// </summary>
    /// <returns>How many raw windows were folded.</returns>
    public async Task<int> RollupAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var folded = 0;

        await WriteAsync(async connection =>
        {
            var groups = new List<(string App, string? Version, string Hour)>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT app_id, version, substr(to_at, 1, 13)
                    FROM windows
                    WHERE kind = 'raw' AND to_at < $cutoff
                    GROUP BY app_id, version, substr(to_at, 1, 13)
                    ORDER BY substr(to_at, 1, 13)
                    """;
                cmd.Parameters.AddWithValue("$cutoff", Write(olderThan));

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    groups.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetString(2)));
                }
            }

            foreach (var (app, version, hour) in groups)
            {
                await using var tx = await connection.BeginTransactionAsync(ct);
                (string, object?)[] scope =
                [
                    ("$app", app), ("$version", version), ("$hour", hour), ("$cutoff", Write(olderThan)),
                ];

                const string Match = """
                    w.kind = 'raw' AND w.to_at < $cutoff AND w.app_id = $app
                    AND substr(w.to_at, 1, 13) = $hour
                    AND ((w.version IS NULL AND $version IS NULL) OR w.version = $version)
                    """;

                var windowId = Convert.ToInt64(await ScalarAsync(connection, $"""
                    INSERT INTO windows (app_id, instance, version, kind, from_at, to_at, overflow,
                                         received_at, client_version, dropped)
                    SELECT $app, NULL, $version, 'hourly', MIN(w.from_at), MAX(w.to_at),
                           SUM(w.overflow), $now, MAX(w.client_version), SUM(w.dropped)
                    FROM windows w WHERE {Match}
                    RETURNING id
                    """, ct, tx, [.. scope, ("$now", Now())]));

                // Eight buckets, summed one by one: SQLite has no array type and this is the one
                // place the shape of a timing has to survive being folded.
                var buckets = string.Join(" || ',' || ",
                    Enumerable.Range(0, Timing.Buckets).Select(i => $"SUM(json_extract(q.hist, '$[{i}]'))"));

                await ExecuteAsync(connection, $"""
                    INSERT INTO query_stats (window_id, fingerprint, target_id, operation, call_site, calls,
                                             duration_sum, duration_max, hist, rows_read, max_repeats, errors)
                    SELECT $window, q.fingerprint, q.target_id, q.operation, q.call_site,
                           SUM(q.calls), SUM(q.duration_sum), MAX(q.duration_max),
                           '[' || {buckets} || ']',
                           SUM(q.rows_read), MAX(q.max_repeats), SUM(q.errors)
                    FROM query_stats q JOIN windows w ON w.id = q.window_id
                    WHERE {Match}
                    GROUP BY q.fingerprint, q.target_id, q.operation, q.call_site
                    """, ct, tx, [.. scope, ("$window", windowId)]);

                await ExecuteAsync(connection, $"""
                    INSERT INTO operation_stats (window_id, name, kind, calls, duration_sum, duration_max,
                                                 query_sum, query_max, db_ms)
                    SELECT $window, o.name, o.kind, SUM(o.calls), SUM(o.duration_sum), MAX(o.duration_max),
                           SUM(o.query_sum), MAX(o.query_max), SUM(o.db_ms)
                    FROM operation_stats o JOIN windows w ON w.id = o.window_id
                    WHERE {Match}
                    GROUP BY o.name, o.kind
                    """, ct, tx, [.. scope, ("$window", windowId)]);

                folded += Convert.ToInt32(await ScalarAsync(connection, $"""
                    SELECT count(*) FROM windows w WHERE {Match}
                    """, ct, tx, scope));

                // The rows go with the window: every stats table cascades from it.
                await ExecuteAsync(connection, $"""
                    DELETE FROM windows WHERE id IN (SELECT w.id FROM windows w WHERE {Match})
                    """, ct, tx, scope);

                await tx.CommitAsync(ct);
            }
        }, ct);

        return folded;
    }

    /// <summary>Drops hourly windows past the retention limit, and query texts nothing points at.</summary>
    public async Task<int> TrimAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var removed = 0;

        await WriteAsync(async connection =>
        {
            removed = Convert.ToInt32(await ScalarAsync(connection, """
                SELECT count(*) FROM windows WHERE kind = 'hourly' AND to_at < $cutoff
                """, ct, null, ("$cutoff", Write(olderThan))));

            await ExecuteAsync(connection,
                "DELETE FROM windows WHERE kind = 'hourly' AND to_at < $cutoff", ct, null,
                ("$cutoff", Write(olderThan)));

            await ExecuteAsync(connection, """
                DELETE FROM query_texts
                WHERE NOT EXISTS (SELECT 1 FROM query_stats q WHERE q.fingerprint = query_texts.fingerprint)
                """, ct);
        }, ct);

        return removed;
    }

    /// <summary>Stable id for an app name, so a restart reports as the same application.</summary>
    public static string AppId(string name) => TargetSpec.Slug(name);

    private static string Histogram(IReadOnlyList<long> buckets)
    {
        var values = new long[Timing.Buckets];
        for (var i = 0; i < values.Length && i < buckets.Count; i++) values[i] = buckets[i];
        return "[" + string.Join(',', values) + "]";
    }
}
