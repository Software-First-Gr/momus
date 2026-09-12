using System.Data.Common;
using Momus.Core;

namespace Momus.Postgres.Checks;

public sealed class ConnectionSaturationCheck : IDiagnosticCheck
{
    public string Id => "pg.connection_saturation";
    public string Title => "Connection saturation";
    public string Category => "configuration";

    public async Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await Db.QueryAsync(connection, """
            -- Client backends only: pg_stat_activity also lists the checkpointer, the WAL writer,
            -- autovacuum and I/O workers, which do not count against max_connections.
            SELECT (SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend') AS used,
                   current_setting('max_connections')::int AS max
            """, ct);
        var used = Db.ToLong(rows[0]["used"]);
        var max = Db.ToLong(rows[0]["max"]);

        var severity = PostgresThresholds.ConnectionSaturationSeverity(used, max);
        if (severity is null) return [];

        return
        [
            new Finding
            {
                CheckId = Id,
                Severity = severity.Value,
                Title = $"Using {used} of {max} available connections",
                Detail = $"The server is at {(double)used / max:P0} of max_connections. " +
                         "When the limit is hit, new clients fail with 'too many connections'.",
                Recommendation = "Introduce or tune a connection pooler (PgBouncer, or pooling in the app), " +
                                 "and check for connection leaks before raising max_connections.",
                Subjects = [Subject.ForServer()],
                Evidence = new Dictionary<string, object?> { ["used"] = used, ["max"] = max },
            },
        ];
    }
}
