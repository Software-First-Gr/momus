using Momus.Core;
using Momus.Server.Store;

namespace Momus.Tests;

/// <summary>
/// What the server adds over the CLI is memory, so these tests are about the passage of time:
/// a finding keeps the date it was first seen, and stops being current when it stops appearing.
/// </summary>
public class StoreTests
{
    [Fact]
    public async Task A_scan_round_trips_with_its_findings_and_subjects()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);

        await store.SaveScanAsync("shop", Report(At(0),
            Finding(Severity.High, "Table public.orders is scanned", Subject.ForTable("public.orders")),
            Finding(Severity.Info, "#1 query by total time", Subject.ForQuery("9f3a1c77d02b4e10"))));

        var scan = await store.LatestScanAsync("shop");
        Assert.NotNull(scan);
        Assert.True(scan.Succeeded);
        Assert.Equal(1, scan.CheckCount);
        Assert.Equal(2, scan.FindingCount);
        Assert.Equal("shop_db", scan.DatabaseName);

        var findings = await store.LatestFindingsAsync("shop");
        Assert.Equal(2, findings.Count);

        var high = findings[0];
        Assert.Equal(Severity.High, high.Severity);
        Assert.Equal("queries", high.Category);
        Assert.Equal("look at it", high.Recommendation);
        Assert.Contains("\"rows\":42", high.EvidenceJson);
        Assert.Equal(Subject.ForTable("public.orders"), Assert.Single(high.Subjects));
        Assert.True(high.IsNew);
    }

    [Fact]
    public async Task A_finding_keeps_its_first_seen_date_and_gets_a_new_last_seen()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);

        // The same problem, seen twice, an hour apart. The title moves because the check writes
        // live numbers into it; the subject does not, and that is what identity is built on.
        await store.SaveScanAsync("shop", Report(At(0),
            Finding(Severity.Medium, "Table public.orders scanned 100 times", Subject.ForTable("public.orders"))));

        await store.SaveScanAsync("shop", Report(At(60),
            Finding(Severity.High, "Table public.orders scanned 9000 times", Subject.ForTable("public.orders"))));

        var finding = Assert.Single(await store.LatestFindingsAsync("shop"));

        Assert.Equal(At(0), finding.FirstSeen);
        Assert.Equal(At(60), finding.LastSeen);
        Assert.Equal(2, finding.SeenCount);
        Assert.False(finding.IsNew);

        // The current scan's version of the finding is what is shown.
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal("Table public.orders scanned 9000 times", finding.Title);
    }

    [Fact]
    public async Task Two_scans_of_the_same_check_do_not_merge_findings_about_different_things()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);

        await store.SaveScanAsync("shop", Report(At(0),
            Finding(Severity.Medium, "orders", Subject.ForTable("public.orders")),
            Finding(Severity.Medium, "carts", Subject.ForTable("public.carts"))));

        var findings = await store.LatestFindingsAsync("shop");
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(1, f.SeenCount));
    }

    [Fact]
    public async Task A_finding_that_stops_appearing_is_no_longer_current()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);

        await store.SaveScanAsync("shop", Report(At(0),
            Finding(Severity.High, "still here", Subject.ForTable("public.orders")),
            Finding(Severity.High, "gone tomorrow", Subject.ForSession(4412))));

        await store.SaveScanAsync("shop", Report(At(60),
            Finding(Severity.High, "still here", Subject.ForTable("public.orders"))));

        var current = await store.LatestFindingsAsync("shop");
        Assert.Equal("still here", Assert.Single(current).Title);
        Assert.Equal(2, await store.ScanCountAsync("shop"));
    }

    [Fact]
    public async Task A_check_that_failed_is_kept_so_it_is_never_silent()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);

        await store.SaveScanAsync("shop", new ScanReport
        {
            StartedAt = At(0),
            Duration = TimeSpan.FromMilliseconds(30),
            Target = new TargetInfo { Provider = "postgres", DatabaseName = "shop_db" },
            Checks =
            [
                new CheckResult
                {
                    CheckId = "pg.top_queries", Title = "Top queries", Category = "queries",
                    Succeeded = false, Duration = TimeSpan.Zero,
                    Error = "permission denied for view pg_stat_statements",
                },
            ],
        });

        var failure = Assert.Single(await store.LatestCheckFailuresAsync("shop"));
        Assert.Equal("pg.top_queries", failure.CheckId);
        Assert.Contains("permission denied", failure.Error);
    }

    [Fact]
    public async Task A_scan_that_could_not_run_at_all_is_recorded_on_the_target()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);

        await store.RecordScanFailureAsync("shop", "28P01: password authentication failed");

        var target = Assert.Single(await store.TargetsAsync());
        Assert.Contains("password authentication failed", target.LastError);
        Assert.NotNull(target.LastScanAt);

        // A failed scan must not be mistaken for a clean one.
        Assert.Empty(await store.LatestFindingsAsync("shop"));
    }

    [Fact]
    public async Task A_successful_scan_clears_the_previous_error()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);

        await store.RecordScanFailureAsync("shop", "connection refused");
        await store.SaveScanAsync("shop", Report(At(0),
            Finding(Severity.Low, "fine", Subject.ForServer())));

        Assert.Null(Assert.Single(await store.TargetsAsync()).LastError);
    }

    [Fact]
    public async Task Targets_can_be_added_updated_and_removed()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store);

        await store.UpsertTargetAsync(new StoredTarget
        {
            Id = "shop", Name = "shop", Provider = "postgres",
            ConnectionString = "Host=host.docker.internal;Database=shop", Source = "env",
        });

        var target = Assert.Single(await store.TargetsAsync());
        Assert.Contains("host.docker.internal", target.ConnectionString);

        await store.RemoveTargetAsync("shop");
        Assert.Empty(await store.TargetsAsync());
    }

    [Fact]
    public async Task Initializing_twice_applies_the_schema_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"momus-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = new MomusStore(path))
            {
                await store.InitializeAsync();
                await store.InitializeAsync();
                await AddTargetAsync(store);
            }

            // Reopening the same file finds what was written: this is the volume, after a restart.
            await using var reopened = new MomusStore(path);
            await reopened.InitializeAsync();
            Assert.Single(await reopened.TargetsAsync());
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    // ---- fixtures ----------------------------------------------------------------------

    private static DateTimeOffset At(int minutes) =>
        new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static Task AddTargetAsync(MomusStore store) => store.UpsertTargetAsync(new StoredTarget
    {
        Id = "shop", Name = "shop", Provider = "postgres",
        ConnectionString = "Host=localhost;Database=shop", Source = "cli",
    });

    private static Finding Finding(Severity severity, string title, params Subject[] subjects) => new()
    {
        CheckId = "pg.seq_scan_heavy_tables",
        Severity = severity,
        Title = title,
        Detail = "detail",
        Recommendation = "look at it",
        Subjects = subjects,
        Evidence = new Dictionary<string, object?> { ["rows"] = 42 },
    };

    private static ScanReport Report(DateTimeOffset startedAt, params Finding[] findings) => new()
    {
        StartedAt = startedAt,
        Duration = TimeSpan.FromMilliseconds(120),
        Target = new TargetInfo
        {
            Provider = "postgres", DatabaseName = "shop_db", ServerVersion = "PostgreSQL 18.4",
        },
        Checks =
        [
            new CheckResult
            {
                CheckId = "pg.seq_scan_heavy_tables", Title = "Sequential scans", Category = "queries",
                Succeeded = true, Duration = TimeSpan.FromMilliseconds(12), Findings = findings,
            },
        ],
    };
}
