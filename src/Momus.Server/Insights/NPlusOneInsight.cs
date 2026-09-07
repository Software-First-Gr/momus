using Momus.Core;
using Momus.Core.Insights;
using static System.FormattableString;

namespace Momus.Server.Insights;

/// <summary>
/// One statement running in a loop inside a single operation. The classic: fetch the order, fetch
/// its lines, then fetch each line's product one at a time.
/// </summary>
/// <remarks>
/// Two gates, because an N+1 is a shape and a cost, and neither alone is worth telling anyone
/// about. The shape is the repeat count: five executions of one statement inside one operation is
/// a loop, not a coincidence. The cost is the rate: a loop that runs twice an hour is not a
/// problem however tight it is. See D15 in PLAN.md for why the numbers are 5 and 60 rather than
/// the ×10 the design first proposed.
/// </remarks>
public sealed class NPlusOneInsight : IInsight
{
    /// <summary>Below this it could be a hand-written sequence of a few related reads.</summary>
    public const int MinRepeats = 5;

    /// <summary>At least one execution per second before a loop is worth an afternoon.</summary>
    public const double MinCallsPerMinute = 60;

    public string Kind => "n_plus_one";

    public Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct)
    {
        var database = QueryJoin.FromFindings(context.LatestFindings);

        var insights = context.QueryStats
            .Where(q => q.MaxRepeats >= MinRepeats && q.CallsPerMinute >= MinCallsPerMinute)
            .OrderByDescending(q => q.CallsPerMinute)
            .Select(q => Build(q, context, database.GetValueOrDefault(q.Fingerprint)))
            .ToList();

        return Task.FromResult<IReadOnlyList<Insight>>(insights);
    }

    private static Insight Build(QueryStatView query, IInsightContext context, DatabaseView? onTheQuery)
    {
        // "The DB side says the table is scanned or the query is slow" — either half raises it.
        var onTheTable = TableMatch.WorstTableFinding(context.LatestFindings, query.Sample);
        var worst = Worst(onTheQuery?.Severity, onTheTable?.Severity);
        var severity = worst >= Severity.Medium ? Severity.High : Severity.Medium;

        // Collapsing the loop leaves one statement per call instead of MaxRepeats of them.
        var avoidable = query.CallsPerMinute * (query.MaxRepeats - 1) / query.MaxRepeats;

        var says = onTheQuery is not null
            ? $" The database ranks it too: {onTheQuery.Title.ToLowerInvariant()}."
            : onTheTable is not null
                ? $" The database is unhappy about a table it names: {onTheTable.Title}"
                : "";

        return new Insight
        {
            Kind = "n_plus_one",
            Severity = severity,
            Title = Invariant($"{query.Operation} runs one statement ×{query.MaxRepeats} per call"),
            Detail =
                Invariant($"The same statement runs up to {query.MaxRepeats} times inside a single ") +
                Invariant($"{query.Operation}{Where(query.CallSite)}, {query.CallsPerMinute:N0} ") +
                Invariant($"times a minute in total at ") +
                Invariant($"{query.MeanMs:N1} ms each. Collapsing the loop would remove roughly ") +
                Invariant($"{avoidable:N0} round trips a minute.") + says,
            Recommendation =
                "Fetch them together instead of one at a time — an Include, a join, or a single " +
                "WHERE id IN (…) — and the loop becomes one statement per call.",
            Subjects = [Subject.ForQuery(query.Fingerprint)],
            Evidence = new Dictionary<string, object?>
            {
                ["operation"] = query.Operation,
                ["call_site"] = query.CallSite,
                ["repeats_per_operation"] = query.MaxRepeats,
                ["calls_per_minute"] = Math.Round(query.CallsPerMinute, 1),
                ["avoidable_calls_per_minute"] = Math.Round(avoidable, 1),
                ["app_mean_ms"] = Math.Round(query.MeanMs, 2),
                ["statement"] = query.Sample,
                ["database_says"] = onTheQuery?.Title ?? onTheTable?.Title,
            },
        };
    }

    /// <summary>The half of D6 that matters most: the loop, mapped to the line that writes it.</summary>
    private static string Where(string? callSite) => callSite is { Length: > 0 } ? $" ({callSite})" : "";

    private static Severity Worst(Severity? a, Severity? b) =>
        (Severity)Math.Max((int)(a ?? Severity.Info), (int)(b ?? Severity.Info));
}
