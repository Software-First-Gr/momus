using System.Text;
using Momus.Core;
using Momus.Server.Insights;
using Momus.Server.Store;
using static System.FormattableString;

namespace Momus.Server.Diagnostics;

/// <summary>How much a verdict should worry you.</summary>
public enum Verdict
{
    /// <summary>Working, and worth saying so: half the value of this page is ruling things out.</summary>
    Ok,

    /// <summary>Not broken, but not what you were promised either.</summary>
    Warning,

    /// <summary>This half of the loop is not working.</summary>
    Problem,
}

/// <param name="Verdict">Whether this is fine, odd, or broken.</param>
/// <param name="Headline">What is true, in one line.</param>
/// <param name="WhatToDo">The next thing to try. Null when there is nothing to do.</param>
public sealed record Check(Verdict Verdict, string Headline, string? WhatToDo = null);

/// <summary>
/// One page that answers "is this working, and if not which half". It exists because the failures
/// of a two-sided tool are mostly silent: a client that cannot reach the server, a scanning user
/// that cannot see other sessions' statements, a fingerprint that does not match. Each of those
/// looks exactly like "no problems found".
/// </summary>
/// <remarks>
/// Everything here is safe to paste into a bug report, and that is the point — it is written to be
/// copied. No connection string is included, on either half.
/// </remarks>
public sealed record DiagnosticsReport
{
    public required string ServerVersion { get; init; }
    public required TimeSpan Uptime { get; init; }
    public required string DataDirectory { get; init; }
    public required long DatabaseBytes { get; init; }
    public required long SchemaVersion { get; init; }
    public required TimeSpan ScanInterval { get; init; }
    public required TimeSpan Window { get; init; }

    public IReadOnlyList<TargetHealth> Targets { get; init; } = [];
    public IReadOnlyList<StoredApp> Apps { get; init; } = [];
    public required IngestHealth Ingest { get; init; }
    public StoredWindow? LatestWindow { get; init; }

    /// <summary>Statements the app reported that the database also ranks. The product, as a number.</summary>
    public int Joined { get; init; }

    public int AppStatements { get; init; }
    public int DatabaseStatements { get; init; }
    public int Insights { get; init; }

    public IReadOnlyList<Check> Checks { get; init; } = [];

    public bool AnyProblem => Checks.Any(c => c.Verdict == Verdict.Problem);
}

/// <summary>One target, without its connection string.</summary>
public sealed record TargetHealth
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public required string Source { get; init; }
    public string? DatabaseName { get; init; }
    public string? ServerVersion { get; init; }
    public DateTimeOffset? LastScanAt { get; init; }
    public string? LastError { get; init; }
    public int ScansKept { get; init; }
    public int Findings { get; init; }

    /// <summary>Findings about a single statement. Zero of these is the pg_stat_statements symptom.</summary>
    public int StatementFindings { get; init; }

    public IReadOnlyList<StoredCheckFailure> FailedChecks { get; init; } = [];
}
