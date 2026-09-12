using Momus.Core;
using Momus.Core.Insights;
using Momus.Server.Insights;
using Momus.Tests.Fakes;

namespace Momus.Tests;

/// <summary>
/// The one number that decides what sits in the five "Fix first" slots. The whole point of it is
/// the case where severity alone gets the answer wrong.
/// </summary>
public class RankingTests
{
    [Fact]
    public void A_Critical_nothing_calls_loses_to_a_High_on_the_busiest_endpoint()
    {
        // The example DESIGN.md gives for why a score exists at all.
        var context = new InsightSnapshot
        {
            QueryStats =
            [
                Stat("busy", calls: 9_900),
                Stat("rare", calls: 100),
            ],
        };

        var ranked = InsightRanking.Rank(
            [Insight(Severity.Critical, "rare"), Insight(Severity.High, "busy")], context);

        Assert.Equal("busy", ranked.OrderByDescending(i => i.Score).First().Subjects[0].Key);
    }

    [Fact]
    public void With_equal_traffic_severity_decides()
    {
        var context = new InsightSnapshot
        {
            QueryStats = [Stat("a", 5_000), Stat("b", 5_000)],
        };

        var ranked = InsightRanking.Rank(
            [Insight(Severity.Medium, "a"), Insight(Severity.Critical, "b")], context);

        Assert.Equal("b", ranked.OrderByDescending(i => i.Score).First().Subjects[0].Key);
    }

    [Fact]
    public void Something_that_started_today_outranks_the_same_thing_from_last_month()
    {
        var context = new InsightSnapshot { QueryStats = [Stat("a", 1_000), Stat("b", 1_000)] };

        var fresh = Insight(Severity.High, "a");
        var old = Insight(Severity.High, "b");

        var ranked = InsightRanking.Rank([fresh, old], context, new Dictionary<string, DateTimeOffset>
        {
            [fresh.IdentityKey] = context.Now,
            [old.IdentityKey] = context.Now.AddDays(-30),
        });

        Assert.True(ranked[0].Score > ranked[1].Score);

        // And not by so much that recency could ever beat a whole severity level.
        Assert.True(ranked[0].Score < ranked[1].Score * 2);
    }

    [Fact]
    public void An_extra_subject_does_not_make_an_insight_about_the_whole_application()
    {
        // Found on the running demo: an unused-index finding carries index: and table:, and
        // summing the two scored it as if it were about everything — which put a Low unused index
        // above a High sequential scan on the same table.
        var context = new InsightSnapshot
        {
            QueryStats = [Stat("hot", 8_000, "select ... from other o"), Stat("lines", 2_000)],
        };

        var unusedIndex = DbFinding(Severity.Low, "unused index",
            Subject.ForIndex("ix_order_lines_price"), Subject.ForTable("public.order_lines"));
        var seqScan = DbFinding(Severity.High, "scanned", Subject.ForTable("public.order_lines"));

        var ranked = InsightRanking.Rank([unusedIndex, seqScan], context);

        Assert.Equal("scanned", ranked.OrderByDescending(i => i.Score).First().Title);
        Assert.Equal(InsightRanking.Share(seqScan, context), InsightRanking.Share(unusedIndex, context));
    }

    [Fact]
    public void A_server_wide_problem_is_neither_all_of_the_traffic_nor_none_of_it()
    {
        var context = new InsightSnapshot { QueryStats = [Stat("a", 10_000)] };

        var everywhere = DbFinding(Severity.Medium, "cache hit ratio", Subject.ForServer());
        var session = DbFinding(Severity.High, "idle in transaction", Subject.ForSession(44));

        Assert.Null(InsightRanking.Share(everywhere, context));
        Assert.Equal(InsightRanking.NeutralReach, InsightRanking.Reach(everywhere, context));
        Assert.Equal(InsightRanking.NeutralReach, InsightRanking.Reach(session, context));
    }

