using System.Data.Common;

namespace Momus.Core;

/// <summary>A database Momus can scan: knows how to connect and which checks apply.</summary>
public interface IScanTarget
{
    /// <summary>Provider key, e.g. "postgres" or "sqlserver".</summary>
    string Provider { get; }

    DbConnection CreateConnection();

    IReadOnlyList<IDiagnosticCheck> Checks { get; }

    /// <summary>Basic identity of the scanned server (version, database name).</summary>
    Task<TargetInfo> GetTargetInfoAsync(DbConnection connection, CancellationToken ct);
}

public sealed record TargetInfo
{
    public required string Provider { get; init; }
    public string? ServerVersion { get; init; }
    public string? DatabaseName { get; init; }
    public IReadOnlyDictionary<string, object?> Extra { get; init; } =
        new Dictionary<string, object?>();
}
