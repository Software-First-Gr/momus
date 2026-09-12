using Momus.Core;
using Momus.Server;
using Momus.Server.Store;

namespace Momus.Tests;

/// <summary>
/// The database's statement counters run from the last reset; the app side is the last hour.
/// These tests are the difference between the two, and the demo case that made it necessary: a seed
/// INSERT that ran once, days ago, holding a Fix first slot.
/// </summary>
public class StatementActivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    // ---- ranking on the window ---------------------------------------------------------

    [Fact]
    public void A_statement_that_did_nothing_in_the_window_is_not_ranked()
    {
        var baseline = Baseline(Now.AddHours(-1), ("seed", 1, 1055), ("hot", 10_000, 4_000));

        var result = StatementActivity.Apply(Report(Pg("seed", 1, 1055), Pg("hot", 12_000, 5_000)), baseline);

        var finding = Assert.Single(Statements(result));
        Assert.Equal(Subject.ForQuery("hot"), Assert.Single(finding.Subjects));
        Assert.Equal("#1 query by total time in the last hour: 1.0 s across 2,000 calls", finding.Title);
        Assert.Equal(2_000L, finding.Evidence["calls"]);
        Assert.Equal(1_000.0, finding.Evidence["total_exec_ms"]);
        Assert.Equal(0.5, finding.Evidence["mean_exec_ms"]);
        Assert.Equal(12_000L, finding.Evidence["lifetime_calls"]);
        Assert.Equal(3_600.0, finding.Evidence["window_seconds"]);
    }

    [Fact]
    public void The_order_is_this_hour_s_work_not_a_lifetime_s()
    {
        var baseline = Baseline(Now.AddHours(-1), ("old", 100, 900_000), ("busy", 1_000, 1_000));

        var result = StatementActivity.Apply(Report(Pg("old", 110, 900_500), Pg("busy", 5_000, 21_000)), baseline);

        Assert.Equal(["query:busy", "query:old"], Statements(result).Select(f => f.Subjects[0].ToString()));
        Assert.StartsWith("#2 query by total time in the last hour: 500 ms", Statements(result)[1].Title);
    }

    [Fact]
    public void Without_a_baseline_the_numbers_say_they_are_since_the_reset()
    {
        var finding = Assert.Single(Statements(StatementActivity.Apply(Report(Pg("a", 3, 300)), baseline: null)));

        Assert.Equal("#1 query by total time since statistics were reset: 300 ms across 3 calls", finding.Title);
        Assert.Null(finding.Evidence["window_seconds"]);
    }

    [Fact]
    public void Counters_that_went_down_were_reset_and_everything_they_hold_is_new()
    {
        var baseline = Baseline(Now.AddMinutes(-40), ("a", 5_000, 10_000));

        var finding = Assert.Single(Statements(StatementActivity.Apply(Report(Pg("a", 40, 80)), baseline)));

        Assert.Equal(40L, finding.Evidence["calls"]);
        Assert.Equal("#1 query by total time in the last 40 min: 80 ms across 40 calls", finding.Title);
    }

    [Fact]
    public void A_statement_new_since_the_baseline_counts_from_zero()
    {
        var baseline = Baseline(Now.AddMinutes(-20), ("other", 10, 10));

        var result = StatementActivity.Apply(Report(Pg("other", 10, 10), Pg("new", 7, 70)), baseline);

        var finding = Assert.Single(Statements(result));
        Assert.Equal(7L, finding.Evidence["calls"]);
    }

    [Fact]
    public void Session_bookkeeping_is_counted_but_never_ranked()
    {
        var result = StatementActivity.Apply(
            Report(Pg("discard", 5_000, 50, text: "DISCARD all"), Pg("q", 10, 5)), baseline: null);

        Assert.Equal(Subject.ForQuery("q"), Assert.Single(Statements(result)).Subjects[0]);
        Assert.Contains(result.Samples, s => s.Fingerprint == "discard");
    }

    [Fact]
    public void Every_statement_is_sampled_but_only_the_top_are_kept()
    {
        var findings = Enumerable.Range(0, 60).Select(i => Pg($"k{i}", 10, 1_000 - i)).ToArray();

        var result = StatementActivity.Apply(Report(findings), baseline: null);

        Assert.Equal(StatementActivity.Keep, Statements(result).Count);
        Assert.Equal(60, result.Samples.Count);
        Assert.StartsWith($"#{StatementActivity.Keep} ", Statements(result)[^1].Title);
    }

    [Fact]
    public void Two_entries_with_one_fingerprint_are_one_statement()
    {
        var result = StatementActivity.Apply(Report(Pg("same", 10, 100), Pg("same", 5, 50)), baseline: null);

        var finding = Assert.Single(Statements(result));
        Assert.Equal(15L, finding.Evidence["calls"]);
        Assert.Equal(new StatementSample("same", 15, 150), Assert.Single(result.Samples));
    }

    [Fact]
    public void Sql_server_counts_cpu_and_executions()
    {
        var baseline = Baseline(Now.AddHours(-1), ("x", 100, 1_000));

        var finding = Assert.Single(Statements(StatementActivity.Apply(Report(Mssql("x", 110, 2_000)), baseline)));

        Assert.Equal("#1 query by CPU in the last hour: 1.0 s across 10 executions", finding.Title);
        Assert.Equal(1_000.0, finding.Evidence["total_cpu_ms"]);
        Assert.Equal(100.0, finding.Evidence["avg_cpu_ms"]);
        Assert.Equal(10L, finding.Evidence["execution_count"]);
    }

    [Fact]
    public void Findings_that_are_not_statements_and_other_checks_pass_through()
    {
        var notInstalled = new Finding
        {
            CheckId = "pg.top_queries", Severity = Severity.Info, Title = "pg_stat_statements is not installed",
            Detail = "", Subjects = [Subject.ForServer()],
        };
        var report = Report(notInstalled) with
        {
            Checks = [.. Report(notInstalled).Checks, Check("pg.dead_tuples", Pg("looks-like-one", 1, 1))],
        };

        var result = StatementActivity.Apply(report, baseline: null);

        Assert.Same(notInstalled, Assert.Single(result.Report.Checks[0].Findings));
        Assert.Same(report.Checks[1], result.Report.Checks[1]);
        Assert.Empty(result.Samples);
    }

    [Theory]
    [InlineData(30, "in the last 30 s")]
    [InlineData(12 * 60, "in the last 12 min")]
    [InlineData(62 * 60, "in the last hour")]
    [InlineData(2 * 3600, "in the last 2 h")]
    public void The_window_is_named_the_way_a_person_would(int seconds, string expected) =>
        Assert.Equal(expected, StatementActivity.Span(TimeSpan.FromSeconds(seconds)));

    // ---- the baseline in the store -----------------------------------------------------

    [Fact]
    public async Task The_baseline_is_the_newest_scan_at_least_a_window_old()
    {
        await using var store = await StoreWithScansAsync((-90, 1), (-65, 2), (-30, 3));

        var baseline = await store.StatementBaselineAsync("shop", Now, TimeSpan.FromHours(1));

        Assert.NotNull(baseline);
        Assert.Equal(Now.AddMinutes(-65), baseline.CapturedAt);
        Assert.Equal(new StatementSample("q", 2, 20), baseline.Samples["q"]);
    }

    [Fact]
    public async Task A_young_server_subtracts_its_first_scan()
    {
        await using var store = await StoreWithScansAsync((-20, 1), (-10, 2));

        var baseline = await store.StatementBaselineAsync("shop", Now, TimeSpan.FromHours(1));

        Assert.Equal(Now.AddMinutes(-20), baseline?.CapturedAt);
    }

    [Fact]
    public async Task A_baseline_older_than_two_windows_is_not_passed_off_as_the_last_hour()
    {
        await using var store = await StoreWithScansAsync((-180, 1));

        Assert.Null(await store.StatementBaselineAsync("shop", Now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task A_scan_without_counters_is_not_a_baseline()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);
        await store.SaveScanAsync("shop", Report(Now.AddMinutes(-70)));

        Assert.Null(await store.StatementBaselineAsync("shop", Now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task Trimming_drops_old_counters_and_keeps_the_scans()
    {
        await using var store = await StoreWithScansAsync((-300, 1), (-10, 2));

        var removed = await store.TrimStatementSamplesAsync(Now - MomusStore.StatementSampleRetention);

        Assert.Equal(1, removed);
        Assert.Equal(2, await store.ScanCountAsync("shop"));
    }

    // ---- fixtures ----------------------------------------------------------------------

    private static async Task<MomusStore> StoreWithScansAsync(params (int Minutes, long Calls)[] scans)
    {
        var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);

        foreach (var (minutes, calls) in scans)
        {
            await store.SaveScanAsync("shop", Report(Now.AddMinutes(minutes)),
                [new StatementSample("q", calls, calls * 10)]);
        }

        return store;
    }

    private static Task AddTargetAsync(MomusStore store) => store.UpsertTargetAsync(new StoredTarget
    {
        Id = "shop", Name = "shop", Provider = "postgres",
        ConnectionString = "Host=localhost;Database=shop", Source = "cli",
    });

    private static StatementBaseline Baseline(DateTimeOffset at, params (string Key, long Calls, double TotalMs)[] samples) =>
        new(at, samples.ToDictionary(s => s.Key, s => new StatementSample(s.Key, s.Calls, s.TotalMs)));

    private static IReadOnlyList<Finding> Statements(StatementActivity.Result result) =>
        result.Report.Checks.Single(c => StatementActivity.CheckIds.Contains(c.CheckId)).Findings;

    private static Finding Pg(string key, long calls, double totalMs, string text = "select ... from orders where id = ?") => new()
    {
        CheckId = "pg.top_queries",
        Severity = Severity.Info,
        Title = "#? query by total time",
        Detail = "",
        Subjects = [Subject.ForQuery(key)],
        Evidence = new Dictionary<string, object?>
        {
            ["normalized"] = text, ["calls"] = calls, ["total_exec_ms"] = totalMs, ["mean_exec_ms"] = totalMs / calls,
        },
    };

    private static Finding Mssql(string key, long executions, long cpuMs) => new()
    {
        CheckId = "mssql.top_cpu_queries",
        Severity = Severity.Info,
        Title = "#? query by CPU",
        Detail = "",
        Subjects = [Subject.ForQuery(key)],
        Evidence = new Dictionary<string, object?>
        {
            ["normalized"] = "select ... from orders where id = ?",
            ["execution_count"] = executions, ["total_cpu_ms"] = cpuMs, ["avg_cpu_ms"] = (double)cpuMs / executions,
        },
    };

    private static CheckResult Check(string id, params Finding[] findings) => new()
    {
        CheckId = id, Title = id, Category = "queries", Succeeded = true,
        Duration = TimeSpan.FromMilliseconds(5), Findings = findings,
    };

    private static ScanReport Report(params Finding[] findings) => Report(Now, findings);

    private static ScanReport Report(DateTimeOffset startedAt, params Finding[] findings) => new()
    {
        StartedAt = startedAt,
        Duration = TimeSpan.FromMilliseconds(120),
        Target = new TargetInfo { Provider = "postgres", DatabaseName = "shop", ServerVersion = "PostgreSQL 18.4" },
        Checks = [Check(findings.FirstOrDefault()?.CheckId ?? "pg.top_queries", findings)],
    };
}
