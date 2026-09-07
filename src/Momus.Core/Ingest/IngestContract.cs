using System.Text.Json;
using System.Text.Json.Serialization;

namespace Momus.Core.Ingest;

/// <summary>
/// What the application sends the server, every few seconds: aggregates only. No literal, no
/// parameter value and no result row ever leaves the process — only normalized statement text,
/// counts and timings. The contract lives in Core so the client and the server compile against
/// one definition of it rather than two that drift.
/// </summary>
/// <remarks>Version 1 of this shape is frozen at 1.0; after that, new fields only.</remarks>
public sealed record IngestBatch
{
    public required IngestApp App { get; init; }
    public required IngestWindow Window { get; init; }

    /// <summary>Databases this app talks to, so the server can offer to scan them too.</summary>
    public IReadOnlyList<IngestTarget> Targets { get; init; } = [];

    public IReadOnlyList<IngestOperation> Operations { get; init; } = [];
    public IReadOnlyList<IngestQuery> Queries { get; init; } = [];

    /// <summary>
    /// Executions the client could not attribute because the window hit its key limit. Shown in
    /// the UI as "unattributed": a number that is never silently zero is worth more than one that
    /// pretends the sample is complete.
    /// </summary>
    public long Overflow { get; init; }
}

/// <summary>
/// Who is reporting. <c>Instance</c> is host and process, so two replicas of one app stay
/// distinguishable; a new <c>Version</c> appearing is what marks a deploy.
/// </summary>
public sealed record IngestApp(string Name, string? Version, string? Instance, string? Environment);

/// <summary>The few seconds this batch covers.</summary>
public sealed record IngestWindow(DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// A database the app talks to. The connection string is only sent when the client is configured
/// to share it — by default, only to a server on loopback. It exists so that a developer
/// configures the database once, on the application side.
/// </summary>
public sealed record IngestTarget(string Id, string Provider, string? Database, string? ConnectionString);

/// <summary>
/// One named unit of work over the window. <c>Kind</c> is "http" for a request, "background" for
/// work the application named itself, or "ambient" for statements that ran outside any operation;
/// <c>Queries</c> holds the statements run inside one operation — the sum, and the worst single one.
/// </summary>
public sealed record IngestOperation(
    string Name,
    string Kind,
    long Count,
    Stat DurationMs,
    Counts Queries,
    Stat DbMs);

/// <summary>
/// One statement, as one operation ran it. <c>Key</c> is the <see cref="SqlFingerprint"/> key —
/// the join with what the database reports. <c>CallSite</c> is the first frame outside the
/// framework, e.g. "OrdersHandler.cs:42". <c>Sample</c> is normalized text, never the original,
/// which could carry literals. <c>MaxRepeatsPerOperation</c> is the N in N+1: how often this one
/// statement ran inside a single operation.
/// </summary>
public sealed record IngestQuery(
    string Key,
    string? Target,
    string Operation,
    string? CallSite,
    string Sample,
    long Count,
    Timing DurationMs,
    long Rows,
    int MaxRepeatsPerOperation,
    long Errors);

/// <summary>A summed and peak measurement.</summary>
public sealed record Stat(double Sum, double Max);

/// <summary>A summed and peak count.</summary>
public sealed record Counts(long Sum, long Max);

/// <summary>
/// Timings with a shape. <c>Hist</c> is eight log2 buckets starting at 1 ms: enough for a p95
/// without shipping raw samples.
/// </summary>
public sealed record Timing(double Sum, double Max, IReadOnlyList<long> Hist)
{
    public const int Buckets = 8;

    /// <summary>Which bucket a duration falls in: &lt;1 ms, &lt;2, &lt;4, … &lt;64, then everything above.</summary>
    public static int BucketFor(double milliseconds)
    {
        if (milliseconds < 1) return 0;
        var bucket = (int)Math.Log2(milliseconds) + 1;
        return bucket >= Buckets ? Buckets - 1 : bucket;
    }

    /// <summary>Approximate percentile from the buckets, as the upper bound of the bucket it lands in.</summary>
    public double Percentile(double fraction)
    {
        var total = Hist.Sum();
        if (total == 0) return 0;

        var target = total * fraction;
        long seen = 0;
        for (var i = 0; i < Hist.Count; i++)
        {
            seen += Hist[i];
            if (seen >= target) return i == 0 ? 1 : Math.Pow(2, i);
        }
        return Max;
    }
}

/// <summary>One JSON shape for both ends of the wire.</summary>
public static class IngestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
