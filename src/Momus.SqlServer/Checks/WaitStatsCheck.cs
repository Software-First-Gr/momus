using System.Data.Common;
using Momus.Core;

namespace Momus.SqlServer.Checks;

public sealed class WaitStatsCheck : IDiagnosticCheck
{
    public string Id => "mssql.wait_stats";
    public string Title => "Dominant wait types";
    public string Category => "waits";

    /// <summary>
    /// Background, housekeeping and startup waits: a thread sleeping until it has work, not a
    /// query waiting for a resource. They are most of the instance's wait time on a quiet server,
    /// so leaving any of them in turns every scan of a staging database into noise. The 2019/2022
    /// block is what the top of <c>sys.dm_os_wait_stats</c> actually held on two idle SQL Server
    /// 2022 instances on 2026-09-24 — <c>SOS_WORK_DISPATCHER</c> alone was 94% — plus the rest of
    /// the well-known idle set. Real waits (<c>WaitStatsTests</c> pins some) must never be added.
    /// </summary>
    internal static readonly string[] BenignWaits =
    [
        "BROKER_EVENTHANDLER", "BROKER_EVENTQUEUE", "BROKER_RECEIVE_WAITFOR", "BROKER_TASK_STOP",
        "BROKER_TO_FLUSH", "BROKER_TRANSMITTER", "CHECKPOINT_QUEUE", "CHKPT", "CLR_AUTO_EVENT",
        "CLR_MANUAL_EVENT", "CLR_SEMAPHORE", "CXCONSUMER", "DBMIRROR_DBM_EVENT", "DBMIRROR_EVENTS_QUEUE",
        "DBMIRROR_WORKER_QUEUE", "DBMIRRORING_CMD", "DIRTY_PAGE_POLL", "DISPATCHER_QUEUE_SEMAPHORE",
        "EXECSYNC", "FSAGENT", "FT_IFTS_SCHEDULER_IDLE_WAIT", "FT_IFTSHC_MUTEX", "HADR_CLUSAPI_CALL",
        "HADR_FILESTREAM_IOMGR_IOCOMPLETION", "HADR_LOGCAPTURE_WAIT", "HADR_NOTIFICATION_DEQUEUE",
        "HADR_TIMER_TASK", "HADR_WORK_QUEUE", "KSOURCE_WAKEUP", "LAZYWRITER_SLEEP", "LOGMGR_QUEUE",
        "MEMORY_ALLOCATION_EXT", "ONDEMAND_TASK_QUEUE", "PARALLEL_REDO_DRAIN_WORKER",
        "PARALLEL_REDO_LOG_CACHE", "PARALLEL_REDO_TRAN_LIST", "PARALLEL_REDO_WORKER_SYNC",
        "PARALLEL_REDO_WORKER_WAIT_WORK", "QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP",
        "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP", "QDS_SHUTDOWN_QUEUE", "REDO_THREAD_PENDING_WORK",
        "REQUEST_FOR_DEADLOCK_SEARCH", "RESOURCE_QUEUE", "SERVER_IDLE_CHECK", "SLEEP_BPOOL_FLUSH",
        "SLEEP_DBSTARTUP", "SLEEP_DCOMSTARTUP", "SLEEP_MASTERDBREADY", "SLEEP_MASTERMDREADY",
        "SLEEP_MASTERUPGRADED", "SLEEP_MSDBSTARTUP", "SLEEP_SYSTEMTASK", "SLEEP_TASK",
        "SLEEP_TEMPDBSTARTUP", "SNI_HTTP_ACCEPT", "SP_SERVER_DIAGNOSTICS_SLEEP", "SQLTRACE_BUFFER_FLUSH",
        "SQLTRACE_WAIT_ENTRIES", "VDI_CLIENT_OTHER", "WAIT_FOR_RESULTS", "WAIT_XTP_CKPT_CLOSE",
        "WAIT_XTP_HOST_WAIT", "WAIT_XTP_OFFLINE_CKPT_NEW_LOG", "WAIT_XTP_RECOVERY", "WAITFOR",
        "WAITFOR_TASKSHUTDOWN", "XE_DISPATCHER_JOIN", "XE_DISPATCHER_WAIT", "XE_LIVE_TARGET_TVF",
        "XE_TIMER_EVENT",

        // SQL Server 2019 and 2022, seen on idle instances.
        "AZURE_IMDS_VERSIONS", "PVS_PREALLOCATE", "PWAIT_ALL_COMPONENTS_INITIALIZED",
        "PWAIT_DIRECTLOGCONSUMER_GETNEXT", "PWAIT_EXTENSIBILITY_CLEANUP_TASK", "QDS_ASYNC_QUEUE",
        "SLEEP_PHYSMASTERDBREADY", "SOS_WORK_DISPATCHER", "SQLTRACE_INCREMENTAL_FLUSH_SLEEP",
        "STARTUP_DEPENDENCY_MANAGER",
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
                Detail = (cause is not null
                    ? $"{waitType} usually means: {cause}."
                    : $"{waitType} is among the top waits since the last restart/stats reset.") +
                    " Wait statistics belong to the instance, so they include every database on it.",
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
