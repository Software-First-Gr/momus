using Momus.Core;
using Momus.Postgres;
using Momus.SqlServer;
using Momus.Tests.Fakes;

namespace Momus.Tests;

/// <summary>
/// Every finding must name what it is about. Subjects are how the insight engine joins the database
/// side to the application side and how the store recognises the same finding across scans, so a
/// check that produces a finding without one is a check whose output cannot be followed over time.
/// </summary>
public class SubjectTests
{
    [Fact]
    public async Task Every_postgres_finding_carries_a_subject()
    {
        var connection = PostgresRows();
        var findings = await RunAll(new PostgresScanTarget("Host=fake"), connection);

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.NotEmpty(f.Subjects));
        // Fixtures are meant to exercise every check, not just the easy ones.
        Assert.Equal(7, findings.Select(f => f.CheckId).Distinct().Count());
    }

    [Fact]
    public async Task Every_sqlserver_finding_carries_a_subject()
    {
        var connection = SqlServerRows();
        var findings = await RunAll(new SqlServerScanTarget("Server=fake"), connection);

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.NotEmpty(f.Subjects));
        Assert.Equal(5, findings.Select(f => f.CheckId).Distinct().Count());
    }

    [Fact]
    public async Task Query_findings_are_keyed_by_fingerprint_so_both_sides_can_join()
    {
        var findings = await RunAll(new PostgresScanTarget("Host=fake"), PostgresRows());

        var top = Assert.Single(findings, f => f.CheckId == "pg.top_queries");
        var subject = Assert.Single(top.Subjects);
        Assert.Equal(Subject.Query, subject.Kind);

        // The app side sees the same statement with its own parameter markers and spacing.
        Assert.Equal(subject.Key, SqlFingerprint.Compute(
            "SELECT o.id FROM orders o WHERE o.customer_id = @p0 AND o.status = @p1"));
    }

    [Fact]
    public void Subjects_round_trip_and_sort_independently_of_order()
    {
        Assert.Equal("table:public.orders", Subject.ForTable("public.orders").ToString());
        Assert.Equal(Subject.ForTable("public.orders"), Subject.Parse("table:public.orders"));
        Assert.Equal("server", Subject.ForServer().ToString());
        Assert.Equal(Subject.ForServer(), Subject.Parse("server"));

        Assert.Equal(
            Subject.Canonical([Subject.ForTable("t"), Subject.ForIndex("i")]),
            Subject.Canonical([Subject.ForIndex("i"), Subject.ForTable("t")]));
    }

    [Fact]
    public void Subjects_serialize_as_one_string_per_subject()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            Converters = { new SubjectJsonConverter() },
        };

        var finding = new Finding
        {
            CheckId = "pg.unused_indexes",
            Severity = Severity.Low,
            Title = "t",
            Detail = "d",
            Subjects = [Subject.ForIndex("ix_orders_status"), Subject.ForTable("public.orders")],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(finding, options);
        Assert.Contains("\"subjects\":[\"index:ix_orders_status\",\"table:public.orders\"]",
            json.Replace(" ", "").Replace("\n", "").Replace("\r", ""));

        var back = System.Text.Json.JsonSerializer.Deserialize<Finding>(json, options)!;
        Assert.Equal(finding.Subjects, back.Subjects);
    }

    private static async Task<List<Finding>> RunAll(IScanTarget target, FakeDataConnection connection)
    {
        await connection.OpenAsync();
        var findings = new List<Finding>();
        foreach (var check in target.Checks)
        {
            findings.AddRange(await check.RunAsync(connection, CancellationToken.None));
        }
        return findings;
    }

    // Fixture rows shaped like the real statistics views. Order matters: the first response whose
    // match appears in the SQL wins, so narrower probes are registered before broader views.
    private static FakeDataConnection PostgresRows() => new FakeDataConnection()
        .When("pg_stat_database", Row(("blks_hit", 850_000L), ("blks_read", 150_000L)))
        .When("max_connections", Row(("used", 95L), ("max", 100L)))
        .When("pg_total_relation_size", Row(
            ("schemaname", "public"), ("relname", "order_lines"), ("seq_scan", 4_000L),
            ("idx_scan", 12L), ("n_live_tup", 1_200_000L), ("total_bytes", 900_000_000L)))
        .When("pg_stat_user_indexes", Row(
            ("schemaname", "public"), ("table_name", "orders"),
            ("index_name", "ix_orders_status"), ("index_bytes", 220L * 1024 * 1024)))
        .When("n_dead_tup >", Row(
            ("schemaname", "public"), ("relname", "carts"), ("n_live_tup", 40_000L),
            ("n_dead_tup", 300_000L), ("last_autovacuum", ""), ("last_vacuum", "")))
        .When("pg_extension", Row(("count", 1L)))
        .When("FROM pg_stat_statements", Row(
            ("queryid", "-4738201938471"),
            ("full_query", "SELECT o.id FROM orders o WHERE o.customer_id = $1 AND o.status = $2"),
            ("query", "SELECT o.id FROM orders o WHERE o.customer_id = $1 AND o.status = $2"),
            ("calls", 48_000L), ("total_exec_time", 91_000.0), ("mean_exec_time", 1.9), ("rows", 192_000L)))
        .When("pg_stat_activity", Row(
            ("pid", 4412L), ("state", "idle in transaction"), ("usename", "app"),
            ("application_name", "Shop.Api"), ("in_state_secs", 900L), ("running_secs", 900L),
            ("query", "SELECT 1")));

    private static FakeDataConnection SqlServerRows() => new FakeDataConnection()
        .When("dm_os_wait_stats", Row(
            ("wait_type", "PAGEIOLATCH_SH"), ("wait_time_ms", 900_000L),
            ("waiting_tasks_count", 41_000L), ("pct", 62.5)))
        .When("dm_db_missing_index_group_stats", Row(
            ("table_name", "dbo.Orders"), ("avg_user_impact", 94.0), ("uses", 21_000L),
            ("avg_total_user_cost", 12.5), ("score", 246_000.0),
            ("equality_columns", "[CustomerId]"), ("inequality_columns", ""), ("included_columns", "[Total]")))
        .When("dm_exec_query_stats", Row(
            ("total_cpu_ms", 78_000L), ("execution_count", 120L), ("avg_cpu_ms", 650.0),
            ("total_logical_reads", 9_100_000L), ("query_hash", "0x8F3A1C77D02B4E10"),
            ("statement_text", "SELECT * FROM [Products] WHERE LOWER([Name]) LIKE @p0"),
            ("query_text", "SELECT * FROM [Products] WHERE LOWER([Name]) LIKE @p0")))
        .When("dm_exec_requests", Row(
            ("session_id", 61L), ("blocking_session_id", 54L), ("wait_type", "LCK_M_X"),
            ("wait_time", 42_000L), ("query_text", "UPDATE Carts SET Total = @p0 WHERE Id = @p1")))
        .When("dm_os_performance_counters", Row(("ple", 180L)));

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in cells) row[name] = value;
        return row;
    }
}
