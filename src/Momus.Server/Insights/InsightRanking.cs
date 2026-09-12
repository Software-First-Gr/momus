using Momus.Core;
using Momus.Core.Insights;

namespace Momus.Server.Insights;

/// <summary>
/// One number that decides what to fix first. A Critical nothing calls loses to a High on the
/// busiest endpoint, because the question is not "what is worst" but "what is costing most".
/// </summary>
/// <remarks>
/// Deliberately three factors and no more. Severity is what the rule concluded, reach is how much
/// of the application actually hits it, and recency favours something that started today over
/// something that has been true for a month and evidently survivable. A fourth factor would be a
/// knob nobody could reason about.
/// </remarks>
public static class InsightRanking
{
    /// <summary>Doubling per level, so severity dominates unless reach or recency say otherwise.</summary>
    public static double Weight(Severity severity) => severity switch
    {
        Severity.Critical => 16,
        Severity.High => 8,
        Severity.Medium => 4,
        Severity.Low => 2,
        _ => 1,
    };

    /// <summary>Something new is worth twice something that has been true for days.</summary>
    public const double NewMultiplier = 2;

    /// <summary>
    /// Reach runs from 1, for a subject no instrumented application touches, to this, for one that
    /// every statement hits. Three, so the busiest endpoint's High passes a Critical nothing calls.
    /// </summary>
    public const double MaxReach = 3;

    /// <summary>
    /// The reach of an insight about something that carries no traffic of its own: the server, a
    /// database, a session, an index on its own. Halfway, because traffic says nothing either way
    /// about them — and scoring them as everything or as nothing were both wrong on the demo.
    /// </summary>
    public const double NeutralReach = 2;

    public static IReadOnlyList<Insight> Rank(
        IReadOnlyList<Insight> insights, IInsightContext context,
        IReadOnlyDictionary<string, DateTimeOffset>? firstSeen = null) =>
        insights.Select(i => i with { Score = Score(i, context, FirstSeen(firstSeen, i, context)) }).ToList();

    public static double Score(Insight insight, IInsightContext context, DateTimeOffset firstSeen) =>
        Weight(insight.Severity) * Reach(insight, context) * Recency(firstSeen, context.Now);

    /// <summary>
    /// How far traffic moves this insight: <c>1 + 2 × share</c> when a subject carries traffic, and
    /// <see cref="NeutralReach"/> when none does or no application is reporting at all.
    /// </summary>
    /// <remarks>
    /// Found on the running demo, both ways round. A <c>server</c> subject used to count as all of
    /// the traffic, which put a Low buffer-cache ratio second on Fix first; a <c>session</c> counted
    /// as none of it, which put a High that had started minutes earlier — a transaction idle for five
    /// minutes, blocking vacuum database-wide — fifth, below an Info card. Neither kind of subject is
    /// something an application calls, so neither is scaled by what applications call.
    /// </remarks>
    public static double Reach(Insight insight, IInsightContext context) =>
        Share(insight, context) is { } share ? 1 + (MaxReach - 1) * share : NeutralReach;

    /// <summary>
    /// The share of the application's database traffic this insight's subjects account for, or
    /// null when none of them is a query, an operation or a table.
    /// </summary>
    /// <remarks>
    /// The widest subject wins rather than all of them adding up. An unused-index finding carries
    /// both <c>index:</c> and <c>table:</c>, and summing them made every multi-subject insight
    /// score as if it were about the whole application — which put a Low unused index above a High
    /// sequential scan on the same table. The index has no traffic of its own; the table it sits
    /// on is what the insight is scaled by.
    /// </remarks>
    public static double? Share(Insight insight, IInsightContext context)
    {
        var measurable = insight.Subjects
            .Where(s => s.Kind is Subject.Query or Subject.Operation or Subject.Table)
            .ToList();
        if (measurable.Count == 0) return null;

        var totalCalls = context.QueryStats.Sum(q => q.Calls);
        if (totalCalls == 0) return null;

        double widest = 0;

        foreach (var subject in measurable)
        {
            var hit = subject.Kind switch
            {
                Subject.Query => context.QueryStats
                    .Where(q => q.Fingerprint == subject.Key).Sum(q => q.Calls),
                Subject.Operation => context.QueryStats
                    .Where(q => q.Operation == subject.Key).Sum(q => q.Calls),
                _ => context.QueryStats
                    .Where(q => TableMatch.Mentions(q.Sample, subject.Key)).Sum(q => q.Calls),
            };

            if (hit > widest) widest = hit;
        }

        return Math.Clamp(widest / totalCalls, 0, 1);
    }

    /// <summary>
    /// Two for something first seen today, falling towards one over a week. Nothing ever reaches
    /// zero: a problem does not stop being a problem for having been ignored.
    /// </summary>
    public static double Recency(DateTimeOffset firstSeen, DateTimeOffset now)
    {
        var days = Math.Max(0, (now - firstSeen).TotalDays);
        return 1 + (NewMultiplier - 1) / (1 + days);
    }

    private static DateTimeOffset FirstSeen(
        IReadOnlyDictionary<string, DateTimeOffset>? known, Insight insight, IInsightContext context) =>
        known is not null && known.TryGetValue(insight.IdentityKey, out var at) ? at : context.Now;
}
