using System.Data.Common;
using Momus.Core;

namespace Momus.SqlServer.Checks;

public sealed class BlockingSessionsCheck : IDiagnosticCheck
{
    public string Id => "mssql.blocking";
    public string Title => "Blocked sessions right now";
    public string Category => "sessions";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        // dm_exec_requests is instance-wide. A blocked request belongs to the database it is running
        // in; blocking in a neighbouring database is that database's finding, not this one's.
        var rows = await Db.QueryAsync(connection, $"""
            SELECT r.session_id, r.blocking_session_id, r.wait_type, r.wait_time,
                   DB_NAME(r.database_id) AS database_name,
                   LEFT(st.text, 300) AS query_text
            FROM sys.dm_exec_requests r
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) st
            WHERE r.blocking_session_id <> 0
              AND (r.database_id = DB_ID() OR DB_ID() <= {TopCpuQueriesCheck.SystemDatabaseMaxId})
            """, ct);

        return rows.Select(row =>
        {
            var waitMs = Db.ToLong(row["wait_time"]);
            return new Finding
            {
                CheckId = Id,
                Severity = waitMs > 30_000 ? Severity.Critical : Severity.High,
                Title = $"Session {Db.ToLong(row["session_id"])} blocked by session " +
                        $"{Db.ToLong(row["blocking_session_id"])} for {waitMs / 1000.0:N0}s",
                Detail = $"Wait type {Db.ToStr(row["wait_type"])}. The blocked statement is in the evidence. " +
                         "This is live blocking observed at scan time.",
                Recommendation = "Trace the head blocker (sp_WhoIsActive or sys.dm_exec_requests) — long " +
                                 "transactions and missing indexes are the usual causes.",
                Subjects =
                [
                    Subject.ForSession(Db.ToLong(row["session_id"])),
                    Subject.ForSession(Db.ToLong(row["blocking_session_id"])),
                ],
                Evidence = new Dictionary<string, object?>
                {
                    ["session_id"] = Db.ToLong(row["session_id"]),
                    ["blocking_session_id"] = Db.ToLong(row["blocking_session_id"]),
                    ["wait_type"] = Db.ToStr(row["wait_type"]),
                    ["wait_ms"] = waitMs,
                    ["database"] = Db.ToStr(row["database_name"]),
                    ["query"] = Db.ToStr(row["query_text"]).Trim(),
                },
            };
        }).ToList();
    }
}
