using System.Data.Common;
using Momus.Core;

namespace Momus.Postgres.Checks;

/// <summary>
/// Sessions waiting on a lock another session holds, and who holds it.
/// </summary>
/// <remarks>
/// Point-in-time, like every view it reads: a wait shorter than the scan interval is usually over
/// before anyone looks, so this finds blocking chains rather than brief contention. The demo's slow
/// build, whose cart updates each wait tens of milliseconds, is exactly what it cannot see — the
/// app side's <c>regression</c> is what catches that.
/// </remarks>
public sealed class LockWaitsCheck : IDiagnosticCheck
{
    public string Id => "pg.lock_waits";
    public string Title => "Sessions blocked on locks";
    public string Category => "sessions";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        // state_change is when an active session started its statement, so the wait is "at least
        // this long". pg_locks.waitstart would be exact, but only exists from PostgreSQL 14.
        // Only sessions waiting in this database: pg_stat_activity is cluster-wide. The holder is
        // not filtered — whoever holds the lock is the answer, whatever database it is in.
        var rows = await Db.QueryAsync(connection, $"""
            SELECT w.pid, w.usename, w.application_name, w.wait_event,
                   extract(epoch FROM now() - w.state_change)::bigint AS waiting_secs,
                   left(w.query, 300) AS query, w.query AS full_query,
                   cardinality(pg_blocking_pids(w.pid)) AS blocker_count,
                   b.pid AS blocker_pid, b.state AS blocker_state,
                   extract(epoch FROM now() - b.state_change)::bigint AS blocker_state_secs,
                   left(b.query, 300) AS blocker_query, b.query AS blocker_full_query
            FROM pg_stat_activity w
            LEFT JOIN LATERAL (
                SELECT a.pid, a.state, a.state_change, a.query
                FROM pg_stat_activity a
                WHERE a.pid = ANY (pg_blocking_pids(w.pid))
                ORDER BY a.state_change
                LIMIT 1
            ) b ON true
            WHERE w.wait_event_type = 'Lock'
              AND w.pid <> pg_backend_pid()
              AND w.datname = current_database()
              AND now() - w.state_change > interval '{PostgresThresholds.LockWaitMinSeconds} seconds'
            ORDER BY w.state_change
            LIMIT 20
            """, ct);

        return rows.Select(row =>
        {
            var pid = Db.ToLong(row["pid"]);
            var seconds = Db.ToLong(row["waiting_secs"]);
            long? blocker = row.GetValueOrDefault("blocker_pid") is null ? null : Db.ToLong(row["blocker_pid"]);
            var blockerState = Db.ToStr(row.GetValueOrDefault("blocker_state"));
            var blockers = Db.ToLong(row.GetValueOrDefault("blocker_count"));
            var idleBlocker = blockerState == "idle in transaction";
            var waited = Prose.Duration(TimeSpan.FromSeconds(seconds));

            return new Finding
            {
                CheckId = Id,
                Severity = PostgresThresholds.LockWaitSeverity(seconds) ?? Severity.Medium,
                Title = blocker is { } holder
                    ? $"Session {pid} has waited {waited} for a lock held by session {holder}"
                    : $"Session {pid} has waited {waited} for a lock",
                Detail = (idleBlocker
                        ? $"Session {blocker} is idle in transaction: it holds the lock and is doing nothing with it."
                        : "Everything that needs the same rows or table queues behind it, and so does anything waiting on those.") +
                    (blockers > 1 ? $" {blockers} sessions are blocking it in all." : ""),
                Recommendation = idleBlocker
                    ? "Find the code path that leaves that transaction open; idle_in_transaction_session_timeout stops it happening silently."
                    : "Look at what the blocking session is running (it is in the evidence) and keep transactions that take strong locks short.",
                Subjects = blocker is { } s ? [Subject.ForSession(pid), Subject.ForSession(s)] : [Subject.ForSession(pid)],
                Evidence = new Dictionary<string, object?>
                {
                    ["pid"] = pid,
                    ["user"] = Db.ToStr(row["usename"]),
                    ["application_name"] = Db.ToStr(row["application_name"]),
                    ["wait_event"] = Db.ToStr(row["wait_event"]),
                    ["seconds"] = seconds,
                    ["query"] = Db.ToStr(row["query"]),
                    // Not "query_fingerprint": the waiting statement is the victim, and an insight
                    // looking for the transaction that holds things up must only ever match the blocker.
                    ["waiting_query_fingerprint"] = SqlFingerprint.Compute(Db.ToStr(row.GetValueOrDefault("full_query"))),
                    ["blocker_pid"] = blocker,
                    ["blocker_state"] = blocker is null ? null : blockerState,
                    ["blocker_state_seconds"] = blocker is null ? null : Db.ToLong(row.GetValueOrDefault("blocker_state_secs")),
                    ["blocker_query"] = blocker is null ? null : Db.ToStr(row.GetValueOrDefault("blocker_query")),
                    // What ties the blocker to an application's transaction (TransactionHeldOpenInsight).
                    ["blocker_query_fingerprint"] = blocker is null
                        ? null
                        : SqlFingerprint.Compute(Db.ToStr(row.GetValueOrDefault("blocker_full_query"))),
                    ["blocker_count"] = blockers,
                },
            };
        }).ToList();
    }
}
