using Microsoft.Extensions.Logging.Abstractions;
using Momus.Core;
using Momus.Core.Insights;
using Momus.Server.Insights;

namespace Momus.Tests;

/// <summary>
/// The rules, against hand-built snapshots. No database and no store: an insight is a pure
/// function of what the context holds, which is the whole reason the context is a snapshot.
/// </summary>
public class InsightTests
{
    // ---- n_plus_one --------------------------------------------------------------------

    [Fact]
    public async Task A_loop_inside_one_operation_is_found()
    {
        var insight = Assert.Single(await Run(new NPlusOneInsight(), Context(
            queries: [Query("prod", "GET /orders/{id}", repeats: 6, perMinute: 2543, meanMs: 0.2)])));

        Assert.Equal("n_plus_one", insight.Kind);
        Assert.Equal(Severity.Medium, insight.Severity);
        Assert.Contains("×6 per call", insight.Title);
        Assert.Contains("GET /orders/{id}", insight.Title);

        // Five of every six round trips are the avoidable ones.
        Assert.Contains("2,119 round trips", insight.Detail);
        Assert.Equal(Subject.ForQuery("prod"), Assert.Single(insight.Subjects));
    }

    [Fact]
    public async Task A_healthy_dataset_produces_nothing()
    {
        Assert.Empty(await Run(new NPlusOneInsight(), Context(
            queries: [Query("one", "GET /search", repeats: 1, perMinute: 2000, meanMs: 16)])));
    }

    [Fact]
    public async Task A_loop_nobody_runs_is_not_worth_an_afternoon()
    {
        // A tight loop, twice an hour. The shape is there; the cost is not.
        Assert.Empty(await Run(new NPlusOneInsight(), Context(
            queries: [Query("rare", "POST /import", repeats: 40, perMinute: 2, meanMs: 3)])));
    }

    [Fact]
    public async Task A_loop_over_a_table_the_database_dislikes_is_High()
    {
        var insight = Assert.Single(await Run(new NPlusOneInsight(), Context(
            queries:
            [
                Query("lines", "GET /orders/{id}", repeats: 6, perMinute: 2543, meanMs: 0.2,
                    sample: "select o.id from order_lines o where o.order_id = ?"),
            ],
            findings:
            [
                Finding(Severity.High, "Table public.order_lines is scanned 9,000 times",
                    Subject.ForTable("public.order_lines")),
            ])));

        Assert.Equal(Severity.High, insight.Severity);
        Assert.Contains("order_lines", insight.Detail);
    }

    [Fact]
    public async Task A_table_that_is_only_a_substring_is_not_a_match()
    {
        // "orders" must not match "order_lines", or every finding on one would raise the other.
        var insight = Assert.Single(await Run(new NPlusOneInsight(), Context(
            queries:
            [
                Query("lines", "GET /orders/{id}", repeats: 6, perMinute: 2543, meanMs: 0.2,
                    sample: "select o.id from order_lines o where o.order_id = ?"),
            ],
            findings: [Finding(Severity.High, "Table public.orders is scanned", Subject.ForTable("public.orders"))])));

        Assert.Equal(Severity.Medium, insight.Severity);
    }

    // ---- hot_query_origin --------------------------------------------------------------

    [Fact]
    public async Task The_database_s_worst_statement_is_mapped_to_the_code_that_runs_it()
    {
        var insight = Assert.Single(await Run(new HotQueryOriginInsight(), Context(
            queries: [Query("hot", "GET /orders/{id}", repeats: 1, perMinute: 424, meanMs: 7.9)],
            findings: [TopQuery(Severity.Info, "#1 query by total time", "hot", meanMs: 7.5, totalMs: 17700, calls: 2375)])));

        Assert.Equal("hot_query_origin", insight.Kind);
        Assert.Equal(Severity.Info, insight.Severity);
        Assert.Contains("#1 by total database time", insight.Title);
        Assert.Contains("GET /orders/{id}", insight.Title);
        Assert.Contains("OrdersHandler.cs:42", insight.Detail);
        Assert.Equal("GET /orders/{id}", insight.Evidence["operation"]);
    }

    [Fact]
    public async Task A_statement_no_application_sent_says_so_rather_than_going_missing()
    {
        var insight = Assert.Single(await Run(new HotQueryOriginInsight(), Context(
            findings: [TopQuery(Severity.Medium, "#1 query by total time", "orphan", meanMs: 2300, totalMs: 9200, calls: 4)])));

        Assert.Contains("no application reported it", insight.Title);
        Assert.Contains("a job, a migration", insight.Detail);
        Assert.Null(insight.Evidence["operation"]);
    }

    [Fact]
    public async Task Only_the_top_statements_become_cards()
    {
        var findings = Enumerable.Range(0, 25)
            .Select(i => TopQuery(Severity.Info, $"#{i + 1} query", $"k{i}", 1, totalMs: 1000 - i, calls: 10))
            .ToList();

        var insights = await Run(new HotQueryOriginInsight(), Context(findings: findings));

        Assert.Equal(HotQueryOriginInsight.Top, insights.Count);
        Assert.Equal(Subject.ForQuery("k0"), Assert.Single(insights[0].Subjects));
    }

    // ---- db_finding --------------------------------------------------------------------

