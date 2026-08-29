namespace Momus.Core;

/// <summary>Outcome of running a single check, successful or not.</summary>
public sealed record CheckResult
{
    public required string CheckId { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required bool Succeeded { get; init; }
    public required TimeSpan Duration { get; init; }
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    /// <summary>Error message when the check itself failed. A failed check never aborts a scan.</summary>
    public string? Error { get; init; }
}

/// <summary>Full result of one scan run.</summary>
public sealed record ScanReport
{
    public required DateTimeOffset StartedAt { get; init; }
    public required TimeSpan Duration { get; init; }
    public required TargetInfo Target { get; init; }
    public required IReadOnlyList<CheckResult> Checks { get; init; }

    public IEnumerable<Finding> Findings =>
        Checks.SelectMany(c => c.Findings).OrderByDescending(f => f.Severity);

    public int CountAtLeast(Severity severity) =>
        Checks.SelectMany(c => c.Findings).Count(f => f.Severity >= severity);
}
