using System.Data.Common;
using Microsoft.Data.SqlClient;
using Momus.Core;
using Momus.SqlServer.Checks;

namespace Momus.SqlServer;

/// <summary>SQL Server scan target: connects with Microsoft.Data.SqlClient and runs the DMV check suite.</summary>
/// <param name="connectionString">Microsoft.Data.SqlClient connection string. Only DMVs are read.</param>
/// <param name="topQueryLimit">Statements to keep from dm_exec_query_stats: 5 for a console report, 50 for the store.</param>
public sealed class SqlServerScanTarget(string connectionString, int topQueryLimit = 5) : IScanTarget
{
    public string Provider => "sqlserver";

    public DbConnection CreateConnection() => new SqlConnection(connectionString);

    public IReadOnlyList<IDiagnosticCheck> Checks { get; } =
    [
        new WaitStatsCheck(),
        new MissingIndexesCheck(),
        new TopCpuQueriesCheck(topQueryLimit),
        new BlockingSessionsCheck(),
        new MemoryPressureCheck(),
    ];

    public async Task<TargetInfo> GetTargetInfoAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection, """
            SELECT @@VERSION AS version, DB_NAME() AS db,
                   SERVERPROPERTY('Edition') AS edition
            """, ct);
        var row = rows[0];
        return new TargetInfo
        {
            Provider = Provider,
            ServerVersion = Db.ToStr(row["version"]).Split('\n')[0].Trim(),
            DatabaseName = Db.ToStr(row["db"]),
            Extra = new Dictionary<string, object?> { ["edition"] = Db.ToStr(row["edition"]) },
        };
    }
}