    [Fact]
    public async Task A_finding_about_a_table_names_the_endpoints_that_touch_it()
    {
        var insight = Assert.Single(await Run(new DbFindingInsight(), Context(
            queries:
            [
                Query("a", "GET /orders/{id}", repeats: 1, perMinute: 400, meanMs: 8,
                    sample: "select o.id from order_lines o where o.order_id = ?"),
                Query("b", "GET /search", repeats: 1, perMinute: 200, meanMs: 16,
                    sample: "select p.id from products p where lower(p.name) like ?"),
            ],
            findings:
            [
                Finding(Severity.High, "Table public.order_lines is scanned", Subject.ForTable("public.order_lines")),
            ])));

        Assert.Equal("db_finding", insight.Kind);
        Assert.Contains("GET /orders/{id}", insight.Detail);
        Assert.DoesNotContain("GET /search", insight.Detail);
    }

    [Fact]
    public async Task Statement_findings_are_left_to_the_rule_that_says_more_about_them()
    {
        var insights = await Run(new DbFindingInsight(), Context(
            findings:
            [
                TopQuery(Severity.Info, "#1 query by total time", "hot", 1, 100, 10),
                Finding(Severity.Medium, "Cache hit ratio is 78%", Subject.ForServer()),
            ]));

        Assert.Equal("Cache hit ratio is 78%", Assert.Single(insights).Title);
    }

    [Fact]
    public async Task Two_server_wide_findings_do_not_become_one_insight()
    {
        var insights = await Run(new DbFindingInsight(), Context(findings:
        [
            Finding(Severity.Medium, "Cache hit ratio is 78%", Subject.ForServer()) with { CheckId = "pg.cache_hit_ratio" },
            Finding(Severity.High, "34 of 40 connections in use", Subject.ForServer()) with { CheckId = "pg.connection_saturation" },
        ]));

        Assert.Equal(2, insights.Count);
        Assert.Equal(2, insights.Select(i => i.IdentityKey).Distinct().Count());
    }

    // ---- the engine --------------------------------------------------------------------

    [Fact]
    public async Task A_rule_that_throws_does_not_take_the_others_with_it()
    {
        var engine = new InsightEngine(NullLogger<InsightEngine>.Instance,
            [new Exploding(), new NPlusOneInsight()]);

        var insights = await engine.EvaluateAsync(Context(
            queries: [Query("prod", "GET /orders/{id}", repeats: 6, perMinute: 2543, meanMs: 0.2)]),
            CancellationToken.None);

        Assert.Equal("n_plus_one", Assert.Single(insights).Kind);
    }

    private sealed class Exploding : IInsight
    {
        public string Kind => "explodes";

        public Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct) =>
            throw new InvalidOperationException("a rule with a bug in it");
    }

    // ---- fixtures ----------------------------------------------------------------------

    private static async Task<IReadOnlyList<Insight>> Run(IInsight rule, IInsightContext context) =>
        await rule.EvaluateAsync(context, CancellationToken.None);

    private static IInsightContext Context(
        IReadOnlyList<QueryStatView>? queries = null,
        IReadOnlyList<FindingView>? findings = null) => new Snapshot
    {
        TargetId = "shop",
        Now = When,
        Since = When.AddHours(-1),
        QueryStats = queries ?? [],
        LatestFindings = findings ?? [],
    };

    private static readonly DateTimeOffset When = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private sealed record Snapshot : IInsightContext
    {
        public required string TargetId { get; init; }
        public required DateTimeOffset Now { get; init; }
        public required DateTimeOffset Since { get; init; }
        public IReadOnlyList<FindingView> LatestFindings { get; init; } = [];
        public IReadOnlyList<QueryStatView> QueryStats { get; init; } = [];
        public IReadOnlyList<OperationStatView> OperationStats { get; init; } = [];
    }

    private static QueryStatView Query(
        string key, string operation, int repeats, double perMinute, double meanMs,
        string sample = "select p.id from products p where p.id = ?") => new()
    {
        Fingerprint = key,
        TargetId = "shop",
        Operation = operation,
        OperationCount = 1,
        CallSite = "OrdersHandler.cs:42",
        Sample = sample,
        Calls = (long)(perMinute * 60),
        TotalMs = perMinute * 60 * meanMs,
        MeanMs = meanMs,
        CallsPerMinute = perMinute,
        MaxRepeats = repeats,
    };

    private static FindingView Finding(Severity severity, string title, params Subject[] subjects) => new()
    {
        CheckId = "pg.seq_scan_heavy_tables",
        Category = "tables",
        Severity = severity,
        Title = title,
        Detail = "The database says so.",
        EvidenceJson = "{}",
        Subjects = subjects,
        FirstSeen = When.AddHours(-3),
        LastSeen = When,
        SeenCount = 4,
    };

    private static FindingView TopQuery(
        Severity severity, string title, string key, double meanMs, double totalMs, long calls) => new()
    {
        CheckId = "pg.top_queries",
        Category = "queries",
        Severity = severity,
        Title = title,
        Detail = "",
        EvidenceJson = $$"""
            {"normalized":"select ... from order_lines where order_id = ?",
             "mean_exec_ms":{{Invariant(meanMs)}}, "total_exec_ms":{{Invariant(totalMs)}}, "calls":{{calls}}}
            """,
        Subjects = [Subject.ForQuery(key)],
        FirstSeen = When.AddHours(-3),
        LastSeen = When,
        SeenCount = 4,
    };

    private static string Invariant(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
