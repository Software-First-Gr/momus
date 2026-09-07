using System.Data.Common;
using Momus.Core;

namespace Momus.SqlServer.Checks;

public sealed class MissingIndexesCheck : IDiagnosticCheck
{
    public string Id => "mssql.missing_indexes";
    public string Title => "Missing indexes suggested by the optimizer";
    public string Category => "indexes";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection, """
            SELECT TOP 10
                OBJECT_SCHEMA_NAME(mid.object_id, mid.database_id) + '.' +
                    OBJECT_NAME(mid.object_id, mid.database_id) AS table_name,
                migs.avg_user_impact,
                migs.user_seeks + migs.user_scans AS uses,
                migs.avg_total_user_cost,
                (migs.user_seeks + migs.user_scans) * migs.avg_total_user_cost * (migs.avg_user_impact / 100.0) AS score,
                mid.equality_columns, mid.inequality_columns, mid.included_columns
            FROM sys.dm_db_missing_index_group_stats migs
            JOIN sys.dm_db_missing_index_groups mig ON migs.group_handle = mig.index_group_handle
            JOIN sys.dm_db_missing_index_details mid ON mig.index_handle = mid.index_handle
            WHERE mid.database_id = DB_ID()
            ORDER BY score DESC
            """, ct);

        return rows
            .Where(row => Db.ToDouble(row["score"]) > 100)
            .Select(row =>
            {
                var table = Db.ToStr(row["table_name"]);
                var score = Db.ToDouble(row["score"]);
                var impact = Db.ToDouble(row["avg_user_impact"]);
                return new Finding
                {
                    CheckId = Id,
                    Severity = score > 100_000 ? Severity.High : score > 10_000 ? Severity.Medium : Severity.Low,
                    Title = $"Optimizer wants an index on {table} (est. {impact:N0}% improvement)",
                    Detail = $"Queries against {table} triggered {Db.ToLong(row["uses"]):N0} seeks/scans that " +
                             "would have used this index. Column details are in the evidence.",
                    Recommendation = "Treat DMV suggestions as hints, not commands: consolidate overlapping " +
                                     "suggestions and validate the workload cost of one more index before creating it.",
                    Subjects = [Subject.ForTable(table)],
                    Evidence = new Dictionary<string, object?>
                    {
                        ["table"] = table,
                        ["equality_columns"] = Db.ToStr(row["equality_columns"]),
                        ["inequality_columns"] = Db.ToStr(row["inequality_columns"]),
                        ["included_columns"] = Db.ToStr(row["included_columns"]),
                        ["avg_user_impact_pct"] = impact,
                        ["score"] = Math.Round(score, 0),
                    },
                };
            }).ToList();
    }
}
