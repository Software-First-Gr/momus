using Momus.Core;
using Momus.Core.Insights;
using static System.FormattableString;

namespace Momus.Server.Insights;

/// <summary>
/// A statement that got slower with a deploy. This is the insight that needs history and nothing
/// else can produce: the database knows a query is slow, the application knows which endpoint runs
/// it, and only something that has been watching since Tuesday knows it was fine on Monday.
/// </summary>
/// <remarks>
/// No deploy has to be reported to Momus for this to work. A version appearing on an application
/// instance for the first time is the marker, and that date is already in the store.
/// </remarks>
public sealed class RegressionInsight : IInsight
{
    /// <summary>Below this the difference is noise dressed up as a finding.</summary>
    public const double MinRatio = 3;

    /// <summary>Three times nothing is still nothing: a slowdown has to be felt as well as measured.</summary>
    public const double MinDeltaMs = 20;

    /// <summary>Fewer calls than this on either side and the mean is an anecdote.</summary>
    public const long MinCalls = 100;

    /// <summary>Past this, it is not a regression, it is an outage in slow motion.</summary>
    public const double CriticalRatio = 10;

    /// <summary>
    /// A version that has not reported for this long has stopped running. Plenty of a client's
    /// five-second windows, and short enough that a rollback clears the card within minutes.
    /// </summary>
    public static readonly TimeSpan RunningGrace = TimeSpan.FromMinutes(2);

    public string Kind => "regression";

    public Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct)
    {
        var insights = new List<Insight>();

        if (Compared(context.Deploys) is { } pair)
        {
            var (newest, previous) = pair;
            var database = QueryJoin.FromFindings(context.LatestFindings);

            var before = context.VersionStats.Where(v => v.Version == previous.Version)
                .ToDictionary(v => v.Fingerprint, StringComparer.Ordinal);

            foreach (var after in context.VersionStats.Where(v => v.Version == newest.Version))
            {
                if (!before.TryGetValue(after.Fingerprint, out var was)) continue;
                if (after.Calls < MinCalls || was.Calls < MinCalls) continue;
                if (was.MeanMs <= 0) continue;

                var ratio = after.MeanMs / was.MeanMs;
                var delta = after.MeanMs - was.MeanMs;
                if (ratio < MinRatio || delta < MinDeltaMs) continue;

                insights.Add(Build(after, was, newest, previous, ratio, delta,
                    database.GetValueOrDefault(after.Fingerprint), context));
            }
        }

        return Task.FromResult<IReadOnlyList<Insight>>(insights);
    }

    /// <summary>
    /// The deploy to judge, and the one to judge it against — or null with fewer than two versions.
    /// </summary>
    /// <remarks>
    /// The one judged is the most recently deployed version that is <em>still running</em>, not
    /// simply the last to appear. After a rollback the last to appear is the version that was taken
    /// away, and a card saying "got slower in 1.1.0" while 1.0.0 serves every request is wrong in
    /// the way that costs the most trust. During a rolling deploy both are running, and the new one
    /// is judged. It is compared against whichever other version ran most recently.
    /// </remarks>
    public static (DeployView Newest, DeployView Previous)? Compared(IReadOnlyList<DeployView> deploys)
    {
        if (deploys.Count < 2) return null;

        var lastReport = deploys.Max(d => d.LastSeen);
        var newest = deploys
            .Where(d => d.LastSeen >= lastReport - RunningGrace)
            .MaxBy(d => d.FirstSeen)!;
        var previous = deploys
            .Where(d => d.Version != newest.Version)
            .OrderByDescending(d => d.LastSeen)
            .ThenByDescending(d => d.FirstSeen)
            .First();

        return (newest, previous);
    }

    private static Insight Build(
        VersionStatView after, VersionStatView before, DeployView newest, DeployView previous,
        double ratio, double delta, DatabaseView? database, IInsightContext context)
    {
        // Anything the database started complaining about between the two deploys is a candidate
        // explanation, and it is the sort of thing nobody thinks to look for by hand. Not statements
        // — they enter and leave the hourly ranking all the time — and not Info, which explains
        // nothing: on the demo both put "0.1 ms across 2 calls" in this list.
        var since = context.LatestFindings
            .Where(f => f.FirstSeen >= previous.FirstSeen && f.FirstSeen <= newest.FirstSeen)
            .Where(f => f.Severity > Severity.Info && !f.Subjects.Any(s => s.Kind == Subject.Query))
            .OrderByDescending(f => (int)f.Severity)
            .Take(3)
            .Select(f => f.Title)
            .ToList();

        var where = after.CallSite is { Length: > 0 } site ? $" ({site})" : "";

        return new Insight
        {
            Kind = "regression",
            Severity = ratio >= CriticalRatio ? Severity.Critical : Severity.High,
            Title = Invariant($"A statement in {after.Operation ?? "an operation"} got {ratio:N1}× slower in {newest.Version}"),
            Detail =
                Invariant($"{after.MeanMs:N1} ms per call under {newest.Version}, against ") +
                Invariant($"{before.MeanMs:N1} ms under {previous.Version} — {delta:N1} ms worse, ") +
                Invariant($"over {after.Calls:N0} and {before.Calls:N0} calls. ") +
                Invariant($"{newest.Version} first appeared {newest.FirstSeen:u}.{where}") +
                (database is not null
                    ? Invariant($" The database ranks it: {database.Title.ToLowerInvariant()}.")
                    : "") +
                (since.Count > 0
                    ? $" The database also started reporting, between the two deploys: {string.Join("; ", since)}."
                    : ""),
            Recommendation =
                "Compare the two builds at this call site. A changed query shape, a lost index, or " +
                "a new N+1 around it are the usual three.",
            Subjects = [Subject.ForQuery(after.Fingerprint)],
            Evidence = new Dictionary<string, object?>
            {
                ["operation"] = after.Operation,
                ["call_site"] = after.CallSite,
                ["version"] = newest.Version,
                ["previous_version"] = previous.Version,
                ["deployed_at"] = newest.FirstSeen,
                ["mean_ms"] = Math.Round(after.MeanMs, 2),
                ["previous_mean_ms"] = Math.Round(before.MeanMs, 2),
                ["ratio"] = Math.Round(ratio, 2),
                ["calls"] = after.Calls,
                ["previous_calls"] = before.Calls,
                ["database_findings_since_previous_deploy"] = since.Count == 0 ? null : since,
            },
        };
    }
}
