using Momus.Core;
using Momus.Core.Insights;
using Momus.Server;

namespace Momus.Tests;

/// <summary>
/// The join that is the product. The cases that matter are the asymmetric ones: a statement only
/// the app knows about, and one only the database does.
/// </summary>
public class QueryJoinTests
{
    [Fact]
    public void Both_halves_of_a_statement_land_on_one_row()
    {
        var rows = QueryJoin.Build(
            [Stat("9f3a", "GET /orders/{id}", calls: 4800, sumMs: 8640)],
            [Finding(Severity.High, "#1 query by total time", "9f3a", meanMs: 1.1, totalMs: 78000, calls: 70000)],
            "shop");

        var row = Assert.Single(rows);
        Assert.NotNull(row.App);
        Assert.NotNull(row.Database);
        Assert.Equal("GET /orders/{id}", row.App.Operation);
        Assert.Equal(1.8, row.App.MeanMs, 2);
        Assert.Equal(1.1, row.Database.MeanMs, 2);
        Assert.Equal("time", row.Database.Measure);
        Assert.Equal(Severity.High, row.Severity);
    }

    [Fact]
    public void A_statement_no_app_sent_is_still_a_row()
    {
        var rows = QueryJoin.Build(
            [Stat("9f3a", "GET /orders/{id}", calls: 10, sumMs: 20)],
            [
                Finding(Severity.High, "#1 query by total time", "9f3a", meanMs: 2, totalMs: 200, calls: 100),
                Finding(Severity.Medium, "#2 query by total time", "beef", meanMs: 2300, totalMs: 9200, calls: 4),
            ],
            "shop");

        Assert.Equal(2, rows.Count);

        // The database's own ranking wins the ordering, so an unexplained cost sits at the top.
        Assert.Equal("9f3a", rows[0].Fingerprint);
        var orphan = rows[1];
        Assert.Null(orphan.App);
        Assert.Equal("select ... from audit_log where created_at > ?", orphan.Sample);
        Assert.Equal(0, orphan.MaxRepeats);
    }

    [Fact]
    public void A_statement_the_database_does_not_rank_keeps_its_app_side_numbers()
    {
        var row = Assert.Single(QueryJoin.Build(
            [Stat("cafe", "POST /cart/items", calls: 300, sumMs: 1260)],
            [],
            "shop"));

        Assert.Null(row.Database);
        Assert.Equal(Severity.Info, row.Severity);
        Assert.Equal(4.2, row.App!.MeanMs, 2);
    }

    [Fact]
    public void Cpu_and_execution_time_are_labelled_rather_than_averaged_together()
    {
        var finding = new FindingView
        {
            CheckId = "mssql.top_cpu_queries",
            Category = "queries",
            Severity = Severity.Medium,
            Title = "#1 query by CPU",
            Detail = "",
            EvidenceJson = """{"normalized":"select ...","avg_cpu_ms":12.5,"total_cpu_ms":9000,"execution_count":720}""",
            Subjects = [Subject.ForQuery("dead")],
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            SeenCount = 3,
        };

        var view = Assert.Single(QueryJoin.FromFindings([finding])).Value;
        Assert.Equal("CPU", view.Measure);
        Assert.Equal(12.5, view.MeanMs);
        Assert.Equal(720, view.Calls);
    }

    [Fact]
    public void A_statement_against_another_database_is_not_shown_on_this_target()
    {
        var rows = QueryJoin.Build(
            [Stat("9f3a", "GET /orders/{id}", calls: 10, sumMs: 20), Stat("beef", "GET /x", 5, 5, target: "billing")],
            [],
            "shop");

        Assert.Equal("9f3a", Assert.Single(rows).Fingerprint);
    }

    // ---- fixtures ----------------------------------------------------------------------

    private static QueryStatView Stat(string key, string operation, long calls, double sumMs, string target = "shop") =>
        new()
        {
            Fingerprint = key,
            TargetId = target,
            Operation = operation,
            OperationCount = 1,
            CallSite = "OrdersHandler.cs:42",
            Sample = "select ... from order_lines where order_id = ?",
            Calls = calls,
            TotalMs = sumMs,
            MeanMs = calls == 0 ? 0 : sumMs / calls,
            CallsPerMinute = calls,
        };

    private static string Invariant(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static FindingView Finding(
        Severity severity, string title, string key, double meanMs, double totalMs, long calls) => new()
    {
        CheckId = "pg.top_queries",
        Category = "queries",
        Severity = severity,
        Title = title,
        Detail = "",
        // Formatted invariantly on purpose: this is JSON, and a Greek decimal comma is not.
        EvidenceJson = $$"""
            {"normalized":"select ... from audit_log where created_at > ?",
             "mean_exec_ms":{{Invariant(meanMs)}}, "total_exec_ms":{{Invariant(totalMs)}}, "calls":{{calls}}}
            """,
        Subjects = [Subject.ForQuery(key)],
        FirstSeen = DateTimeOffset.UtcNow.AddHours(-3),
        LastSeen = DateTimeOffset.UtcNow,
        SeenCount = 4,
    };
}
