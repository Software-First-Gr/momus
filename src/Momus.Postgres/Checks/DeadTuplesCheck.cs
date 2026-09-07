using System.Data.Common;
using Momus.Core;

namespace Momus.Postgres.Checks;

public sealed class DeadTuplesCheck : IDiagnosticCheck
{
    public string Id => "pg.dead_tuples";
    public string Title => "Tables with excessive dead tuples";
    public string Category => "maintenance";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection, """
            SELECT schemaname, relname, n_live_tup, n_dead_tup,
                   last_autovacuum, last_vacuum
            FROM pg_stat_user_tables
            WHERE n_dead_tup > 10000
              AND n_dead_tup > n_live_tup * 0.2
            ORDER BY n_dead_tup DESC
            LIMIT 10
            """, ct);

        return rows.Select(row =>
        {
            var table = $"{Db.ToStr(row["schemaname"])}.{Db.ToStr(row["relname"])}";
            var dead = Db.ToLong(row["n_dead_tup"]);
            var live = Db.ToLong(row["n_live_tup"]);
            return new Finding
            {
                CheckId = Id,
                Severity = PostgresThresholds.DeadTupleSeverity(dead, live),
                Title = $"Table {table} has {dead:N0} dead tuples",
                Detail = $"{table} carries {dead:N0} dead tuples against {live:N0} live rows. " +
                         "Dead tuples bloat the table and slow every scan until vacuum reclaims them.",
                Recommendation = "Check that autovacuum is keeping up (last runs are in the evidence); consider " +
                                 "lowering autovacuum_vacuum_scale_factor for this table or running VACUUM (ANALYZE) now.",
                Subjects = [Subject.ForTable(table)],
                Evidence = new Dictionary<string, object?>
                {
                    ["table"] = table,
                    ["n_dead_tup"] = dead,
                    ["n_live_tup"] = live,
                    ["last_autovacuum"] = Db.ToStr(row["last_autovacuum"]),
                    ["last_vacuum"] = Db.ToStr(row["last_vacuum"]),
                },
            };
        }).ToList();
    }
}
