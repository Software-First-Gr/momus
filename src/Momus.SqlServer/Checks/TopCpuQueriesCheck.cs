using System.Data.Common;
using Momus.Core;

namespace Momus.SqlServer.Checks;

/// <param name="limit">
/// How many statements to report. The CLI shows five; the server asks for fifty, so the store can
/// join them against what the app reports rather than only against the worst offenders.
/// </param>
public sealed class TopCpuQueriesCheck(int limit = 5) : IDiagnosticCheck
{
    public string Id => "mssql.top_cpu_queries";
    public string Title => "Most expensive queries by CPU";
    public string Category => "queries";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        // statement_text is the full slice the fingerprint is computed from; query_text is the
        // truncated copy shown to a human. Hashing the truncated text would produce a key no
        // application could ever reproduce.
        var rows = await Db.QueryAsync(connection, $"""
            SELECT TOP {Math.Clamp(limit, 1, 500)}
                qs.total_worker_time / 1000 AS total_cpu_ms,
                qs.execution_count,
                qs.total_worker_time / NULLIF(qs.execution_count, 0) / 1000.0 AS avg_cpu_ms,
                qs.total_logical_reads,
                CONVERT(varchar(34), qs.query_hash, 1) AS query_hash,
                SUBSTRING(st.text, (qs.statement_start_offset / 2) + 1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                      ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1) AS statement_text,
                LEFT(SUBSTRING(st.text, (qs.statement_start_offset / 2) + 1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                      ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1), 300) AS query_text
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            ORDER BY qs.total_worker_time DESC
            """, ct);

        return rows.Select((row, i) =>
        {
            var avgCpuMs = Db.ToDouble(row["avg_cpu_ms"]);
            var totalCpuMs = Db.ToLong(row["total_cpu_ms"]);
            var execs = Db.ToLong(row["execution_count"]);
            var fingerprint = SqlFingerprint.Analyze(Db.ToStr(row["statement_text"]));
            return new Finding
            {
                CheckId = Id,
                Severity = avgCpuMs > 1000 ? Severity.High : avgCpuMs > 250 ? Severity.Medium : Severity.Info,
                Title = $"#{i + 1} query by CPU: {totalCpuMs / 1000.0:N1}s across {execs:N0} executions",
                Detail = $"Average CPU {avgCpuMs:N1} ms per execution, {Db.ToLong(row["total_logical_reads"]):N0} " +
                         "total logical reads. Query text (truncated) is in the evidence.",
                Recommendation = avgCpuMs > 250
                    ? "Get the actual execution plan for this statement; high per-execution CPU usually means a scan or spill."
                    : "Cheap per call but hot in aggregate — check call frequency from the application first.",
                Subjects = [Subject.ForQuery(fingerprint.Key)],
                Evidence = new Dictionary<string, object?>
                {
                    ["query"] = Db.ToStr(row["query_text"]).Trim(),
                    ["normalized"] = fingerprint.Text,
                    ["query_hash"] = Db.ToStr(row["query_hash"]),
                    ["execution_count"] = execs,
                    ["total_cpu_ms"] = totalCpuMs,
                    ["avg_cpu_ms"] = Math.Round(avgCpuMs, 2),
                    ["total_logical_reads"] = Db.ToLong(row["total_logical_reads"]),
                },
            };
        }).ToList();
    }
}
