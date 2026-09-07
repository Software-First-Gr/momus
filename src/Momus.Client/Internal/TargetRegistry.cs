using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Momus.Core.Ingest;

namespace Momus.Client.Internal;

/// <summary>
/// The databases this application talks to, learned from the contexts that execute statements.
/// Sending them lets the server scan the same database the app is using without anyone typing a
/// connection string twice — which is the whole reason the two halves can be joined at all.
/// </summary>
internal sealed class TargetRegistry(MomusOptions options)
{
    private readonly ConcurrentDictionary<string, IngestTarget> _targets = new();

    /// <summary>Returns the target id for a context, registering it the first time it is seen.</summary>
    public string? Register(DbContext? context)
    {
        if (context is null) return null;

        try
        {
            var connectionString = context.Database.GetConnectionString();
            if (string.IsNullOrEmpty(connectionString)) return null;

            var provider = ProviderKey(context.Database.ProviderName);
            if (provider is null) return null;

            var database = DatabaseName(context);
            var id = Slug(database ?? provider);

            _targets.GetOrAdd(id, _ => new IngestTarget(
                id, provider, database,
                options.SharesConnectionStrings ? connectionString : null));

            return id;
        }
        catch
        {
            // Learning where the database is must never be the reason a query fails.
            return null;
        }
    }

    public IReadOnlyList<IngestTarget> All() => _targets.Values.ToArray();

    private static string? DatabaseName(DbContext context)
    {
        try
        {
            return context.Database.GetDbConnection().Database is { Length: > 0 } name ? name : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>EF provider assembly names to the keys Momus's scan targets use.</summary>
    private static string? ProviderKey(string? providerName) => providerName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => "postgres",
        "Microsoft.EntityFrameworkCore.SqlServer" => "sqlserver",
        _ => null,
    };

    private static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "database" : slug;
    }
}
