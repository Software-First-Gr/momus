using Microsoft.Extensions.Logging;
using Momus.Core.Insights;

namespace Momus.Server.Insights;

/// <summary>
/// Runs every rule over one snapshot. The counterpart of <c>CollectorEngine</c>, and it follows
/// the same rule: one rule that throws becomes one log line, never a missing evaluation.
/// </summary>
public sealed class InsightEngine(ILogger<InsightEngine> logger, IReadOnlyList<IInsight>? rules = null)
{
    /// <summary>
    /// How far back the app side is read. An hour is long enough that a quiet minute does not
    /// erase an N+1, and short enough that "calls per minute" still means today.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<IInsight> _rules = rules ??
    [
        new NPlusOneInsight(),
        new HotQueryOriginInsight(),
        new DbFindingInsight(),
    ];

    public async Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct)
    {
        var insights = new List<Insight>();

        foreach (var rule in _rules)
        {
            try
            {
                insights.AddRange(await rule.EvaluateAsync(context, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Insight {Kind} failed; the others still ran.", rule.Kind);
            }
        }

        return insights;
    }
}
