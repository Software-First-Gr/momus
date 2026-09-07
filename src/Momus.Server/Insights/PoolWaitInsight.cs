using Momus.Core;
using Momus.Core.Insights;
using static System.FormattableString;

namespace Momus.Server.Insights;

/// <summary>
/// Time spent waiting for a connection rather than using one. When it is more than a blip, the
/// pool is empty, and an empty pool is nearly always transactions held open one step earlier —
/// which is why this insight names them when it can.
/// </summary>
public sealed class PoolWaitInsight : IInsight
{
    /// <summary>A tenth of a second to be handed a connection means there was not one to hand.</summary>
    public const double MinP95WaitMs = 100;

    public const long MinWaits = 20;

    public string Kind => "pool_wait";

    public Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct)
    {
        var saturation = context.LatestFindings
            .Where(f => f.CheckId.Contains("connection", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => (int)f.Severity)
            .FirstOrDefault();

        // The app-side cause, if there is one: whoever is holding connections longest.
        var holding = context.Transactions
            .OrderByDescending(t => t.OpenMs.Sum)
            .Take(2)
            .Select(t => t.Operation)
            .ToList();

        var insights = context.PoolWaits
            .Where(p => p.Waits >= MinWaits && p.P95WaitMs > MinP95WaitMs)
            .OrderByDescending(p => p.P95WaitMs)
            .Select(p => Build(p, saturation, holding))
            .ToList();

        return Task.FromResult<IReadOnlyList<Insight>>(insights);
    }

    private static Insight Build(
        PoolStatView pool, FindingView? saturation, IReadOnlyList<string> holding)
    {
        return new Insight
        {
            Kind = "pool_wait",
            Severity = saturation is not null && saturation.Severity >= Severity.Medium
                ? Severity.High
                : Severity.Medium,
            Title = Invariant($"{pool.Operation} waits {pool.P95WaitMs:N0} ms for a connection"),
            Detail =
                Invariant($"p95 wait {pool.P95WaitMs:N0} ms over {pool.Waits:N0} acquisitions — ") +
                "latency that has nothing to do with the query." +
                (saturation is not null ? $" The database agrees: {saturation.Title}" : "") +
                (holding.Count > 0
                    ? $" The operations holding transactions open longest are {string.Join(" and ", holding)}."
                    : ""),
            Recommendation =
                "Raise the pool size only after checking what is holding connections: a bigger " +
                "pool around a transaction held open buys minutes, not a fix.",
            Subjects = [Subject.ForOperation(pool.Operation)],
            Evidence = new Dictionary<string, object?>
            {
                ["operation"] = pool.Operation,
                ["acquisitions"] = pool.Waits,
                ["p95_wait_ms"] = Math.Round(pool.P95WaitMs, 1),
                ["mean_wait_ms"] = Math.Round(pool.MeanWaitMs, 1),
                ["database_says"] = saturation?.Title,
                ["holding_transactions_open"] = holding.Count == 0 ? null : holding,
            },
        };
    }
}
