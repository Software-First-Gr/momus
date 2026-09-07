using Momus.Core;

namespace Momus.Server.Store;

/// <summary>A database the scheduler scans, as the store holds it.</summary>
public sealed record StoredTarget
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public required string ConnectionString { get; init; }

    /// <summary>Where it came from: env, cli or ui. In M2 the client's hello adds one more.</summary>
    public required string Source { get; init; }

    public string? DatabaseName { get; init; }
    public string? ServerVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastScanAt { get; init; }

    /// <summary>Why the last scan failed, or null when it worked. The header shows it.</summary>
    public string? LastError { get; init; }
}

/// <summary>One scan run, without its findings.</summary>
public sealed record StoredScan
{
    public required long Id { get; init; }
    public required string TargetId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required TimeSpan Duration { get; init; }
    public required bool Succeeded { get; init; }
    public string? Error { get; init; }
    public string? ServerVersion { get; init; }
    public string? DatabaseName { get; init; }
    public int CheckCount { get; init; }
    public int FailedCheckCount { get; init; }
    public int FindingCount { get; init; }
}

/// <summary>
/// A finding from the newest scan, carrying the two dates the CLI could never give you:
/// when Momus first saw it and when it last did.
/// </summary>
public sealed record StoredFinding
{
    public required long Id { get; init; }
    public required string CheckId { get; init; }
    public required string Category { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public string? Recommendation { get; init; }

    /// <summary>Evidence as the JSON the check produced; the UI renders it as a table.</summary>
    public required string EvidenceJson { get; init; }

    public IReadOnlyList<Subject> Subjects { get; init; } = [];
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public int SeenCount { get; init; }
    public string Status { get; init; } = "open";

    /// <summary>True the first time this finding is seen — worth pointing out in the UI.</summary>
    public bool IsNew => SeenCount <= 1;
}

/// <summary>A failed check from the newest scan. Shown so a permission problem is never silent.</summary>
public sealed record StoredCheckFailure(string CheckId, string Title, string Error);
