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

    /// <summary>Spare rows to fetch so that leaving out Momus's own statements still fills the limit.</summary>
    public const int OwnStatementAllowance = 25;

    /// <summary>
    /// master, tempdb, model and msdb. Scanning through one of them means the instance, not a
    /// database, so the statement and blocking checks do not narrow to it.
    /// </summary>
    public const int SystemDatabaseMaxId = 4;

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        // statement_text is the full slice the fingerprint is computed from; query_text is the
        // truncated copy shown to a human. Hashing the truncated text would produce a key no
        // application could ever reproduce.
        //
        // dm_exec_query_stats covers every database on the instance, so on a shared server the list
        // was other databases' statements too — and when two databases run the same application,
        // the same statement twice under one fingerprint. The plan's dbid attribute says which
        // database compiled it. dm_exec_sql_text.dbid does not: it is NULL for prepared statements,
        // which is everything EF Core sends (measured on SQL Server 2022, 2026-09-24). A connection
        // to a system database has no application of its own, so it keeps the instance-wide view.
        var rows = await Db.QueryAsync(connection, $"""
            SELECT TOP {Math.Clamp(limit, 1, 500) + OwnStatementAllowance}
                qs.total_worker_time / 1000 AS total_cpu_ms,
                qs.execution_count,
                qs.total_worker_time / NULLIF(qs.execution_count, 0) / 1000.0 AS avg_cpu_ms,
                qs.total_logical_reads,
                CONVERT(varchar(34), qs.query_hash, 1) AS query_hash,
                DB_NAME(pa.dbid) AS database_name,
                SUBSTRING(st.text, (qs.statement_start_offset / 2) + 1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                      ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1) AS statement_text,
                LEFT(SUBSTRING(st.text, (qs.statement_start_offset / 2) + 1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                      ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1), 300) AS query_text
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            CROSS APPLY (SELECT CONVERT(int, value) AS dbid
                         FROM sys.dm_exec_plan_attributes(qs.plan_handle)
                         WHERE attribute = 'dbid') pa
            WHERE pa.dbid = DB_ID() OR DB_ID() <= {SystemDatabaseMaxId}
            ORDER BY qs.total_worker_time DESC
            """, ct);

        // Momus's own reads are in the DMV too. Fetch a few spare rows so leaving them out still
        // returns as many statements as were asked for.
        return rows
            .Select(row => (Row: row, Fingerprint: SqlFingerprint.Analyze(Db.ToStr(row["statement_text"]))))
            .Where(r => !Db.IsOwn(r.Fingerprint.Key))
            .Take(Math.Clamp(limit, 1, 500))
            .Select((r, i) =>
        {
            var (row, fingerprint) = r;
            var avgCpuMs = Db.ToDouble(row["avg_cpu_ms"]);
            var totalCpuMs = Db.ToLong(row["total_cpu_ms"]);
            var execs = Db.ToLong(row["execution_count"]);
            return new Finding
            {
                CheckId = Id,
                Severity = avgCpuMs > 1000 ? Severity.High : avgCpuMs > 250 ? Severity.Medium : Severity.Info,
                Title = $"#{i + 1} query by CPU: {Prose.Millis(totalCpuMs)} across {Prose.Count(execs, "execution")}",
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
                    ["database"] = Db.ToStr(row["database_name"]),
                    ["execution_count"] = execs,
                    ["total_cpu_ms"] = totalCpuMs,
                    ["avg_cpu_ms"] = Math.Round(avgCpuMs, 2),
                    ["total_logical_reads"] = Db.ToLong(row["total_logical_reads"]),
                },
            };
        }).ToList();
    }
}
