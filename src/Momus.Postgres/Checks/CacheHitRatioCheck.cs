using System.Data.Common;
using Momus.Core;

namespace Momus.Postgres.Checks;

public sealed class CacheHitRatioCheck : IDiagnosticCheck
{
    public string Id => "pg.cache_hit_ratio";
    public string Title => "Buffer cache hit ratio";
    public string Category => "memory";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection, """
            SELECT blks_hit, blks_read
            FROM pg_stat_database
            WHERE datname = current_database()
            """, ct);
        if (rows.Count == 0) return [];

        var hit = Db.ToLong(rows[0]["blks_hit"]);
        var read = Db.ToLong(rows[0]["blks_read"]);
        var total = hit + read;
        var ratio = total == 0 ? 1.0 : (double)hit / total;

        var severity = PostgresThresholds.CacheHitSeverity(ratio, total);
        if (severity is null) return [];

        return
        [
            new Finding
            {
                CheckId = Id,
                Severity = severity.Value,
                Title = $"Buffer cache hit ratio is {ratio:P1}",
                Detail = $"Since statistics were last reset, {read:N0} of {total:N0} block requests " +
                         "were read from disk instead of shared buffers. Healthy OLTP workloads are typically above 99%.",
                Recommendation = "Check shared_buffers sizing and look for large sequential scans evicting hot data. " +
                                 "The seq-scan and top-query checks in this report usually point at the culprits.",
                Evidence = new Dictionary<string, object?>
                {
                    ["blks_hit"] = hit,
                    ["blks_read"] = read,
                    ["ratio"] = Math.Round(ratio, 4),
                },
            },
        ];
    }
}
