using Momus.Core;
using Momus.Core.Ingest;
using Momus.Core.Insights;
using Momus.Server.Insights;
using Momus.Tests.Fakes;

namespace Momus.Tests;

/// <summary>
/// The three rules that need time or shape rather than a single number: what a deploy changed,
/// what a transaction was holding, and what a pool was not handing out.
/// </summary>
public class M3InsightTests
{
    // ---- regression --------------------------------------------------------------------

    [Fact]
    public async Task A_statement_that_got_slower_with_the_newest_build_is_found()
    {
        var insight = Assert.Single(await Run(new RegressionInsight(), new InsightSnapshot
        {
            Deploys = [Deploy("1.4.0", -3), Deploy("1.5.0", -1)],
            VersionStats =
            [
                Version("9f3a", "1.4.0", calls: 1000, meanMs: 4),
                Version("9f3a", "1.5.0", calls: 1000, meanMs: 32),
            ],
        }));

        Assert.Equal("regression", insight.Kind);
        Assert.Equal(Severity.High, insight.Severity);
        Assert.Contains("8.0× slower in 1.5.0", insight.Title);
        Assert.Equal(Subject.ForQuery("9f3a"), Assert.Single(insight.Subjects));
    }

    [Fact]
    public async Task Ten_times_slower_is_not_a_regression_it_is_an_emergency()
    {
        var insight = Assert.Single(await Run(new RegressionInsight(), new InsightSnapshot
        {
            Deploys = [Deploy("1.4.0", -3), Deploy("1.5.0", -1)],
            VersionStats =
            [
                Version("9f3a", "1.4.0", calls: 500, meanMs: 4),
                Version("9f3a", "1.5.0", calls: 500, meanMs: 60),
            ],
        }));

        Assert.Equal(Severity.Critical, insight.Severity);
    }

    [Theory]
    // Three times slower, but three times almost nothing.
    [InlineData(1000, 0.5, 1000, 5.0)]
    // Ten times slower on twelve calls, which is an anecdote.
    [InlineData(12, 4, 12, 40)]
    // Slower, but not enough to be sure it is not the weather.
    [InlineData(1000, 40, 1000, 60)]
    public async Task A_difference_that_is_noise_stays_quiet(
        long callsBefore, double msBefore, long callsAfter, double msAfter)
    {
        Assert.Empty(await Run(new RegressionInsight(), new InsightSnapshot
        {
            Deploys = [Deploy("1.4.0", -3), Deploy("1.5.0", -1)],
            VersionStats =
            [
                Version("9f3a", "1.4.0", callsBefore, msBefore),
                Version("9f3a", "1.5.0", callsAfter, msAfter),
            ],
        }));
    }

    [Fact]
    public async Task With_only_one_build_ever_seen_there_is_nothing_to_compare()
    {
        Assert.Empty(await Run(new RegressionInsight(), new InsightSnapshot
        {
            Deploys = [Deploy("1.5.0", -1)],
            VersionStats = [Version("9f3a", "1.5.0", 1000, 40)],
        }));
    }

    [Fact]
    public async Task What_the_database_started_saying_between_the_two_deploys_is_evidence()
    {
        var snapshot = new InsightSnapshot
        {
            Deploys = [Deploy("1.4.0", -3), Deploy("1.5.0", -1)],
            VersionStats =
            [
                Version("9f3a", "1.4.0", 1000, 4),
                Version("9f3a", "1.5.0", 1000, 40),
            ],
            LatestFindings =
            [
                // Appeared between the two deploys: a candidate explanation nobody would look for.
                Finding(Severity.High, "Index ix_orders_status was dropped", firstSeenHoursAgo: 2),
                // Older than both, so it explains nothing about this change.
                Finding(Severity.High, "Cache hit ratio is 78%", firstSeenHoursAgo: 100),
            ],
        };

        var insight = Assert.Single(await Run(new RegressionInsight(), snapshot));

        Assert.Contains("ix_orders_status", insight.Detail);
        Assert.DoesNotContain("Cache hit ratio", insight.Detail);
    }

    // ---- transaction_held_open ---------------------------------------------------------

    [Fact]
    public async Task A_transaction_open_across_work_that_is_not_the_database_is_found()
    {
        var insight = Assert.Single(await Run(new TransactionHeldOpenInsight(), new InsightSnapshot
        {
            Transactions = [Transaction("POST /checkout", count: 200, openMs: 1200, dbMs: 90)],
        }));

        Assert.Equal("transaction_held_open", insight.Kind);
        Assert.Equal(Severity.Medium, insight.Severity);
        Assert.Equal(Subject.ForOperation("POST /checkout"), Assert.Single(insight.Subjects));
        Assert.Contains("only 8%", insight.Detail);
    }

