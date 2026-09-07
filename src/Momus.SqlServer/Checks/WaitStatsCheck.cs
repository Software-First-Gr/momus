using System.Data.Common;
using Momus.Core;

namespace Momus.SqlServer.Checks;

public sealed class WaitStatsCheck : IDiagnosticCheck
{
    public string Id => "mssql.wait_stats";
    public string Title => "Dominant wait types";
    public string Category => "waits";

    /// <summary>Background/housekeeping waits that are normal and never actionable.</summary>
    internal static readonly string[] BenignWaits =
    [
        "BROKER_EVENTQUEUE", "BROKER_RECEIVE_WAITFOR", "BROKER_TASK_STOP", "BROKER_TO_FLUSH",
        "BROKER_TRANSMITTER", "CHECKPOINT_QUEUE", "CLR_AUTO_EVENT", "CLR_MANUAL_EVENT",
        "CXCONSUMER", "DIRTY_PAGE_POLL", "DISPATCHER_QUEUE_SEMAPHORE",
        "FT_IFTS_SCHEDULER_IDLE_WAIT", "HADR_CLUSAPI_CALL", "HADR_FILESTREAM_IOMGR_IOCOMPLETION",
        "HADR_TIMER_TASK", "HADR_WORK_QUEUE", "LAZYWRITER_SLEEP", "LOGMGR_QUEUE",
        "ONDEMAND_TASK_QUEUE", "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP", "REQUEST_FOR_DEADLOCK_SEARCH",
        "SLEEP_SYSTEMTASK", "SLEEP_TASK", "SP_SERVER_DIAGNOSTICS_SLEEP", "SQLTRACE_BUFFER_FLUSH",
        "WAITFOR", "XE_DISPATCHER_WAIT", "XE_TIMER_EVENT",
    ];

    private static readonly Dictionary<string, string> KnownCauses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PAGEIOLATCH_SH"] = "reading data pages from disk — often memory pressure or missing indexes",
        ["PAGEIOLATCH_EX"] = "writing data pages — often storage latency",
        ["WRITELOG"] = "transaction log flush latency — storage or overly chatty commits",
        ["LCK_M_X"] = "exclusive lock contention — long transactions or missing indexes",
        ["LCK_M_S"] = "shared lock contention",
        ["CXPACKET"] = "parallelism skew — consider MAXDOP / cost threshold tuning",
        ["SOS_SCHEDULER_YIELD"] = "CPU pressure — runnable queues are long",
        ["RESOURCE_SEMAPHORE"] = "queries waiting for memory grants",
        ["ASYNC_NETWORK_IO"] = "client is consuming results slowly (row-by-row processing?)",
    };

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var benignList = string.Join(", ", BenignWaits.Select(w => $"'{w}'"));
        var rows = await Db.QueryAsync(connection, $"""
            WITH waits AS (
                SELECT wait_type, wait_time_ms, waiting_tasks_count
                FROM sys.dm_os_wait_stats
                WHERE waiting_tasks_count > 0 AND wait_time_ms > 0
                  AND wait_type NOT IN ({benignList})
                  AND wait_type NOT LIKE 'PREEMPTIVE_%'
            )
            SELECT TOP 5 wait_type, wait_time_ms, waiting_tasks_count,
                   CAST(100.0 * wait_time_ms / NULLIF(SUM(wait_time_ms) OVER (), 0) AS decimal(5,2)) AS pct
            FROM waits
            ORDER BY wait_time_ms DESC
            """, ct);

        return rows.Select(row =>
        {
            var waitType = Db.ToStr(row["wait_type"]);
            var pct = Db.ToDouble(row["pct"]);
            var cause = KnownCauses.TryGetValue(waitType, out var c) ? c : null;
            return new Finding
            {
                CheckId = Id,
                Severity = pct > 40 && cause is not null ? Severity.Medium : Severity.Info,
                Title = $"{waitType} accounts for {pct:N1}% of measurable wait time",
                Detail = cause is not null
                    ? $"{waitType} usually means: {cause}."
                    : $"{waitType} is among the top waits since the last restart/stats reset.",
                Recommendation = "Wait stats are cumulative since restart — confirm with a delta sample during " +
                                 "a busy period before acting on them.",
                Subjects = [Subject.ForServer()],
                Evidence = new Dictionary<string, object?>
                {
                    ["wait_type"] = waitType,
                    ["wait_time_ms"] = Db.ToLong(row["wait_time_ms"]),
                    ["waiting_tasks_count"] = Db.ToLong(row["waiting_tasks_count"]),
                    ["pct_of_waits"] = pct,
                },
            };
        }).ToList();
    }
}
