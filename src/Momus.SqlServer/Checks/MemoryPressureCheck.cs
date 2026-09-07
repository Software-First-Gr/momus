using System.Data.Common;
using Momus.Core;

namespace Momus.SqlServer.Checks;

public sealed class MemoryPressureCheck : IDiagnosticCheck
{
    public string Id => "mssql.memory_pressure";
    public string Title => "Buffer pool memory pressure (PLE)";
    public string Category => "memory";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection, """
            SELECT cntr_value AS ple
            FROM sys.dm_os_performance_counters
            WHERE counter_name = 'Page life expectancy'
              AND object_name LIKE '%Buffer Manager%'
            """, ct);
        if (rows.Count == 0) return [];

        var ple = Db.ToLong(rows[0]["ple"]);
        if (ple >= 900) return [];

        return
        [
            new Finding
            {
                CheckId = Id,
                Severity = ple < 300 ? Severity.High : Severity.Medium,
                Title = $"Page life expectancy is {ple}s",
                Detail = "Pages are being evicted from the buffer pool quickly, which usually surfaces as " +
                         "PAGEIOLATCH waits and slow queries. The classic floor is ~300s, scaled up for large buffer pools.",
                Recommendation = "Check max server memory, look for scan-heavy queries flushing the pool " +
                                 "(see top CPU queries), and consider more RAM if the working set simply doesn't fit.",
                Subjects = [Subject.ForServer()],
                Evidence = new Dictionary<string, object?> { ["page_life_expectancy_s"] = ple },
            },
        ];
    }
}
