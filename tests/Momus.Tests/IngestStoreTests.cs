using Momus.Core.Ingest;
using Momus.Server.Store;

namespace Momus.Tests;

/// <summary>
/// The application half of the store. What matters here is that a stretch of windows folds back
/// into the numbers the app actually produced — calls, time and the worst repeat count — because
/// every insight in M2.4 is a threshold over exactly those.
/// </summary>
public class IngestStoreTests
{
    [Fact]
    public async Task A_batch_round_trips_into_an_app_a_window_and_its_rows()
    {
        await using var store = await MomusStore.InMemoryAsync();

        await store.SaveIngestBatchAsync(Batch(At(-5), At(0),
            queries: [Query("9f3a1c77d02b4e10", "GET /orders/{id}", calls: 40, sumMs: 72, repeats: 40)],
            operations: [Operation("GET /orders/{id}", calls: 1, sumMs: 120, queries: 41, dbMs: 74)]));

        var app = Assert.Single(await store.AppsAsync());
        Assert.Equal("shop-api", app.Id);
        Assert.Equal("Shop.Api", app.Name);
        Assert.Equal("1.4.2", app.Version);
        Assert.Equal("Development", app.Environment);
        Assert.Equal(1, app.InstanceCount);

        var window = await store.LatestWindowAsync();
        Assert.NotNull(window);
        Assert.Equal(At(-5), window.From);
        Assert.Equal(At(0), window.To);

        var query = Assert.Single(await store.AppQueryStatsAsync(At(-60)));
        Assert.Equal("9f3a1c77d02b4e10", query.Fingerprint);
        Assert.Equal("GET /orders/{id}", query.Operation);
        Assert.Equal("OrdersHandler.cs:42", query.CallSite);
        Assert.Equal("select ... from order_lines where order_id = ?", query.Sample);
        Assert.Equal(40, query.Calls);
        Assert.Equal(1.8, query.MeanMs, 3);
        Assert.Equal(40, query.MaxRepeats);

        var operation = Assert.Single(await store.AppOperationStatsAsync(At(-60)));
        Assert.Equal("http", operation.Kind);
        Assert.Equal(41, operation.MeanQueries);
        Assert.Equal(0.617, operation.DbShare, 3);
    }

    [Fact]
    public async Task Windows_add_up_and_the_busiest_operation_names_the_row()
    {
        await using var store = await MomusStore.InMemoryAsync();

        // The same statement from two endpoints. The row should be named by the one that runs it
        // most, and still count both — a query's cost is not one endpoint's cost.
        await store.SaveIngestBatchAsync(Batch(At(-10), At(-5),
            queries:
            [
                Query("abc", "GET /orders/{id}", calls: 40, sumMs: 80, repeats: 40),
                Query("abc", "GET /search", calls: 4, sumMs: 200, repeats: 1, callSite: "SearchQuery.cs:17"),
            ]));

        await store.SaveIngestBatchAsync(Batch(At(-5), At(0),
            queries: [Query("abc", "GET /orders/{id}", calls: 60, sumMs: 120, repeats: 12)]));

        var query = Assert.Single(await store.AppQueryStatsAsync(At(-60)));
        Assert.Equal("GET /orders/{id}", query.Operation);
        Assert.Equal(2, query.OperationCount);
        Assert.Equal(104, query.Calls);
        Assert.Equal(400, query.DurationSum);
        Assert.Equal(40, query.MaxRepeats);

        // Ten seconds since the first window, so calls per minute is about six times the count.
        Assert.InRange(query.CallsPerMinute, 590, 630);
    }

    [Fact]
    public async Task Only_windows_inside_the_asked_for_stretch_are_counted()
    {
        await using var store = await MomusStore.InMemoryAsync();

        await store.SaveIngestBatchAsync(Batch(At(-7200), At(-7195),
            queries: [Query("old", "GET /orders/{id}", calls: 1000, sumMs: 1000, repeats: 1)]));
        await store.SaveIngestBatchAsync(Batch(At(-30), At(-25),
            queries: [Query("new", "GET /orders/{id}", calls: 10, sumMs: 10, repeats: 1)]));

        var recent = await store.AppQueryStatsAsync(At(-3600));
        Assert.Equal("new", Assert.Single(recent).Fingerprint);
        Assert.Equal(2, (await store.AppQueryStatsAsync(Everything)).Count);
    }

