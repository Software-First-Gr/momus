using Momus.Core.Insights;

namespace Momus.Tests.Fakes;

/// <summary>
/// A hand-built context. Rules are pure functions of one of these, which is the whole reason the
/// real context is a snapshot loaded up front rather than a set of queries a rule may issue.
/// </summary>
public sealed record InsightSnapshot : IInsightContext
{
    public string TargetId { get; init; } = "shop";
    public DateTimeOffset Now { get; init; } = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Since { get; init; } = new(2026, 9, 7, 11, 0, 0, TimeSpan.Zero);
    public IReadOnlyList<FindingView> LatestFindings { get; init; } = [];
    public IReadOnlyList<QueryStatView> QueryStats { get; init; } = [];
    public IReadOnlyList<OperationStatView> OperationStats { get; init; } = [];
    public IReadOnlyList<TransactionStatView> Transactions { get; init; } = [];
    public IReadOnlyList<PoolStatView> PoolWaits { get; init; } = [];
    public IReadOnlyList<DeployView> Deploys { get; init; } = [];
    public IReadOnlyList<VersionStatView> VersionStats { get; init; } = [];
}
