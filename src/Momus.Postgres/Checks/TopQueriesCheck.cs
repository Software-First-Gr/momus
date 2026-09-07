using System.Data.Common;
using Momus.Core;

namespace Momus.Postgres.Checks;

/// <param name="limit">
/// How many statements to report. The CLI shows five; the server asks for fifty, because the
/// store joins these against what the app reports and a query that is #23 in the database can
/// still be #1 on a single endpoint.
/// </param>
public sealed class TopQueriesCheck(int limit = 5) : IDiagnosticCheck
{
    public string Id => "pg.top_queries";
    public string Title => "Most expensive queries (pg_stat_statements)";
    public string Category => "queries";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var installed = await Db.ScalarAsync(connection,
            "SELECT count(*) FROM pg_extension WHERE extname = 'pg_stat_statements'", ct);
        if (Db.ToLong(installed) == 0)
        {
            return
            [
                new Finding
                {
                    CheckId = Id,
                    Severity = Severity.Info,
                    Title = "pg_stat_statements is not installed",
                    Detail = "Without pg_stat_statements Momus cannot rank queries by cumulative cost, " +
                             "which is the single most useful signal for optimization work.",
                    Recommendation = "Add pg_stat_statements to shared_preload_libraries, restart, then " +
                                     "CREATE EXTENSION pg_stat_statements;",
                    Subjects = [Subject.ForServer()],
                },
            ];
        }

        // The full text is what the fingerprint is computed from — truncating first would give a
        // key the app side can never reproduce. The truncated copy is only what humans read.
        var rows = await Db.QueryAsync(connection, $"""
            SELECT queryid, query AS full_query, left(query, 300) AS query,
                   calls, total_exec_time, mean_exec_time, rows
            FROM pg_stat_statements
            WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
            ORDER BY total_exec_time DESC
            LIMIT {Math.Clamp(limit, 1, 500)}
            """, ct);

        return rows.Select((row, i) =>
        {
            var meanMs = Db.ToDouble(row["mean_exec_time"]);
            var totalMs = Db.ToDouble(row["total_exec_time"]);
            var calls = Db.ToLong(row["calls"]);
            var fingerprint = SqlFingerprint.Analyze(Db.ToStr(row["full_query"]));
            return new Finding
            {
                CheckId = Id,
                Severity = PostgresThresholds.MeanQueryTimeSeverity(meanMs),
                Title = $"#{i + 1} query by total time: {totalMs / 1000:N1}s across {calls:N0} calls",
                Detail = $"Mean execution time {meanMs:N1} ms. Query text (truncated) is in the evidence.",
                Recommendation = meanMs > 250
                    ? "Run EXPLAIN (ANALYZE, BUFFERS) on this query; it is slow per-call, not just frequent."
                    : "Cheap per call but hot in aggregate — caching or batching may pay off more than tuning.",
                Subjects = [Subject.ForQuery(fingerprint.Key)],
                Evidence = new Dictionary<string, object?>
                {
                    ["query"] = Db.ToStr(row["query"]),
                    ["normalized"] = fingerprint.Text,
                    ["queryid"] = Db.ToStr(row["queryid"]),
                    ["calls"] = calls,
                    ["total_exec_ms"] = Math.Round(totalMs, 1),
                    ["mean_exec_ms"] = Math.Round(meanMs, 2),
                    ["rows"] = Db.ToLong(row["rows"]),
                },
            };
        }).ToList();
    }
}
