using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Momus.Tools.FingerprintCapture;

/// <summary>Everything provider-specific: how to connect, how to make a table, and how to read back
/// what the engine recorded about the statement just executed.</summary>
public abstract class DatabaseProbe
{
    public abstract void Configure(DbContextOptionsBuilder<FxDb> options, string connectionString);
    public abstract Task CreateSchemaAsync(FxDb db);
    public abstract Task DropSchemaAsync(FxDb db);

    /// <summary>Anything the engine needs before it will record statements at all.</summary>
    public virtual Task PrepareAsync(DbConnection connection) => Task.CompletedTask;

    /// <summary>
    /// Identity of every statement the engine knows about, with its execution count folded in, so
    /// that running a shape a second time counts as a change even though the statement is not new.
    /// </summary>
    public abstract Task<HashSet<string>> SnapshotAsync(DbConnection connection);

    /// <summary>Statement texts recorded since <paramref name="before"/> was taken.</summary>
    public abstract Task<List<string>> NewSinceAsync(DbConnection connection, HashSet<string> before);

    protected static async Task<List<(string Id, string Text)>> ReadAsync(DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetValue(0)?.ToString() ?? "", reader.GetValue(1)?.ToString() ?? ""));
        }
        return rows;
    }
}

public sealed class PostgresProbe : DatabaseProbe
{
    public override void Configure(DbContextOptionsBuilder<FxDb> options, string connectionString) =>
        options.UseNpgsql(connectionString);

    public override async Task CreateSchemaAsync(FxDb db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS fx_products (
                "Id" serial PRIMARY KEY, "Name" text NOT NULL, "Category" text NOT NULL, "Price" numeric(10,2) NOT NULL);
            CREATE TABLE IF NOT EXISTS fx_orders (
                "Id" serial PRIMARY KEY, "Status" text NOT NULL, "Total" numeric(10,2) NOT NULL, "CreatedAt" timestamptz NOT NULL);
            CREATE TABLE IF NOT EXISTS fx_lines (
                "Id" serial PRIMARY KEY, "OrderId" int NOT NULL, "ProductId" int NOT NULL, "Quantity" int NOT NULL);

            INSERT INTO fx_products ("Name", "Category", "Price")
            SELECT 'widget ' || g, 'tools', g FROM generate_series(1, 200) g;
            INSERT INTO fx_orders ("Status", "Total", "CreatedAt")
            SELECT 'paid', g, now() - (g || ' hours')::interval FROM generate_series(1, 100) g;
            INSERT INTO fx_lines ("OrderId", "ProductId", "Quantity")
            SELECT 1 + (g % 100), 1 + (g % 200), 1 + (g % 4) FROM generate_series(1, 300) g;
            """);
    }

    public override Task DropSchemaAsync(FxDb db) =>
        db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS fx_lines, fx_orders, fx_products;");

    public override async Task PrepareAsync(DbConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_stat_statements";
        await cmd.ExecuteNonQueryAsync();
    }

    private const string Identity =
        "SELECT queryid::text || ':' || calls::text AS id, query FROM pg_stat_statements";

    public override async Task<HashSet<string>> SnapshotAsync(DbConnection connection) =>
        (await ReadAsync(connection, Identity)).Select(r => r.Id).ToHashSet();

    public override async Task<List<string>> NewSinceAsync(DbConnection connection, HashSet<string> before) =>
        (await ReadAsync(connection, Identity))
        .Where(r => !before.Contains(r.Id))
        .Select(r => r.Text)
        .Where(NotOurOwn)
        .ToList();

    private static bool NotOurOwn(string sql) =>
        !sql.Contains("pg_stat_statements", StringComparison.OrdinalIgnoreCase) &&
        !Housekeeping.Any(h => sql.TrimStart().StartsWith(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Transaction control from EF batching and the reset Npgsql sends when a connection goes back
    /// to the pool. Real statements, but not the one under test.
    /// </summary>
    private static readonly string[] Housekeeping =
        ["BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "DISCARD", "DEALLOCATE", "SET ", "SHOW "];
}

public sealed class SqlServerProbe : DatabaseProbe
{
    public override void Configure(DbContextOptionsBuilder<FxDb> options, string connectionString) =>
        options.UseSqlServer(connectionString);

    public override async Task CreateSchemaAsync(FxDb db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID('fx_products') IS NULL CREATE TABLE fx_products (
                Id int IDENTITY PRIMARY KEY, Name nvarchar(200) NOT NULL, Category nvarchar(100) NOT NULL, Price decimal(10,2) NOT NULL);
            """);
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID('fx_orders') IS NULL CREATE TABLE fx_orders (
                Id int IDENTITY PRIMARY KEY, Status nvarchar(50) NOT NULL, Total decimal(10,2) NOT NULL, CreatedAt datetime2 NOT NULL);
            """);
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID('fx_lines') IS NULL CREATE TABLE fx_lines (
                Id int IDENTITY PRIMARY KEY, OrderId int NOT NULL, ProductId int NOT NULL, Quantity int NOT NULL);
            """);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO fx_products (Name, Category, Price)
            SELECT TOP 200 'widget ' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS varchar(10)), 'tools',
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
            FROM sys.all_objects;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO fx_orders (Status, Total, CreatedAt)
            SELECT TOP 100 'paid', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), DATEADD(hour, -ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), GETUTCDATE())
            FROM sys.all_objects;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO fx_lines (OrderId, ProductId, Quantity)
            SELECT TOP 300 1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 100),
                   1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 200),
                   1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 4)
            FROM sys.all_objects;
            """);
    }

    public override Task DropSchemaAsync(FxDb db) =>
        db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS fx_lines, fx_orders, fx_products;");

    // A plan cache entry is identified by its handle plus where the statement starts inside it,
    // which is exactly how the SQL Server check slices text out of dm_exec_sql_text.
    private const string Identity = """
        SELECT CONVERT(varchar(64), qs.sql_handle, 1) + ':' + CAST(qs.statement_start_offset AS varchar(12))
                 + ':' + CAST(qs.execution_count AS varchar(12)) AS id,
               SUBSTRING(st.text, (qs.statement_start_offset / 2) + 1,
                   ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                     ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1) AS statement_text
        FROM sys.dm_exec_query_stats qs
        CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
        """;

    public override async Task<HashSet<string>> SnapshotAsync(DbConnection connection) =>
        (await ReadAsync(connection, Identity)).Select(r => r.Id).ToHashSet();

    public override async Task<List<string>> NewSinceAsync(DbConnection connection, HashSet<string> before) =>
        (await ReadAsync(connection, Identity))
        .Where(r => !before.Contains(r.Id))
        .Select(r => r.Text.Trim())
        .Where(NotOurOwn)
        .ToList();

    private static bool NotOurOwn(string sql) =>
        !sql.Contains("dm_exec_query_stats", StringComparison.OrdinalIgnoreCase) &&
        !sql.Contains("dm_exec_sql_text", StringComparison.OrdinalIgnoreCase);
}
