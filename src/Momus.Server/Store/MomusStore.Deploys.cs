using System.Text.Json;
using Momus.Core.Ingest;
using Momus.Core.Insights;

namespace Momus.Server.Store;

/// <summary>
/// The M3 reads: what an application was running and when it changed, and the two signals that
/// are about how the application uses a database rather than what it asks it.
/// </summary>
public sealed partial class MomusStore
{
    /// <summary>
    /// Every version an application has been seen running, oldest first. A deploy is not something
    /// Momus is told about — it is a version turning up for the first time, which is a date the
    /// store already has and nothing else has to be configured to produce.
    /// </summary>
    public async Task<IReadOnlyList<DeployView>> DeploysAsync(
        string? appId = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT version, MIN(first_seen), MAX(last_seen), count(DISTINCT instance)
            FROM app_instances
            WHERE version <> '' AND ($app IS NULL OR app_id = $app)
            GROUP BY version
            ORDER BY MIN(first_seen)
            """;
        cmd.Parameters.AddWithValue("$app", (object?)appId ?? DBNull.Value);

        var deploys = new List<DeployView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            deploys.Add(new DeployView(
                reader.GetString(0), Read(reader.GetString(1)), Read(reader.GetString(2)),
                reader.GetInt32(3)));
        }
        return deploys;
    }

    /// <summary>One row per statement per application version, over everything the store still holds.</summary>
    public async Task<IReadOnlyList<VersionStatView>> VersionStatsAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT q.fingerprint, w.version, SUM(q.calls), SUM(q.duration_sum),
                   MIN(q.operation), MIN(NULLIF(q.call_site, ''))
            FROM query_stats q
            JOIN windows w ON w.id = q.window_id
            WHERE w.to_at >= $since AND w.version IS NOT NULL AND w.version <> ''
            GROUP BY q.fingerprint, w.version
            HAVING SUM(q.calls) > 0
            """;
        cmd.Parameters.AddWithValue("$since", Write(since));

        var stats = new List<VersionStatView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            stats.Add(new VersionStatView
            {
                Fingerprint = reader.GetString(0),
                Version = reader.GetString(1),
                Calls = reader.GetInt64(2),
                TotalMs = reader.GetDouble(3),
                Operation = reader.IsDBNull(4) ? null : reader.GetString(4),
                CallSite = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }
        return stats;
    }

    public async Task<IReadOnlyList<TransactionStatView>> TransactionStatsAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.operation, t.call_site, SUM(t.txn_count), SUM(t.open_sum), MAX(t.open_max),
                   SUM(t.db_sum), group_concat(t.open_hist, '|')
            FROM transaction_stats t
            JOIN windows w ON w.id = t.window_id
            WHERE w.to_at >= $since
            GROUP BY t.operation, t.call_site
            """;
        cmd.Parameters.AddWithValue("$since", Write(since));

        var stats = new List<TransactionStatView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            stats.Add(new TransactionStatView
            {
                Operation = reader.GetString(0),
                CallSite = reader.GetString(1) is { Length: > 0 } site ? site : null,
                Count = reader.GetInt64(2),
                OpenMs = new Timing(reader.GetDouble(3), reader.GetDouble(4),
                    SumHistograms(reader.IsDBNull(6) ? null : reader.GetString(6))),
                DbMsSum = reader.GetDouble(5),
            });
        }
        return stats;
    }

    public async Task<IReadOnlyList<PoolStatView>> PoolStatsAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.operation, SUM(p.waits), SUM(p.wait_sum), MAX(p.wait_max),
                   group_concat(p.wait_hist, '|')
            FROM pool_stats p
            JOIN windows w ON w.id = p.window_id
            WHERE w.to_at >= $since
            GROUP BY p.operation
            """;
        cmd.Parameters.AddWithValue("$since", Write(since));

        var stats = new List<PoolStatView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            stats.Add(new PoolStatView
            {
                Operation = reader.GetString(0),
                Waits = reader.GetInt64(1),
                WaitMs = new Timing(reader.GetDouble(2), reader.GetDouble(3),
                    SumHistograms(reader.IsDBNull(4) ? null : reader.GetString(4))),
            });
        }
        return stats;
    }

    /// <summary>
    /// Adds up the per-window histograms SQLite concatenated for us. Summing eight buckets in SQL
    /// takes eight <c>json_extract</c> calls and is worth it in the rollup, which runs over a day
    /// of rows; here the group is small and the loop is clearer.
    /// </summary>
    private static long[] SumHistograms(string? concatenated)
    {
        var total = new long[Timing.Buckets];
        if (string.IsNullOrEmpty(concatenated)) return total;

        foreach (var part in concatenated.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            long[]? buckets;
            try
            {
                buckets = JsonSerializer.Deserialize<long[]>(part);
            }
            catch (JsonException)
            {
                continue;
            }

            if (buckets is null) continue;
            for (var i = 0; i < total.Length && i < buckets.Length; i++) total[i] += buckets[i];
        }

        return total;
    }
}
