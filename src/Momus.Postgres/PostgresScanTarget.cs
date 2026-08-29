using System.Data.Common;
using Momus.Core;
using Momus.Postgres.Checks;
using Npgsql;

namespace Momus.Postgres;

/// <summary>PostgreSQL scan target: connects with Npgsql and runs the Postgres check suite.</summary>
public sealed class PostgresScanTarget(string connectionString) : IScanTarget
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
        new TopQueriesCheck(),
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
