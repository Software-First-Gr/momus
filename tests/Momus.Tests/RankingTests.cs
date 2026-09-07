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

        var unusedIndex = new Insight
        {
            Kind = "db_finding", Severity = Severity.Low, Title = "unused index", Detail = "",
            Subjects = [Subject.ForIndex("ix_order_lines_price"), Subject.ForTable("public.order_lines")],
        };
        var seqScan = new Insight
        {
            Kind = "db_finding", Severity = Severity.High, Title = "scanned", Detail = "",
            Subjects = [Subject.ForTable("public.order_lines")],
        };

        var ranked = InsightRanking.Rank([unusedIndex, seqScan], context);

        Assert.Equal("scanned", ranked.OrderByDescending(i => i.Score).First().Title);
        Assert.Equal(InsightRanking.Share(seqScan, context), InsightRanking.Share(unusedIndex, context), 5);
    }

    [Fact]
    public void A_server_wide_problem_is_hit_by_everything()
    {
        var context = new InsightSnapshot { QueryStats = [Stat("a", 10_000)] };

        var everywhere = new Insight
        {
            Kind = "db_finding", Severity = Severity.Medium, Title = "cache hit ratio", Detail = "",
            Subjects = [Subject.ForServer()],
        };

        Assert.Equal(1, InsightRanking.Share(everywhere, context), 5);
    }

    [Fact]
    public void A_subject_no_application_touches_still_scores()
    {
        // A database-side finding about a table nothing instrumented names is still a finding;
        // the traffic share is a multiplier, not a gate.
        var context = new InsightSnapshot { QueryStats = [Stat("other", 10_000)] };

        var ranked = InsightRanking.Rank([Insight(Severity.High, "unknown")], context);

        Assert.True(ranked[0].Score > 0);
        Assert.Equal(InsightRanking.MinShare, InsightRanking.Share(ranked[0], context), 5);
    }

    [Fact]
    public void With_no_application_reporting_at_all_severity_is_the_whole_order()
    {
        var context = new InsightSnapshot();

        var ranked = InsightRanking.Rank(
            [Insight(Severity.Low, "a"), Insight(Severity.Critical, "b")], context);

        Assert.True(ranked[1].Score > ranked[0].Score);
    }

    private static Insight Insight(Severity severity, string key) => new()
    {
        Kind = "n_plus_one",
        Severity = severity,
        Title = key,
        Detail = "",
        Subjects = [Subject.ForQuery(key)],
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
