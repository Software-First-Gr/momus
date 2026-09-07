using Momus.Core;
using Momus.Core.Ingest;
using Momus.Server;
using Momus.Server.Diagnostics;
using Momus.Server.Store;

namespace Momus.Tests;

/// <summary>
/// The page someone opens when Momus is not doing what they expected. Its whole value is that it
/// is right about which half is broken, so each of these is a way the loop fails quietly.
/// </summary>
public class DiagnosticsTests
{
    [Fact]
    public async Task With_nothing_configured_it_says_so_first()
    {
        await using var store = await MomusStore.InMemoryAsync();

        var report = await Build(store);

        Assert.True(report.AnyProblem);
        Assert.Contains(report.Checks, c => c.Headline.Contains("No database to scan"));
    }

    [Fact]
    public async Task A_database_with_no_statement_data_is_a_permission_problem_and_says_which()
    {
        // The most common half-broken install: every other check works, statement ranking is
        // simply absent, and nothing else on any page would ever mention it.
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);
        await store.SaveScanAsync("shop", Report(Finding(Severity.High, "Table scanned",
            Subject.ForTable("public.orders"))));

        var report = await Build(store);

        var check = Assert.Single(report.Checks, c => c.Headline.Contains("not reporting any statements"));
        Assert.Equal(Verdict.Problem, check.Verdict);
        Assert.Contains("pg_monitor", check.WhatToDo);
    }

    [Fact]
    public async Task Both_halves_working_is_reported_as_working()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);
        await store.SaveScanAsync("shop", Report(TopQuery("9f3a")));
        await store.SaveIngestBatchAsync(Batch(Query("9f3a", callSite: "OrdersHandler.cs:42")));

        var report = await Build(store);

        Assert.False(report.AnyProblem);
        Assert.Equal(1, report.Joined);
        Assert.Contains(report.Checks, c => c.Headline.Contains("matched on both sides"));
    }

    [Fact]
    public async Task Two_sides_that_never_match_is_the_failure_worth_shouting_about()
    {
        // Both halves are alive and the product is pointless: this is what a fingerprint
        // regression looks like from the outside, and nothing else would show it.
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);
        await store.SaveScanAsync("shop", Report(TopQuery("aaaa")));
        await store.SaveIngestBatchAsync(Batch(Query("bbbb", callSite: "OrdersHandler.cs:42")));

        var report = await Build(store);

        var check = Assert.Single(report.Checks, c => c.Headline.Contains("none of them matched"));
        Assert.Equal(Verdict.Problem, check.Verdict);
        Assert.True(report.AnyProblem);
    }

    [Fact]
    public async Task Statements_with_no_call_site_are_a_problem_and_not_a_shrug()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);
        await store.SaveScanAsync("shop", Report(TopQuery("9f3a")));
        await store.SaveIngestBatchAsync(Batch(Query("9f3a", callSite: null)));

        var report = await Build(store);

        Assert.Equal(0, report.Ingest.StatementsWithCallSite);
        Assert.Contains(report.Checks, c =>
            c.Verdict == Verdict.Problem && c.Headline.Contains("No statement has a call site"));
    }

    [Fact]
    public async Task A_client_that_is_losing_data_says_so_where_someone_will_see_it()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);
        await store.SaveScanAsync("shop", Report(TopQuery("9f3a")));

        var batch = Batch(Query("9f3a", callSite: "OrdersHandler.cs:42")) with
        {
            Overflow = 12,
            Client = new IngestClient("0.2.0", Dropped: 340),
        };
        await store.SaveIngestBatchAsync(batch);

        var report = await Build(store);

        Assert.Equal(340, report.Ingest.Dropped);
        Assert.Equal("0.2.0", report.Ingest.ClientVersion);
        Assert.Contains(report.Checks, c => c.Headline.Contains("dropped 340"));
        Assert.Contains(report.Checks, c => c.Headline.Contains("12 execution(s) went unattributed"));
    }

    [Fact]
    public async Task The_report_never_carries_a_connection_string()
    {
        // It exists to be pasted into an issue, so this is not a style preference.
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);
        await store.SaveScanAsync("shop", Report(TopQuery("9f3a")));
        await store.SaveIngestBatchAsync(Batch(Query("9f3a", callSite: "OrdersHandler.cs:42")));

        var markdown = DiagnosticsMarkdown.Render(await Build(store));

        Assert.DoesNotContain("hunter2", markdown);
        Assert.DoesNotContain("Password", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Momus diagnostics", markdown);
        Assert.Contains("Matched on both sides: 1", markdown);
    }

    // ---- fixtures ----------------------------------------------------------------------

    private static Task<DiagnosticsReport> Build(MomusStore store) =>
        new DiagnosticsBuilder(store, new ServerOptions()).BuildAsync();

    private static Task AddTargetAsync(MomusStore store) => store.UpsertTargetAsync(new StoredTarget
    {
        Id = "shop", Name = "shop", Provider = "postgres",
        ConnectionString = "Host=localhost;Database=shop;Password=hunter2", Source = "cli",
    });

    private static Finding Finding(Severity severity, string title, params Subject[] subjects) => new()
    {
        CheckId = "pg.seq_scan_heavy_tables",
        Severity = severity,
        Title = title,
        Detail = "detail",
        Subjects = subjects,
    };

    private static Finding TopQuery(string key) => new()
    {
        CheckId = "pg.top_queries",
        Severity = Severity.Info,
        Title = "#1 query by total time",
        Detail = "detail",
        Subjects = [Subject.ForQuery(key)],
        Evidence = new Dictionary<string, object?>
        {
            ["normalized"] = "select ... from orders where id = ?",
            ["mean_exec_ms"] = 1.1,
            ["total_exec_ms"] = 78000.0,
            ["calls"] = 70000L,
        },
    };

    private static ScanReport Report(params Finding[] findings) => new()
    {
        StartedAt = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromMilliseconds(120),
        Target = new TargetInfo { Provider = "postgres", DatabaseName = "shop", ServerVersion = "18.4" },
        Checks =
        [
            new CheckResult
            {
                CheckId = findings.FirstOrDefault()?.CheckId ?? "pg.top_queries",
                Title = "check", Category = "queries", Succeeded = true,
                Duration = TimeSpan.FromMilliseconds(12), Findings = findings,
            },
        ],
    };

    private static IngestBatch Batch(params IngestQuery[] queries) => new()
    {
        App = new IngestApp("Shop.Api", "1.0.0", "web-01:1", "Development"),
        Window = new IngestWindow(DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow),
        Queries = queries,
    };

    private static IngestQuery Query(string key, string? callSite) =>
        new(key, "shop", "GET /orders/{id}", callSite, "select ... from orders where id = ?",
            100, new Timing(180, 4, [0, 100, 0, 0, 0, 0, 0, 0]), 100, 1, 0);
}