    [Fact]
    public async Task Rollup_folds_a_day_of_windows_into_hourly_rows_without_losing_the_numbers()
    {
        await using var store = await MomusStore.InMemoryAsync();

        // Two windows in the same hour, two days ago, plus one from just now that must survive.
        var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(2);
        await store.SaveIngestBatchAsync(Batch(old, old.AddSeconds(5),
            queries: [Query("abc", "GET /orders/{id}", calls: 40, sumMs: 80, repeats: 40)],
            operations: [Operation("GET /orders/{id}", calls: 1, sumMs: 120, queries: 41, dbMs: 74)]));
        await store.SaveIngestBatchAsync(Batch(old.AddSeconds(5), old.AddSeconds(10),
            queries: [Query("abc", "GET /orders/{id}", calls: 60, sumMs: 120, repeats: 12)],
            operations: [Operation("GET /orders/{id}", calls: 1, sumMs: 100, queries: 61, dbMs: 90)]));
        await store.SaveIngestBatchAsync(Batch(At(-10), At(-5),
            queries: [Query("abc", "GET /orders/{id}", calls: 5, sumMs: 10, repeats: 2)]));

        var folded = await store.RollupAsync(DateTimeOffset.UtcNow - MomusStore.RawRetention);
        Assert.Equal(2, folded);

        // Everything still adds up, including the window the rollup was not allowed to touch.
        var query = Assert.Single(await store.AppQueryStatsAsync(Everything));
        Assert.Equal(105, query.Calls);
        Assert.Equal(210, query.DurationSum);
        Assert.Equal(40, query.MaxRepeats);

        var operation = Assert.Single(await store.AppOperationStatsAsync(Everything));
        Assert.Equal(2, operation.Calls);
        Assert.Equal(220, operation.DurationSum);
        Assert.Equal(164, operation.DbMs);

        // A second pass has nothing left to do: the raw rows are gone, the hourly ones stay.
        Assert.Equal(0, await store.RollupAsync(DateTimeOffset.UtcNow - MomusStore.RawRetention));
    }

    [Fact]
    public async Task Trim_drops_rolled_up_windows_past_the_retention_limit()
    {
        await using var store = await MomusStore.InMemoryAsync();

        var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
        await store.SaveIngestBatchAsync(Batch(old, old.AddSeconds(5),
            queries: [Query("abc", "GET /orders/{id}", calls: 40, sumMs: 80, repeats: 40)]));

        await store.RollupAsync(DateTimeOffset.UtcNow - MomusStore.RawRetention);
        Assert.Single(await store.AppQueryStatsAsync(Everything));

        var removed = await store.TrimAsync(DateTimeOffset.UtcNow - MomusStore.RollupRetention);
        Assert.Equal(1, removed);
        Assert.Empty(await store.AppQueryStatsAsync(Everything));
    }

    [Fact]
    public void A_batch_that_could_not_be_joined_to_anything_is_refused()
    {
        Assert.NotNull(Momus.Server.IngestHandler.Validate(null));
        Assert.NotNull(Momus.Server.IngestHandler.Validate(Batch(At(-5), At(0)) with
        {
            App = new IngestApp("", null, null, null),
        }));
        Assert.NotNull(Momus.Server.IngestHandler.Validate(Batch(At(0), At(-5))));
        Assert.Null(Momus.Server.IngestHandler.Validate(Batch(At(-5), At(0))));
    }

    // ---- fixtures ----------------------------------------------------------------------

    /// <summary>One instant per test, so two calls to <see cref="At"/> mean the same thing.</summary>
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private DateTimeOffset At(int seconds) => _now.AddSeconds(seconds);

    /// <summary>A "since" that excludes nothing, for the tests about folding rather than filtering.</summary>
    private static readonly DateTimeOffset Everything = DateTimeOffset.UnixEpoch;

    private static IngestBatch Batch(
        DateTimeOffset from, DateTimeOffset to,
        IReadOnlyList<IngestQuery>? queries = null,
        IReadOnlyList<IngestOperation>? operations = null) => new()
    {
        App = new IngestApp("Shop.Api", "1.4.2", "web-01:4412", "Development"),
        Window = new IngestWindow(from, to),
        Queries = queries ?? [],
        Operations = operations ?? [],
    };

    private static IngestQuery Query(
        string key, string operation, long calls, double sumMs, int repeats,
        string callSite = "OrdersHandler.cs:42") =>
        new(key, "shop", operation, callSite,
            "select ... from order_lines where order_id = ?",
            calls, new Timing(sumMs, sumMs, [0, calls, 0, 0, 0, 0, 0, 0]), calls * 4, repeats, 0);

    private static IngestOperation Operation(string name, long calls, double sumMs, long queries, double dbMs) =>
        new(name, "http", calls, new Stat(sumMs, sumMs), new Counts(queries, queries), new Stat(dbMs, 0));
}
