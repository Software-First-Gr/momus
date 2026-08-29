using System.Data.Common;
using Momus.Core;

namespace Momus.Postgres.Checks;

public sealed class UnusedIndexesCheck : IDiagnosticCheck
{
    public string Id => "pg.unused_indexes";
    public string Title => "Unused indexes";
    public string Category => "indexes";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection, """
            SELECT s.schemaname, s.relname AS table_name, s.indexrelname AS index_name,
                   pg_relation_size(s.indexrelid) AS index_bytes
            FROM pg_stat_user_indexes s
            JOIN pg_index i ON i.indexrelid = s.indexrelid
            WHERE s.idx_scan = 0
              AND NOT i.indisunique
              AND NOT i.indisprimary
              AND pg_relation_size(s.indexrelid) > 1024 * 1024
            ORDER BY index_bytes DESC
            LIMIT 20
            """, ct);

        return rows.Select(row =>
        {
            var index = Db.ToStr(row["index_name"]);
            var table = $"{Db.ToStr(row["schemaname"])}.{Db.ToStr(row["table_name"])}";
            var bytes = Db.ToLong(row["index_bytes"]);
            return new Finding
            {
                CheckId = Id,
                Severity = bytes > 100 * 1024 * 1024 ? Severity.Medium : Severity.Low,
                Title = $"Index {index} on {table} has never been used",
                Detail = $"{index} ({bytes / 1024.0 / 1024.0:N1} MB) has zero scans since statistics were reset, " +
                         "but still costs write amplification on every INSERT/UPDATE and space on disk.",
                Recommendation = "Verify usage over a full business cycle (stats reset clears counters), " +
                                 "then DROP INDEX CONCURRENTLY if it is truly unused.",
                Evidence = new Dictionary<string, object?>
                {
                    ["table"] = table,
                    ["index"] = index,
                    ["index_bytes"] = bytes,
                },
            };
        }).ToList();
    }
}
