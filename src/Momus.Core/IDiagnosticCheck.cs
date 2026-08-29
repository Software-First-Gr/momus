using System.Data.Common;

namespace Momus.Core;

/// <summary>
/// One focused diagnostic (e.g. "unused indexes", "top waits"). Checks are stateless:
/// they receive an open connection, read whatever system views they need, and return findings.
/// Returning an empty list means "nothing worth reporting" — that is the healthy outcome.
/// </summary>
public interface IDiagnosticCheck
{
    /// <summary>Stable machine id, e.g. "pg.unused_indexes".</summary>
    string Id { get; }

    string Title { get; }

    /// <summary>Grouping key: "queries", "indexes", "sessions", "memory", "configuration".</summary>
    string Category { get; }

    Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct);
}
