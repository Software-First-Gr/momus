using Momus.Core;
using Momus.Core.Insights;
using Momus.Server.Store;

namespace Momus.Tests;

/// <summary>
/// Insights are re-derived every fifteen seconds, so the only thing the store adds is the thing
/// that cannot be re-derived: when this first became true.
/// </summary>
public class InsightStoreTests
{
    [Fact]
    public async Task An_insight_seen_again_keeps_the_day_it_started()
    {
        await using var store = await MomusStore.InMemoryAsync();

        // The same N+1, twice, an hour apart. The title moves because it carries live numbers.
        await store.SaveInsightsAsync("shop", [NPlusOne("×6 per call")], At(0));
        await store.SaveInsightsAsync("shop", [NPlusOne("×9 per call")], At(3600));

        var insight = Assert.Single(await store.CurrentInsightsAsync("shop"));
        Assert.Equal(At(0), insight.FirstSeen);
        Assert.Equal(At(3600), insight.LastSeen);
        Assert.Equal(2, insight.SeenCount);
        Assert.Equal("×9 per call", insight.Title);
        Assert.False(insight.IsNew);
    }

    [Fact]
    public async Task An_insight_that_stopped_firing_leaves_the_page_but_not_the_store()
    {
        await using var store = await MomusStore.InMemoryAsync();

        await store.SaveInsightsAsync("shop", [NPlusOne("×6 per call"), HotQuery()], At(0));
        await store.SaveInsightsAsync("shop", [HotQuery()], At(60));

        var current = Assert.Single(await store.CurrentInsightsAsync("shop"));
        Assert.Equal("hot_query_origin", current.Kind);

        // Fixed and then broken again is a different story from new, so the row is kept.
        await store.SaveInsightsAsync("shop", [NPlusOne("×6 per call"), HotQuery()], At(120));
        var returned = (await store.CurrentInsightsAsync("shop")).Single(i => i.Kind == "n_plus_one");
        Assert.Equal(At(0), returned.FirstSeen);
        Assert.Equal(2, returned.SeenCount);
    }

    [Fact]
    public async Task Insights_of_one_kind_about_different_things_are_different_insights()
    {
        await using var store = await MomusStore.InMemoryAsync();

        await store.SaveInsightsAsync("shop",
        [
            Passthrough("pg.cache_hit_ratio", "Cache hit ratio is 78%"),
            Passthrough("pg.connection_saturation", "34 of 40 connections in use"),
        ], At(0));

        // Both are about "server" and nothing else; only the discriminator tells them apart.
        Assert.Equal(2, (await store.CurrentInsightsAsync("shop")).Count);
    }

    [Fact]
    public async Task The_worst_insight_is_first()
    {
        await using var store = await MomusStore.InMemoryAsync();

        await store.SaveInsightsAsync("shop", [HotQuery(), NPlusOne("×6 per call")], At(0));

        var insights = await store.CurrentInsightsAsync("shop");
        Assert.Equal(Severity.Medium, insights[0].Severity);
        Assert.Equal("n_plus_one", insights[0].Kind);
    }

    // ---- fixtures ----------------------------------------------------------------------

    private static readonly DateTimeOffset Start = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => Start.AddSeconds(seconds);

    private static Insight NPlusOne(string title) => new()
    {
        Kind = "n_plus_one",
        Severity = Severity.Medium,
        Title = title,
        Detail = "It runs in a loop.",
        Subjects = [Subject.ForQuery("prod")],
        Evidence = new Dictionary<string, object?> { ["repeats_per_operation"] = 6 },
    };

    private static Insight HotQuery() => new()
    {
        Kind = "hot_query_origin",
        Severity = Severity.Info,
        Title = "#1 by total database time",
        Detail = "It is the top of the database's own list.",
        Subjects = [Subject.ForQuery("hot")],
    };

    private static Insight Passthrough(string checkId, string title) => new()
    {
        Kind = "db_finding",
        Discriminator = checkId,
        Severity = Severity.Medium,
        Title = title,
        Detail = "The database says so.",
        Subjects = [Subject.ForServer()],
    };
}
