using System.Data.Common;
using Momus.Core;

namespace Momus.Postgres.Checks;

public sealed class SeqScanHeavyTablesCheck : IDiagnosticCheck
{
    public string Id => "pg.seq_scan_heavy_tables";
    public string Title => "Tables dominated by sequential scans";
    public string Category => "indexes";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection, """
            SELECT schemaname, relname, seq_scan, COALESCE(idx_scan, 0) AS idx_scan,
                   n_live_tup, pg_total_relation_size(relid) AS total_bytes
            FROM pg_stat_user_tables
            WHERE seq_scan > 50
              AND n_live_tup > 10000
              AND seq_scan > COALESCE(idx_scan, 0)
            ORDER BY seq_scan * n_live_tup DESC
            LIMIT 10
            """, ct);

        return rows.Select(row =>
        {
            var table = $"{Db.ToStr(row["schemaname"])}.{Db.ToStr(row["relname"])}";
            var seqScan = Db.ToLong(row["seq_scan"]);
            var idxScan = Db.ToLong(row["idx_scan"]);
            var liveTup = Db.ToLong(row["n_live_tup"]);
            return new Finding
            {
                CheckId = Id,
                Severity = liveTup > 100_000 ? Severity.High : Severity.Medium,
                Title = $"Table {table} is read mostly by sequential scans",
                Detail = $"{table} has ~{liveTup:N0} rows and was sequentially scanned {seqScan:N0} times " +
                         $"vs {idxScan:N0} index scans. Every seq scan reads the whole table.",
                Recommendation = "Inspect the queries hitting this table (see top queries) and add an index " +
                                 "matching their WHERE/JOIN columns, or confirm the scans are intentional (batch jobs, analytics).",
                Evidence = new Dictionary<string, object?>
                {
                    ["table"] = table,
                    ["seq_scan"] = seqScan,
                    ["idx_scan"] = idxScan,
                    ["n_live_tup"] = liveTup,
                    ["total_bytes"] = Db.ToLong(row["total_bytes"]),
                },
            };
        }).ToList();
    }
}
