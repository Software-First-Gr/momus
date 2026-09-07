using Momus.Core.Ingest;

namespace Momus.Core.Insights;

/// <summary>
/// A rule over what the store already knows. Unlike <see cref="IDiagnosticCheck"/>, an insight
/// never opens a database connection: the collector reads live statistics views, insights read
/// history. Keeping the two kinds apart is what lets checks be tested with a fake connection and
/// rules be tested with a hand-built snapshot.
/// </summary>
public interface IInsight
{
    /// <summary>Stable across versions: it is half of an insight's identity in the store.</summary>
    string Kind { get; }

    Task<IReadOnlyList<Insight>> EvaluateAsync(IInsightContext context, CancellationToken ct);
}

/// <summary>
/// Everything a rule may read, for one database over one stretch of time, loaded before any rule
/// runs. A snapshot rather than a set of queries, so a rule is a pure function of its inputs and
/// two rules cannot disagree about what the numbers were.
/// </summary>
/// <remarks>
/// M3 adds the reads its own rules need — timings grouped by app version, transactions, pool
/// waits — rather than declaring them here before anything can use them.
/// </remarks>
public interface IInsightContext
{
    /// <summary>The database these findings came from and these insights are about.</summary>
    string TargetId { get; }

    /// <summary>When this evaluation started. Rules take time from here, never from the clock.</summary>
    DateTimeOffset Now { get; }

    /// <summary>How far back <see cref="QueryStats"/> and <see cref="OperationStats"/> reach.</summary>
    DateTimeOffset Since { get; }

    /// <summary>The newest scan's findings, each with the day it was first seen.</summary>
    IReadOnlyList<FindingView> LatestFindings { get; }

    /// <summary>What the applications ran, one row per statement per database.</summary>
    IReadOnlyList<QueryStatView> QueryStats { get; }

    /// <summary>What the applications were asked to do, one row per named operation.</summary>
    IReadOnlyList<OperationStatView> OperationStats { get; }

    /// <summary>Transactions the applications held open, one row per operation and call site.</summary>
    IReadOnlyList<TransactionStatView> Transactions { get; }

    /// <summary>Connection acquisition times, one row per operation.</summary>
    IReadOnlyList<PoolStatView> PoolWaits { get; }

    /// <summary>
    /// Every version an application has been seen running, with the day it first appeared. That
    /// date is a deploy, and it is the only marker Momus needs to answer "since when".
    /// </summary>
    IReadOnlyList<DeployView> Deploys { get; }

    /// <summary>
    /// Statement timings grouped by application version, over a longer stretch than the rest of
    /// this snapshot: comparing two deploys means reaching back past whichever one is older.
    /// </summary>
    IReadOnlyList<VersionStatView> VersionStats { get; }
}

/// <summary>
/// A transaction as one operation used it. The gap between <see cref="OpenMs"/> and
/// <see cref="DbMsSum"/> is the whole signal: time holding locks while doing something else.
/// </summary>
public sealed record TransactionStatView
{
    public required string Operation { get; init; }
    public string? CallSite { get; init; }
    public long Count { get; init; }
    public required Timing OpenMs { get; init; }

    /// <summary>Database time spent inside these transactions.</summary>
    public double DbMsSum { get; init; }

    /// <summary>Share of the open time that was actually database work. Low is the problem.</summary>
    public double DbShare => OpenMs.Sum <= 0 ? 0 : DbMsSum / OpenMs.Sum;

    public double P95OpenMs => OpenMs.Percentile(0.95);
    public double MeanOpenMs => Count == 0 ? 0 : OpenMs.Sum / Count;
}

/// <summary>How long one operation waited for a connection. Normally nothing.</summary>
public sealed record PoolStatView
{
    public required string Operation { get; init; }
    public long Waits { get; init; }
    public required Timing WaitMs { get; init; }

    public double P95WaitMs => WaitMs.Percentile(0.95);
    public double MeanWaitMs => Waits == 0 ? 0 : WaitMs.Sum / Waits;
}

/// <summary>A version of an application, and when it first turned up.</summary>
public sealed record DeployView(string Version, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Instances);

/// <summary>One statement's timings under one application version, for comparing two deploys.</summary>
public sealed record VersionStatView
{
    public required string Fingerprint { get; init; }
    public required string Version { get; init; }
    public long Calls { get; init; }
    public double TotalMs { get; init; }
    public string? Operation { get; init; }
    public string? CallSite { get; init; }
    public double MeanMs => Calls == 0 ? 0 : TotalMs / Calls;
}

/// <summary>
/// A finding as history holds it: what the check said, plus the two dates only a server that has
/// been running can know.
/// </summary>
public sealed record FindingView
{
    public required string CheckId { get; init; }
    public required string Category { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public string? Recommendation { get; init; }

    /// <summary>Evidence as the check wrote it. Rules read numbers out of it by name.</summary>
    public required string EvidenceJson { get; init; }

    public IReadOnlyList<Subject> Subjects { get; init; } = [];
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public int SeenCount { get; init; }
}

/// <summary>
/// One statement's application-side numbers over the context's stretch of time.
/// </summary>
/// <remarks>
/// Every rate here is derived from the context's own window rather than from the clock. That is
/// the difference between this and the store's read model, and it is the point: a rule evaluated
/// twice on the same snapshot has to reach the same answer both times.
/// </remarks>
public sealed record QueryStatView
{
    public required string Fingerprint { get; init; }
    public string TargetId { get; init; } = "";

    /// <summary>The operation that runs it most often.</summary>
    public required string Operation { get; init; }

    /// <summary>How many distinct operations run it. More than one and the cost is shared.</summary>
    public int OperationCount { get; init; }

    /// <summary>First frame outside the framework, e.g. <c>OrdersHandler.cs:42</c>.</summary>
    public string? CallSite { get; init; }

    /// <summary>Normalized text. Never the original — that could carry literals.</summary>
    public string? Sample { get; init; }

    public long Calls { get; init; }
    public double TotalMs { get; init; }
    public double MeanMs { get; init; }
    public double CallsPerMinute { get; init; }

    /// <summary>The N in N+1: the most times this ran inside one operation.</summary>
    public int MaxRepeats { get; init; }

    public long Errors { get; init; }
}

/// <summary>One named unit of work — a route or a background job — over the context's stretch.</summary>
public sealed record OperationStatView
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public long Calls { get; init; }
    public double MeanMs { get; init; }

    /// <summary>Average statements per call. Eight is an N+1 whatever the database thinks of each.</summary>
    public double MeanQueries { get; init; }

    public long MaxQueries { get; init; }

    /// <summary>Share of the operation's own time spent waiting on the database.</summary>
    public double DbShare { get; init; }
}
