using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Momus.Core;
using Momus.Server;
using Momus.Server.Store;
using Momus.Tests.Fakes;

namespace Momus.Tests;

/// <summary>
/// The scheduler's contract is dull on purpose: it scans what it is told to scan, it writes down
/// what happened, and nothing that goes wrong is allowed to stop the loop.
/// </summary>
public class SchedulerTests
{
    [Fact]
    public async Task A_new_target_is_scanned_without_waiting_for_the_interval()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store, "shop");

        // An interval far longer than the test: if this passes, it is because a target that has
        // never been scanned is due immediately.
        using var scheduler = Scheduler(store, TimeSpan.FromHours(1), Findings(Severity.High, "trouble"));
        await scheduler.StartAsync(CancellationToken.None);

        var scan = await Eventually(() => store.LatestScanAsync("shop"));
        await scheduler.StopAsync(CancellationToken.None);

        Assert.True(scan.Succeeded);
        Assert.Equal("trouble", Assert.Single(await store.LatestFindingsAsync("shop")).Title);
    }

    [Fact]
    public async Task Scan_now_runs_a_target_that_is_not_due()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store, "shop");
        using var scheduler = Scheduler(store, TimeSpan.FromHours(1), Findings(Severity.Low, "first"));

        await scheduler.StartAsync(CancellationToken.None);
        await Eventually(() => store.LatestScanAsync("shop"));

        scheduler.RequestScan("shop");
        var twice = await Eventually(async () => await store.ScanCountAsync("shop") >= 2 ? "yes" : null);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal("yes", twice);
    }

    [Fact]
    public async Task A_target_that_cannot_be_reached_is_recorded_and_the_others_still_run()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await AddTargetAsync(store, "broken");
        await AddTargetAsync(store, "healthy");

        var factory = new FakeFactory(connectionString =>
            connectionString.Contains("broken")
                ? throw new InvalidOperationException("28P01: password authentication failed")
                : new FakeTarget(Findings(Severity.Medium, "fine")));

        using var scheduler = new ScanScheduler(
            store, new ServerOptions { ScanInterval = TimeSpan.FromHours(1) }, factory,
            NullLogger<ScanScheduler>.Instance);

        await scheduler.StartAsync(CancellationToken.None);
        await Eventually(() => store.LatestScanAsync("healthy"));
        var broken = await Eventually(async () =>
            (await store.TargetsAsync()).FirstOrDefault(t => t.Id == "broken" && t.LastError is not null));
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Contains("password authentication failed", broken.LastError);
        Assert.True((await store.LatestScanAsync("healthy"))!.Succeeded);
    }

    // ---- helpers -----------------------------------------------------------------------

    private static ScanScheduler Scheduler(MomusStore store, TimeSpan interval, IReadOnlyList<Finding> findings) =>
        new(store, new ServerOptions { ScanInterval = interval },
            new FakeFactory(_ => new FakeTarget(findings)), NullLogger<ScanScheduler>.Instance);

    private static IReadOnlyList<Finding> Findings(Severity severity, string title) =>
    [
        new Finding
        {
            CheckId = "fake.check", Severity = severity, Title = title, Detail = "detail",
            Subjects = [Subject.ForTable("public.orders")],
        },
    ];

    private static Task AddTargetAsync(MomusStore store, string id) => store.UpsertTargetAsync(new StoredTarget
    {
        Id = id, Name = id, Provider = "postgres",
        ConnectionString = $"Host=localhost;Database={id}", Source = "cli",
    });

    /// <summary>Polls until the value appears, so the test does not depend on the tick length.</summary>
    private static async Task<T> Eventually<T>(Func<Task<T?>> read, int seconds = 10) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await read() is { } value) return value;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Nothing appeared within {seconds}s.");
    }

    private sealed class FakeFactory(Func<string, IScanTarget> create) : IScanTargetFactory
    {
        public IReadOnlyList<string> Providers { get; } = ["postgres"];
        public IScanTarget Create(string provider, string connectionString) => create(connectionString);
    }

    private sealed class FakeTarget(IReadOnlyList<Finding> findings) : IScanTarget
    {
        public string Provider => "postgres";
        public DbConnection CreateConnection() => new FakeDataConnection();
        public IReadOnlyList<IDiagnosticCheck> Checks { get; } = [new FakeCheck(findings)];

        public Task<TargetInfo> GetTargetInfoAsync(DbConnection connection, CancellationToken ct) =>
            Task.FromResult(new TargetInfo
            {
                Provider = "postgres", DatabaseName = "testdb", ServerVersion = "PostgreSQL 18.4",
            });
    }

    private sealed class FakeCheck(IReadOnlyList<Finding> findings) : IDiagnosticCheck
    {
        public string Id => "fake.check";
        public string Title => "Fake check";
        public string Category => "queries";

        public Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct) =>
            Task.FromResult(findings);
    }
}
