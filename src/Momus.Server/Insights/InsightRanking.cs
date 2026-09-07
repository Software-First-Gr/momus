using Momus.Core;
using Momus.Core.Insights;

namespace Momus.Server.Insights;

/// <summary>
/// One number that decides what to fix first. A Critical nothing calls loses to a High on the
/// busiest endpoint, because the question is not "what is worst" but "what is costing most".
/// </summary>
/// <remarks>
/// Deliberately three factors and no more. Severity is what the rule concluded, traffic is how
/// much of the application actually hits it, and recency favours something that started today
/// over something that has been true for a month and evidently survivable. A fourth factor would
/// be a knob nobody could reason about.
/// </remarks>
public static class InsightRanking
{
    /// <summary>Doubling per level, so severity dominates until traffic differs by more than 2×.</summary>
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
    /// A subject nothing measurable touches still scores: the traffic share is a multiplier, not a
    /// gate, and a database-side finding on a table no instrumented app names is still a finding.
    /// </summary>
    public const double MinShare = 0.02;

    public static IReadOnlyList<Insight> Rank(
        IReadOnlyList<Insight> insights, IInsightContext context,
        IReadOnlyDictionary<string, DateTimeOffset>? firstSeen = null) =>
        insights.Select(i => i with { Score = Score(i, context, FirstSeen(firstSeen, i, context)) }).ToList();

    public static double Score(Insight insight, IInsightContext context, DateTimeOffset firstSeen) =>
        Weight(insight.Severity) * Share(insight, context) * Recency(firstSeen, context.Now);

    /// <summary>
    /// How much of the application's database traffic this insight's subject accounts for.
    /// </summary>
    /// <remarks>
    /// The widest subject wins rather than all of them adding up. An unused-index finding carries
    /// both <c>index:</c> and <c>table:</c>, and summing them made every multi-subject insight
    /// score as if it were about the whole application — which put a Low unused index above a High
    /// sequential scan on the same table. Only <c>server</c> and <c>database</c> genuinely mean
    /// "all of it"; an index or a session has no traffic of its own and contributes nothing, so
    /// the table it sits on is what the insight is scaled by.
    /// </remarks>
    public static double Share(Insight insight, IInsightContext context)
    {
        var totalCalls = context.QueryStats.Sum(q => q.Calls);
        if (totalCalls == 0) return 1;

        double widest = 0;

        foreach (var subject in insight.Subjects)
        {
            var hit = subject.Kind switch
            {
                Subject.Query => context.QueryStats
                    .Where(q => q.Fingerprint == subject.Key).Sum(q => q.Calls),
                Subject.Operation => context.QueryStats
                    .Where(q => q.Operation == subject.Key).Sum(q => q.Calls),
                Subject.Table => context.QueryStats
                    .Where(q => TableMatch.Mentions(q.Sample, subject.Key)).Sum(q => q.Calls),
                Subject.Server or Subject.Database => totalCalls,
                _ => 0,
            };

            if (hit > widest) widest = hit;
        }

        return Math.Clamp(widest / totalCalls, MinShare, 1);
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