    [Fact]
    public async Task A_transaction_that_is_mostly_database_work_is_just_a_slow_query()
    {
        Assert.Empty(await Run(new TransactionHeldOpenInsight(), new InsightSnapshot
        {
            Transactions = [Transaction("POST /checkout", count: 200, openMs: 1200, dbMs: 1100)],
        }));
    }

    [Fact]
    public async Task Idle_sessions_on_the_database_side_make_it_High()
    {
        var insight = Assert.Single(await Run(new TransactionHeldOpenInsight(), new InsightSnapshot
        {
            Transactions = [Transaction("POST /checkout", count: 200, openMs: 1200, dbMs: 90)],
            LatestFindings =
            [
                Finding(Severity.High, "3 sessions idle in transaction for over 5 minutes",
                    checkId: "pg.problem_sessions"),
            ],
        }));

        Assert.Equal(Severity.High, insight.Severity);
        Assert.Contains("idle in transaction", insight.Detail);
    }

    // ---- pool_wait ---------------------------------------------------------------------

    [Fact]
    public async Task Waiting_for_a_connection_is_latency_with_no_query_behind_it()
    {
        var insight = Assert.Single(await Run(new PoolWaitInsight(), new InsightSnapshot
        {
            PoolWaits = [Pool("GET /orders/{id}", waits: 400, waitMs: 260)],
            Transactions = [Transaction("POST /checkout", count: 200, openMs: 1200, dbMs: 90)],
        }));

        Assert.Equal("pool_wait", insight.Kind);
        Assert.Contains("POST /checkout", insight.Detail);
    }

    [Fact]
    public async Task A_pool_that_hands_a_connection_over_immediately_says_nothing()
    {
        Assert.Empty(await Run(new PoolWaitInsight(), new InsightSnapshot
        {
            PoolWaits = [Pool("GET /orders/{id}", waits: 400, waitMs: 1)],
        }));
    }

    [Fact]
    public async Task A_saturated_database_makes_the_wait_High()
    {
        var insight = Assert.Single(await Run(new PoolWaitInsight(), new InsightSnapshot
        {
            PoolWaits = [Pool("GET /orders/{id}", waits: 400, waitMs: 260)],
            LatestFindings =
            [
                Finding(Severity.High, "34 of 40 connections in use",
                    checkId: "pg.connection_saturation"),
            ],
        }));

        Assert.Equal(Severity.High, insight.Severity);
    }

    // ---- fixtures ----------------------------------------------------------------------

    private static readonly DateTimeOffset When = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static async Task<IReadOnlyList<Insight>> Run(IInsight rule, IInsightContext context) =>
        await rule.EvaluateAsync(context, CancellationToken.None);

    private static DeployView Deploy(string version, int hoursAgo) =>
        new(version, When.AddHours(hoursAgo), When, 1);

    private static VersionStatView Version(string key, string version, long calls, double meanMs) => new()
    {
        Fingerprint = key,
        Version = version,
        Calls = calls,
        TotalMs = calls * meanMs,
        Operation = "GET /orders/{id}",
        CallSite = "OrdersHandler.cs:42",
    };

    /// <summary>Every sample in one bucket, so the p95 is that bucket's ceiling and is predictable.</summary>
    private static TransactionStatView Transaction(string operation, long count, double openMs, double dbMs) => new()
    {
        Operation = operation,
        CallSite = "CheckoutService.cs:88",
        Count = count,
        OpenMs = new Timing(openMs * count, openMs, Bucket(openMs, count)),
        DbMsSum = dbMs * count,
    };

    private static PoolStatView Pool(string operation, long waits, double waitMs) => new()
    {
        Operation = operation,
        Waits = waits,
        WaitMs = new Timing(waitMs * waits, waitMs, Bucket(waitMs, waits)),
    };

    private static long[] Bucket(double ms, long count)
    {
        var histogram = new long[Timing.Buckets];
        histogram[Timing.BucketFor(ms)] = count;
        return histogram;
    }

    private static FindingView Finding(
        Severity severity, string title, string checkId = "pg.seq_scan_heavy_tables",
        int firstSeenHoursAgo = 4) => new()
    {
        CheckId = checkId,
        Category = "sessions",
        Severity = severity,
        Title = title,
        Detail = "",
        EvidenceJson = "{}",
        Subjects = [Subject.ForServer()],
        FirstSeen = When.AddHours(-firstSeenHoursAgo),
        LastSeen = When,
        SeenCount = 3,
    };
}
