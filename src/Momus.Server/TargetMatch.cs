using System.Data.Common;
using Momus.Core.Ingest;
using Momus.Server.Store;

namespace Momus.Server;

/// <summary>
/// Which scanned database an application's statements belong to. The client names a database by
/// the slug of its name; a target from the environment or Settings has the id its own name gave
/// it. When the client shares its connection string the server registers the database under the
/// client's id and the two agree — but a server on another machine is never handed one, and then
/// an application's statements joined the database's only if the two names happened to slug the
/// same. So a batch target the server does not know by id is matched on provider and database
/// name, and the batch is rewritten to the target's id before anything is stored.
/// </summary>
public static class TargetMatch
{
    /// <returns>
    /// The batch with matched ids replaced, and what was replaced by what. A target that matches
    /// none, or more than one — two servers each with a "shop" — is left as it came.
    /// </returns>
    public static (IngestBatch Batch, IReadOnlyDictionary<string, string> Renamed) Resolve(
        IngestBatch batch, IReadOnlyList<StoredTarget> known)
    {
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = known.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var target in batch.Targets)
        {
            if (ids.Contains(target.Id) || string.IsNullOrWhiteSpace(target.Database)) continue;

            var matches = known
                .Where(k => Provider(k.Provider) == Provider(target.Provider) &&
                            string.Equals(DatabaseOf(k), target.Database, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 1) renamed[target.Id] = matches[0].Id;
        }

        if (renamed.Count == 0) return (batch, renamed);

        return (batch with
        {
            Targets = batch.Targets
                .Select(t => renamed.TryGetValue(t.Id, out var id) ? t with { Id = id } : t)
                .ToList(),
            Queries = batch.Queries
                .Select(q => q.Target is { } t && renamed.TryGetValue(t, out var id) ? q with { Target = id } : q)
                .ToList(),
        }, renamed);
    }

    /// <summary>The database a target scans: what its last scan reported, or else what its connection string names.</summary>
    public static string? DatabaseOf(StoredTarget target)
    {
        if (target.DatabaseName is { Length: > 0 } scanned) return scanned;

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = target.ConnectionString };
            foreach (var key in (string[])["Database", "Initial Catalog"])
            {
                if (builder.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } name) return name;
            }
        }
        catch (ArgumentException)
        {
            // Not parseable as key=value pairs; the scan will report the name once it runs.
        }

        return null;
    }

    /// <summary>The spellings people and providers use for the same engine.</summary>
    public static string Provider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "mssql" or "sqlserver" or "sql server" => "sqlserver",
        "postgres" or "postgresql" or "pg" or "npgsql" => "postgres",
        var other => other,
    };
}
