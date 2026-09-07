using Momus.Core;
using Momus.Core.Ingest;
using Momus.Core.Insights;

namespace Momus.Server.Insights;

/// <summary>
/// Everything the database found that is not about a single statement, passed through so the
/// Insights tab is the whole picture rather than half of it — with the app side's answer to
/// "who touches this" attached where there is one.
/// </summary>
/// <remarks>
/// Statement-level findings are deliberately not here: <see cref="HotQueryOriginInsight"/> owns
/// them and says strictly more about each. Two rules producing a row for one finding would be two
/// cards saying the same thing.
/// </remarks>
public sealed class DbFindingInsight : IInsight
{
    /// <summary>Enough to recognise the pattern; the Queries tab has the full list.</summary>
    private const int MaxNamedOperations = 3;

    public string Kind => "db_finding";

    public Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct)
    {
        var insights = context.LatestFindings
            .Where(f => !f.Subjects.Any(s => s.Kind == Subject.Query))
            .Select(f => Build(f, context))
            .ToList();

        return Task.FromResult<IReadOnlyList<Insight>>(insights);
    }

    private static Insight Build(FindingView finding, IInsightContext context)
    {
        var operations = Operations(finding, context);

        return new Insight
        {
            Kind = "db_finding",
            // Two server-wide checks share the subject "server", so the check id is the rest of it.
            Discriminator = finding.CheckId,
            Severity = finding.Severity,
            Title = finding.Title,
            Detail = operations.Count == 0
                ? finding.Detail
                : $"{finding.Detail} The app side reaches it from {Join(operations)}.",
            Recommendation = finding.Recommendation,
            Subjects = finding.Subjects,
            Evidence = new Dictionary<string, object?>
            {
                ["check"] = finding.CheckId,
                ["category"] = finding.Category,
                ["first_seen"] = finding.FirstSeen,
                ["seen_count"] = finding.SeenCount,
                ["operations"] = operations.Count == 0 ? null : operations,
                ["evidence"] = finding.EvidenceJson,
            },
        };
    }

    /// <summary>The endpoints whose statements name this finding's table, busiest first.</summary>
    private static IReadOnlyList<string> Operations(FindingView finding, IInsightContext context)
    {
        var tables = finding.Subjects.Where(s => s.Kind == Subject.Table).ToList();
        if (tables.Count == 0) return [];

        return tables
            .SelectMany(t => TableMatch.Touching(context.QueryStats, t.Key))
            // Startup, a migration or a timer is not an endpoint, and naming it in a sentence
            // about who reaches a table reads as a bug rather than as information.
            .Where(q => q.Operation != IngestOperation.AmbientName)
            .GroupBy(q => q.Operation)
            .OrderByDescending(g => g.Sum(q => q.CallsPerMinute))
            .Take(MaxNamedOperations)
            .Select(g => g.Key)
            .ToList();
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
