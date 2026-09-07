using Momus.Core;
using Momus.Core.Insights;
using static System.FormattableString;

namespace Momus.Server.Insights;

/// <summary>
/// The statements the database spends most of its time on, each mapped back to the endpoint and
/// the line of code that runs it — or explicitly not mapped, when no instrumented app sent it.
/// </summary>
/// <remarks>
/// The fourth row of the Queries table in DESIGN.md is why the second case exists: a statement the
/// database is spending real time on that no application reported is a job, a migration or another
/// service, and saying so is more useful than leaving it off the page.
/// </remarks>
public sealed class HotQueryOriginInsight : IInsight
{
    /// <summary>
    /// The scan keeps fifty ranked statements so the Queries tab can join any of them. Fifty
    /// insights would be a list nobody reads; the ones worth a card are the ones at the top.
    /// </summary>
    public const int Top = 10;

    public string Kind => "hot_query_origin";

    public Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct)
    {
        var app = context.QueryStats.ToDictionary(q => q.Fingerprint, StringComparer.Ordinal);

        // Same order the check ranked them in, recomputed from the evidence it wrote, so the
        // insight can say "#1" without parsing a number back out of a title.
        var ranked = QueryJoin.FromFindings(context.LatestFindings)
            .OrderByDescending(d => d.Value.TotalMs)
            .Take(Top)
            .ToList();

        var insights = ranked
            .Select((entry, i) => Build(entry.Key, entry.Value, i + 1, app.GetValueOrDefault(entry.Key)))
            .ToList();

        return Task.FromResult<IReadOnlyList<Insight>>(insights);
    }

    private static Insight Build(string fingerprint, DatabaseView database, int rank, QueryStatView? app)
    {
        var cost =
            Invariant($"The database spent {database.TotalMs / 1000:N1}s of {database.Measure} on this ") +
            Invariant($"statement across {database.Calls:N0} calls, {database.MeanMs:N1} ms each.");

        return new Insight
        {
            Kind = "hot_query_origin",
            Severity = database.Severity,
            Title = app is null
                ? $"#{rank} by total database {database.Measure}, and no application reported it"
                : $"#{rank} by total database {database.Measure}, and it comes from {app.Operation}",
            Detail = app is null
                ? cost + " No instrumented application sent it, so it runs from a job, a migration, " +
                  "another service — or an app that has not added Momus.Client yet."
                : cost + $" The app side runs it from {app.Operation}" +
                  (app.CallSite is { } site ? $" at {site}" : "") +
                  Invariant($", {app.CallsPerMinute:N0} times a minute at {app.MeanMs:N1} ms."),
            Recommendation = app is null
                ? "Find what runs it before tuning it: the fix for a nightly job is not the fix for a route."
                : "This is where tuning pays back the most per hour spent — it is the top of the database's own list.",
            Subjects = [Subject.ForQuery(fingerprint)],
            Evidence = new Dictionary<string, object?>
            {
                ["rank"] = rank,
                ["measure"] = database.Measure,
                ["db_total_ms"] = Math.Round(database.TotalMs, 1),
                ["db_mean_ms"] = Math.Round(database.MeanMs, 2),
                ["db_calls"] = database.Calls,
                ["operation"] = app?.Operation,
                ["call_site"] = app?.CallSite,
                ["app_calls_per_minute"] = app is null ? null : Math.Round(app.CallsPerMinute, 1),
                ["app_mean_ms"] = app is null ? null : Math.Round(app.MeanMs, 2),
                ["statement"] = app?.Sample ?? database.Sample,
            },
        };
    }
}