    [Fact]
    public void A_table_no_application_touches_still_scores()
    {
        // A database-side finding about a table nothing instrumented names is still a finding;
        // reach is a multiplier, not a gate.
        var context = new InsightSnapshot { QueryStats = [Stat("other", 10_000, "select ? from carts c")] };

        var ranked = InsightRanking.Rank([DbFinding(Severity.High, "scanned", Subject.ForTable("public.audit_log"))], context);

        Assert.True(ranked[0].Score > 0);
        Assert.Equal(0, InsightRanking.Share(ranked[0], context));
        Assert.Equal(1, InsightRanking.Reach(ranked[0], context));
    }

    [Fact]
    public void A_Low_server_wide_ratio_sits_below_the_loop_and_the_scanned_table()
    {
        // Fix first on the demo, 2026-09-12: "Buffer cache hit ratio is 98.9 %" (Low, new) was
        // second, above the N+1 on the busiest endpoint and a High sequential scan.
        var context = DemoTraffic();
        var buffer = DbFinding(Severity.Low, "buffer cache", Subject.ForServer());
        var loop = new Insight
        {
            Kind = "n_plus_one", Severity = Severity.Medium, Title = "loop", Detail = "",
            Subjects = [Subject.ForQuery("product-by-id")],
        };
        var scanned = DbFinding(Severity.High, "order_lines scanned", Subject.ForTable("public.order_lines"));

        var ranked = InsightRanking.Rank([buffer, loop, scanned], context, new Dictionary<string, DateTimeOffset>
        {
            [buffer.IdentityKey] = context.Now,
            [loop.IdentityKey] = context.Now.AddDays(-5),
            [scanned.IdentityKey] = context.Now.AddDays(-5),
        });

        Assert.Equal(["order_lines scanned", "loop", "buffer cache"],
            ranked.OrderByDescending(i => i.Score).Select(i => i.Title));
    }

    [Fact]
    public void A_High_that_started_minutes_ago_on_a_session_outranks_an_Info_card_on_a_busy_query()
    {
        // Fix first on the demo, 2026-09-12: a transaction idle for five minutes (High, new) was
        // fifth, below "#8 by total database time" (Info) on the busiest statement.
        var context = DemoTraffic();
        var idle = DbFinding(Severity.High, "idle in transaction", Subject.ForSession(44));
        var hot = new Insight
        {
            Kind = "hot_query_origin", Severity = Severity.Info, Title = "hot", Detail = "",
            Subjects = [Subject.ForQuery("product-by-id")],
        };

        var ranked = InsightRanking.Rank([hot, idle], context, new Dictionary<string, DateTimeOffset>
        {
            [idle.IdentityKey] = context.Now,
            [hot.IdentityKey] = context.Now.AddDays(-5),
        });

        Assert.Equal("idle in transaction", ranked.OrderByDescending(i => i.Score).First().Title);
    }

    [Fact]
    public void With_no_application_reporting_at_all_severity_is_the_whole_order()
    {
        var context = new InsightSnapshot();

        var ranked = InsightRanking.Rank(
            [Insight(Severity.Low, "a"), Insight(Severity.Critical, "b")], context);

        Assert.True(ranked[1].Score > ranked[0].Score);
    }

    /// <summary>Roughly the demo's calls per minute under heavy traffic.</summary>
    private static InsightSnapshot DemoTraffic() => new()
    {
        QueryStats =
        [
            Stat("product-by-id", 1_519, "select p.id from products p where p.id = ?"),
            Stat("lines-by-order", 253, "select o.id from order_lines o where o.order_id = ?"),
            Stat("cart-update", 378, "update carts c set total = c.total + ? where c.id = ?"),
            Stat("search", 100, "select p.id from products p where lower (p.name) like ?"),
        ],
    };

    private static Insight Insight(Severity severity, string key) => new()
    {
        Kind = "n_plus_one",
        Severity = severity,
        Title = key,
        Detail = "",
        Subjects = [Subject.ForQuery(key)],
    };

    private static Insight DbFinding(Severity severity, string title, params Subject[] subjects) => new()
    {
        Kind = "db_finding",
        Severity = severity,
        Title = title,
        Detail = "",
        Subjects = subjects,
    };

    private static QueryStatView Stat(
        string key, long calls, string sample = "select ? from order_lines l") => new()
    {
        Fingerprint = key,
        Operation = "GET /x",
        Calls = calls,
        Sample = sample,
    };
}
