using Momus.Core.Insights;
using Momus.Server.Store;

namespace Momus.Server.Insights;

/// <summary>
/// The one place the store's read models become the vocabulary rules and pages speak. It exists
/// so that a rate is computed once, against an explicit window, instead of every consumer
/// dividing by whatever it thinks "now" is.
/// </summary>
public static class StoreViews
{
    public static FindingView ToView(this StoredFinding finding) => new()
    {
        CheckId = finding.CheckId,
        Category = finding.Category,
        Severity = finding.Severity,
        Title = finding.Title,
        Detail = finding.Detail,
        Recommendation = finding.Recommendation,
        EvidenceJson = finding.EvidenceJson,
        Subjects = finding.Subjects,
        FirstSeen = finding.FirstSeen,
        LastSeen = finding.LastSeen,
        SeenCount = finding.SeenCount,
    };

    /// <param name="stat">The store's own row for one statement.</param>
    /// <param name="now">
    /// The end of the stretch being measured. Passed in rather than read from the clock, so a rule
    /// evaluated twice on the same data reaches the same answer twice.
    /// </param>
    public static QueryStatView ToView(this AppQueryStat stat, DateTimeOffset now)
    {
        var minutes = (now - stat.From).TotalMinutes;

        return new QueryStatView
        {
            Fingerprint = stat.Fingerprint,
            TargetId = stat.TargetId,
            Operation = stat.Operation,
            OperationCount = stat.OperationCount,
            CallSite = stat.CallSite,
            Sample = stat.Sample,
            Calls = stat.Calls,
            TotalMs = stat.DurationSum,
            MeanMs = stat.MeanMs,
            CallsPerMinute = minutes <= 0 ? stat.Calls : stat.Calls / minutes,
            MaxRepeats = stat.MaxRepeats,
            Errors = stat.Errors,
        };
    }

    public static OperationStatView ToView(this AppOperationStat stat) => new()
    {
        Name = stat.Name,
        Kind = stat.Kind,
        Calls = stat.Calls,
        MeanMs = stat.MeanMs,
        MeanQueries = stat.MeanQueries,
        MaxQueries = stat.QueryMax,
        DbShare = stat.DbShare,
    };
}

/// <summary>
/// One database, one stretch of time, loaded before any rule runs. Nothing here opens a database
/// connection: every read is of the store, which is the line between an insight and a check.
/// </summary>
public sealed class StoreInsightContext : IInsightContext
{
    public required string TargetId { get; init; }
    public required DateTimeOffset Now { get; init; }
    public required DateTimeOffset Since { get; init; }
    public IReadOnlyList<FindingView> LatestFindings { get; init; } = [];
    public IReadOnlyList<QueryStatView> QueryStats { get; init; } = [];
    public IReadOnlyList<OperationStatView> OperationStats { get; init; } = [];
    public IReadOnlyList<TransactionStatView> Transactions { get; init; } = [];
    public IReadOnlyList<PoolStatView> PoolWaits { get; init; } = [];
    public IReadOnlyList<DeployView> Deploys { get; init; } = [];
    public IReadOnlyList<VersionStatView> VersionStats { get; init; } = [];

    /// <summary>Reads everything the rules need for one target, before any of them runs.</summary>
    /// <param name="window">
    /// How far back the live numbers reach. Version comparisons reach further on purpose: two
    /// deploys are rarely both inside one hour, and a regression that only shows up against a
    /// build from yesterday is still a regression.
    /// </param>
    public static async Task<StoreInsightContext> LoadAsync(
        MomusStore store, string targetId, TimeSpan window, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - window;
        var history = now - MomusStore.RollupRetention;

        var findings = await store.LatestFindingsAsync(targetId, ct);
        var queries = await store.AppQueryStatsAsync(since, ct: ct);
        var operations = await store.AppOperationStatsAsync(since, ct: ct);

        return new StoreInsightContext
        {
            TargetId = targetId,
            Now = now,
            Since = since,
            LatestFindings = findings.Select(f => f.ToView()).ToList(),
            // A statement the client could not attribute to a database is still this target's:
            // there is one target in the free tier, and dropping the row would lose the join.
            QueryStats = queries
                .Where(q => q.TargetId.Length == 0 || q.TargetId == targetId)
                .Select(q => q.ToView(now)).ToList(),
            OperationStats = operations.Select(o => o.ToView()).ToList(),
            Transactions = await store.TransactionStatsAsync(since, ct),
            PoolWaits = await store.PoolStatsAsync(since, ct),
            Deploys = await store.DeploysAsync(ct: ct),
            VersionStats = await store.VersionStatsAsync(history, ct),
        };
    }
}
