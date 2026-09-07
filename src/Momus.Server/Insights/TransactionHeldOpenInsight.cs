using Momus.Core;
using Momus.Core.Insights;
using static System.FormattableString;

namespace Momus.Server.Insights;

/// <summary>
/// A transaction open far longer than the database work inside it. Every other session that wants
/// the same rows queues behind its locks, and the application's own timings show nothing wrong:
/// the endpoint is not slow, it is just holding something.
/// </summary>
public sealed class TransactionHeldOpenInsight : IInsight
{
    /// <summary>Half a second of held locks is where it starts to be somebody else's problem.</summary>
    public const double MinP95OpenMs = 500;

    /// <summary>Below this share, the transaction is mostly not doing database work at all.</summary>
    public const double MaxDbShare = 0.30;

    /// <summary>A handful of samples cannot support a percentile.</summary>
    public const long MinCount = 20;

    public string Kind => "transaction_held_open";

    public Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct)
    {
        // The database's side of the same story: sessions sitting idle in a transaction, and
        // whatever is blocked behind them.
        var symptoms = context.LatestFindings
            .Where(f => f.CheckId.Contains("idle", StringComparison.OrdinalIgnoreCase) ||
                        f.CheckId.Contains("blocking", StringComparison.OrdinalIgnoreCase) ||
                        f.CheckId.Contains("problem_sessions", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => (int)f.Severity)
            .ToList();

        var insights = context.Transactions
            .Where(t => t.Count >= MinCount && t.P95OpenMs > MinP95OpenMs && t.DbShare < MaxDbShare)
            .OrderByDescending(t => t.P95OpenMs)
            .Select(t => Build(t, symptoms))
            .ToList();

        return Task.FromResult<IReadOnlyList<Insight>>(insights);
    }

    private static Insight Build(TransactionStatView transaction, IReadOnlyList<FindingView> symptoms)
    {
        var worst = symptoms.FirstOrDefault();
        var where = transaction.CallSite is { Length: > 0 } site ? $" ({site})" : "";

        return new Insight
        {
            Kind = "transaction_held_open",
            Severity = worst is not null && worst.Severity >= Severity.Medium
                ? Severity.High
                : Severity.Medium,
            Title = Invariant($"{transaction.Operation} holds a transaction open for {transaction.P95OpenMs:N0} ms"),
            Detail =
                Invariant($"p95 open time {transaction.P95OpenMs:N0} ms across {transaction.Count:N0} ") +
                Invariant($"transactions, of which only {Share(transaction.DbShare)} was spent talking to ") +
                Invariant($"the database.{where} The rest is locks held while something else happens.") +
                (worst is not null ? $" The database side agrees: {worst.Title}" : ""),
            Recommendation =
                "Move whatever is not database work — an HTTP call, a file write, a slow " +
                "computation — outside the transaction, or commit before doing it.",
            // The operation is the subject: this is about how the application uses the database,
            // not about any one statement.
            Subjects = [Subject.ForOperation(transaction.Operation)],
            Evidence = new Dictionary<string, object?>
            {
                ["operation"] = transaction.Operation,
                ["call_site"] = transaction.CallSite,
                ["transactions"] = transaction.Count,
                ["p95_open_ms"] = Math.Round(transaction.P95OpenMs, 1),
                ["mean_open_ms"] = Math.Round(transaction.MeanOpenMs, 1),
                ["db_share"] = Math.Round(transaction.DbShare, 3),
                ["database_says"] = worst?.Title,
            },
        };
    }

    private static string Share(double fraction) => Invariant($"{fraction * 100:N0}%");
}
