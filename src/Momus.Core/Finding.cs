namespace Momus.Core;

/// <summary>How urgently a finding deserves attention.</summary>
public enum Severity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// <summary>A single actionable observation produced by a diagnostic check.</summary>
public sealed record Finding
{
    /// <summary>Id of the check that produced this finding.</summary>
    public required string CheckId { get; init; }

    public required Severity Severity { get; init; }

    /// <summary>Short, human-readable headline (e.g. "Index `ix_orders_status` is never used").</summary>
    public required string Title { get; init; }

    /// <summary>What was observed and why it matters.</summary>
    public required string Detail { get; init; }

    /// <summary>What the operator should consider doing about it.</summary>
    public string? Recommendation { get; init; }

    /// <summary>Structured supporting data (numbers, object names) for machine consumption.</summary>
    public IReadOnlyDictionary<string, object?> Evidence { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// What this finding is about, as typed keys the insight engine and the store join on.
    /// Every check sets at least one; server-wide observations use <see cref="Subject.ForServer"/>.
    /// </summary>
    public IReadOnlyList<Subject> Subjects { get; init; } = [];
}
