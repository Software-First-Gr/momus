using System.Data.Common;
using Momus.Core;
using Momus.Postgres.Checks;
using Npgsql;

namespace Momus.Postgres;

/// <summary>PostgreSQL scan target: connects with Npgsql and runs the Postgres check suite.</summary>
/// <param name="connectionString">Npgsql connection string. Only statistics views are read.</param>
/// <param name="topQueryLimit">Statements to read from pg_stat_statements: 5 for a console report, 500 for the server.</param>
public sealed class PostgresScanTarget(string connectionString, int topQueryLimit = 5) : IScanTarget
{
    public string Provider => "postgres";

    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    public IReadOnlyList<IDiagnosticCheck> Checks { get; } =
    [
        new CacheHitRatioCheck(),
        new ConnectionSaturationCheck(),
        new SeqScanHeavyTablesCheck(),
        new UnusedIndexesCheck(),
        new DeadTuplesCheck(),
        new ProblemSessionsCheck(),
        new LockWaitsCheck(),
        // Last, so every other check's SQL is already known to be Momus's own (Db.IsOwn).
        new TopQueriesCheck(topQueryLimit),
    ];

    public async Task<TargetInfo> GetTargetInfoAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection,
            "SELECT version() AS version, current_database() AS db", ct);
        var row = rows[0];
        return new TargetInfo
        {
            Provider = Provider,
            ServerVersion = Db.ToStr(row["version"]),
            DatabaseName = Db.ToStr(row["db"]),
        };
    }
}
