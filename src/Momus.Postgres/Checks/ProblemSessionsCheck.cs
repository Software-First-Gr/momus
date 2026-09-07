using System.Data.Common;
using Momus.Core;

namespace Momus.Postgres.Checks;

public sealed class ProblemSessionsCheck : IDiagnosticCheck
{
    public string Id => "pg.problem_sessions";
    public string Title => "Long-running and idle-in-transaction sessions";
    public string Category => "sessions";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection, """
            SELECT pid, state, usename, application_name,
                   extract(epoch FROM now() - state_change)::bigint AS in_state_secs,
                   extract(epoch FROM now() - query_start)::bigint AS running_secs,
                   left(query, 300) AS query
            FROM pg_stat_activity
            WHERE pid <> pg_backend_pid()
              AND ((state = 'idle in transaction' AND now() - state_change > interval '5 minutes')
                OR (state = 'active' AND now() - query_start > interval '5 minutes'))
            ORDER BY state_change
            LIMIT 20
            """, ct);

        return rows.Select(row =>
        {
            var state = Db.ToStr(row["state"]);
            var pid = Db.ToLong(row["pid"]);
            var idleInTx = state == "idle in transaction";
            var secs = idleInTx ? Db.ToLong(row["in_state_secs"]) : Db.ToLong(row["running_secs"]);
            return new Finding
            {
                CheckId = Id,
                Severity = idleInTx ? Severity.High : Severity.Medium,
                Title = idleInTx
                    ? $"Session {pid} idle in transaction for {TimeSpan.FromSeconds(secs):hh\\:mm\\:ss}"
                    : $"Session {pid} running one query for {TimeSpan.FromSeconds(secs):hh\\:mm\\:ss}",
                Detail = idleInTx
                    ? "An open transaction doing nothing holds locks and blocks vacuum from reclaiming dead tuples across the whole database."
                    : "A very long-running query may be blocking others or missing an index.",
                Recommendation = idleInTx
                    ? "Find the application code path that leaves the transaction open; consider setting idle_in_transaction_session_timeout."
                    : "Inspect the query plan with EXPLAIN; terminate with pg_terminate_backend(pid) if it is a runaway.",
                Subjects = [Subject.ForSession(pid)],
                Evidence = new Dictionary<string, object?>
                {
                    ["pid"] = pid,
                    ["state"] = state,
                    ["user"] = Db.ToStr(row["usename"]),
                    ["application_name"] = Db.ToStr(row["application_name"]),
                    ["seconds"] = secs,
                    ["query"] = Db.ToStr(row["query"]),
                },
            };
        }).ToList();
    }
}
