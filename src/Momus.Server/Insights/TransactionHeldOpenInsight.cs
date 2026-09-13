using System.Text.Json;
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

    /// <summary>Where session findings record the statements that tie them to an application.</summary>
    private static readonly string[] FingerprintKeys = ["query_fingerprint", "blocker_query_fingerprint"];

    public string Kind => "transaction_held_open";

    public Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct)
    {
        // The database's side of the same story: sessions idle in a transaction, or waiting on a
        // lock somebody else holds.
        var sessions = context.LatestFindings
            .Where(f => f.Subjects.Any(s => s.Kind == Subject.Session))
            .OrderByDescending(f => (int)f.Severity)
            .ToList();

        var insights = context.Transactions
            .Where(t => t.Count >= MinCount && t.P95OpenMs > MinP95OpenMs && t.DbShare < MaxDbShare)
            .OrderByDescending(t => t.P95OpenMs)
            .Select(t => Build(t, Related(t, sessions, context)))
            .ToList();

        return Task.FromResult<IReadOnlyList<Insight>>(insights);
    }

    /// <summary>
    /// The worst session finding that is provably about this operation's transactions, if any.
    /// </summary>
    /// <remarks>
    /// Found on the demo: a checkout holding its transaction for 700 ms went to High because a
    /// different scenario had left an unrelated session idle in transaction for five minutes, and
    /// the card said "the database side agrees" about something the database was not saying. The
    /// join is the one the rest of Momus is built on: a session counts when the statement it last
    /// ran — or, for a session blocked on a lock, the statement its blocker last ran — is one this
    /// operation runs. SQL Server's blocking check does not record fingerprints yet, so it never
    /// counts rather than always counting.
    /// </remarks>
    private static FindingView? Related(
        TransactionStatView transaction, IReadOnlyList<FindingView> sessions, IInsightContext context)
    {
        if (sessions.Count == 0) return null;

        var statements = context.QueryStats
            .Where(q => q.Operation == transaction.Operation)
            .Select(q => q.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
        if (statements.Count == 0) return null;

        return sessions.FirstOrDefault(f => Fingerprints(f).Any(statements.Contains));
    }

    private static Insight Build(TransactionStatView transaction, FindingView? related)
    {
        var where = transaction.CallSite is { Length: > 0 } site ? $" ({site})" : "";

        return new Insight
        {
            Kind = "transaction_held_open",
            Severity = related is not null && related.Severity >= Severity.Medium
                ? Severity.High
                : Severity.Medium,
            Title = Invariant($"{transaction.Operation} holds a transaction open for {transaction.P95OpenMs:N0} ms"),
            Detail =
                Invariant($"p95 open time {transaction.P95OpenMs:N0} ms across {transaction.Count:N0} ") +
                Invariant($"transactions, of which only {Share(transaction.DbShare)} was spent talking to ") +
                Invariant($"the database.{where} The rest is locks held while something else happens.") +
                (related is not null ? $" The database side agrees: {related.Title}" : ""),
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
                ["database_says"] = related?.Title,
            },
        };
    }

    private static IReadOnlyList<string> Fingerprints(FindingView finding)
    {
        try
        {
            using var evidence = JsonDocument.Parse(finding.EvidenceJson);
            if (evidence.RootElement.ValueKind != JsonValueKind.Object) return [];

            return FingerprintKeys
                .Select(key => evidence.RootElement.TryGetProperty(key, out var value) &&
                               value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null)
                .OfType<string>()
                .Where(key => key.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Share(double fraction) => Invariant($"{fraction * 100:N0}%");
}
