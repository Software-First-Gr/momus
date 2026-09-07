using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Shop.Api;

/// <summary>One thing this app can be asked to do to its database, and what Momus ought to say about it.</summary>
/// <param name="Kind">"burst" finishes in a moment; "background" runs for minutes and can be stopped.</param>
public sealed record Scenario(string Id, string Title, string What, string Expect, string Kind = "burst");

/// <summary>
/// The demo workload. Every query goes through EF Core on purpose: in M2 the Momus client will
/// intercept exactly these calls, and the route that ran them will be the operation name shown
/// beside the database's own view of the same statement.
/// </summary>
public sealed class Workload(
    SelfClient self,
    IServiceScopeFactory scopes,
    BackgroundJobs jobs,
    Telemetry telemetry,
    IConfiguration configuration,
    ILogger<Workload> logger)
{
    public static readonly IReadOnlyList<Scenario> Catalogue =
    [
        new("n_plus_one", "N+1 on GET /api/orders/{id}",
            "Loads one order, then its lines, then fetches each line's product in a separate query — the classic loop, 20 pages of it.",
            "order_lines has no index on order_id, so each of those queries scans the whole table. Expect it in top queries and in sequential-scan-heavy tables."),

        new("slow_search", "Slow product search",
            "Runs LOWER(name) LIKE '%term%' over 60,000 products, ten times. No index can serve it.",
            "Shows up as an expensive query by total time, and drags the buffer cache hit ratio down."),

        new("daily_report", "Daily report aggregate",
            "Groups 150,000 audit rows by action over a date range, five times.",
            "Another sequential scan; competes with the search query for the top-queries list."),

        new("healthy_cart", "Healthy cart updates",
            "Updates 200 carts by primary key — the control group.",
            "Index scans, nothing to report. A tool that flags this is crying wolf."),

        new("dead_tuples", "Update churn",
            "Rewrites every cart row twelve times, creating far more dead tuples than live rows.",
            "Triggers the dead-tuple / vacuum-debt check until autovacuum catches up."),

        new("idle_transaction", "Idle in transaction",
            "Opens a transaction, writes one row, then holds it open doing nothing for six minutes.",
            "Postgres reports it after five minutes: an open transaction blocks vacuum database-wide. High severity.",
            "background"),

        new("long_query", "One very long query",
            "Runs pg_sleep for six minutes on a connection of its own.",
            "After five minutes it appears as a long-running session. Medium severity.",
            "background"),

        new("connection_flood", "Hold 30 connections",
            "Opens 30 idle connections and keeps them for three minutes.",
            "Pushes connection use past 75% of max_connections, which is the saturation check's threshold.",
            "background"),
    ];

    private string ConnectionString =>
        configuration.GetConnectionString("Shop") ?? throw new InvalidOperationException("ConnectionStrings:Shop is not set.");

    /// <summary>Runs a scenario and returns the line the UI prints. Bursts go over HTTP, like real traffic.</summary>
    public async Task<string> RunAsync(string id, CancellationToken ct)
    {
        var scenario = Catalogue.FirstOrDefault(s => s.Id == id)
                       ?? throw new ArgumentException($"Unknown scenario '{id}'.", nameof(id));

        var result = id switch
        {
            "n_plus_one" => await BurstAsync(20, () => (HttpMethod.Get, $"/api/orders/{Random.Shared.Next(1, Schema.Orders + 1)}"), ct),
            "slow_search" => await BurstAsync(10, () => (HttpMethod.Get, $"/api/search?q={Terms[Random.Shared.Next(Terms.Length)]}"), ct),
            "daily_report" => await BurstAsync(5, () => (HttpMethod.Get, "/api/reports/daily"), ct),
            "healthy_cart" => await BurstAsync(200, () => (HttpMethod.Post, $"/api/cart/{Random.Shared.Next(1, Schema.Customers + 1)}/items"), ct),
            "dead_tuples" => await ChurnAsync(ct),
            "idle_transaction" => IdleTransaction(),
            "long_query" => LongQuery(),
            "connection_flood" => ConnectionFlood(),
            _ => throw new ArgumentException($"Unknown scenario '{id}'.", nameof(id)),
        };

        var message = $"{scenario.Title}: {result}";
        telemetry.Log("run", message);
        logger.LogInformation("Scenario {Id}: {Result}", id, result);
        return message;
    }

    private static readonly string[] Terms = ["widget", "gizmo", "sprocket", "bracket"];

    // ---- what the endpoints actually do ----------------------------------------------

    /// <summary>
    /// The order page: 1 query for the order, 1 for its lines, then one per line for the product.
    /// The bug is the loop, and it is the reason this sample exists.
    /// </summary>
    public async Task<int> OrderPageAsync(ShopDb db, int orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return 1;

        var lines = await db.OrderLines.AsNoTracking()
            .Where(l => l.OrderId == orderId)
            .ToListAsync(ct);

        var queries = 2;
        foreach (var line in lines)
        {
            await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == line.ProductId, ct);
            queries++;
        }

        return queries;
    }

    public async Task<List<object>> SearchAsync(ShopDb db, string? term, CancellationToken ct)
    {
        var q = string.IsNullOrWhiteSpace(term) ? Terms[Random.Shared.Next(Terms.Length)] : term;

        var products = await db.Products.AsNoTracking()
            .Where(p => p.Name.ToLower().Contains(q))
            .OrderBy(p => p.Price)
            .Take(20)
            .Select(p => new { p.Id, p.Name, p.Price })
            .ToListAsync(ct);

        return products.Cast<object>().ToList();
    }

    public async Task<List<object>> ReportAsync(ShopDb db, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Random.Shared.Next(3, 30));

        var rows = await db.AuditLog.AsNoTracking()
            .Where(a => a.At > since)
            .GroupBy(a => a.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.Cast<object>().ToList();
    }

    public Task CartUpdateAsync(ShopDb db, int cartId, CancellationToken ct) =>
        db.Carts
            .Where(c => c.Id == cartId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Total, c => c.Total + 1)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), ct);

    // ---- scenarios that need their own connection ------------------------------------

    private async Task<string> ChurnAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
        db.Database.SetCommandTimeout(120);

        var updated = 0;
        for (var pass = 0; pass < 12; pass++)
        {
            ct.ThrowIfCancellationRequested();
            updated += await db.Carts.ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Total, c => c.Total + 0.01m), ct);
            telemetry.Operation(1);
        }

        return $"{updated:N0} row versions written over {Schema.Customers:N0} live rows";
    }

    private string IdleTransaction()
    {
        jobs.Start("idle_transaction", "Idle in transaction", TimeSpan.FromMinutes(6), async ct =>
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO audit_log (at, actor, action) VALUES (now(), 'demo', 'idle-tx')";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // hold it open, doing nothing — the whole point
        });

        return "transaction open; Postgres calls it a problem after 5 minutes";
    }

    private string LongQuery()
    {
        jobs.Start("long_query", "Long-running query", TimeSpan.FromMinutes(6), async ct =>
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT pg_sleep(360)";
            cmd.CommandTimeout = 400;
            await cmd.ExecuteNonQueryAsync(ct);
        });

        return "pg_sleep(360) running; a long-running session after 5 minutes";
    }

    private string ConnectionFlood()
    {
        const int count = 30;
        jobs.Start("connection_flood", "30 held connections", TimeSpan.FromMinutes(3), async ct =>
        {
            var connections = new List<NpgsqlConnection>(count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    // Pooling off: these have to be real backends on the server, not pool entries.
                    var connection = new NpgsqlConnection(ConnectionString + ";Pooling=false");
                    await connection.OpenAsync(ct);
                    connections.Add(connection);
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                foreach (var connection in connections) await connection.DisposeAsync();
            }
        });

        return $"{count} connections held";
    }

    // ---- plumbing ---------------------------------------------------------------------

    private async Task<string> BurstAsync(int times, Func<(HttpMethod Method, string Path)> next, CancellationToken ct)
    {
        var queries = 0;
        for (var i = 0; i < times; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (method, path) = next();
            var executed = await self.HitAsync(method, path, ct);
            telemetry.Operation(executed);
            queries += executed;
        }

        return $"{times:N0} requests, {queries:N0} queries";
    }
}
