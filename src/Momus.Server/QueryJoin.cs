using System.Text.Json;
using Momus.Core;
using Momus.Server.Store;

namespace Momus.Server;

/// <summary>
/// The database's view of one statement, read out of a finding's evidence. Postgres measures
/// execution time and SQL Server measures CPU, so the numbers are named differently on the two
/// sides and are labelled rather than silently averaged together.
/// </summary>
public sealed record DatabaseView
{
    public required Severity Severity { get; init; }
    public required string Title { get; init; }

    /// <summary>What the statistics view holds, normalized. Shown when the app never sent this one.</summary>
    public string? Sample { get; init; }

    /// <summary>Mean milliseconds per call — execution time on Postgres, CPU on SQL Server.</summary>
    public double MeanMs { get; init; }

    public double TotalMs { get; init; }
    public long Calls { get; init; }

    /// <summary>"time" or "CPU": which of the two the numbers above are.</summary>
    public required string Measure { get; init; }

    public required DateTimeOffset FirstSeen { get; init; }
}

/// <summary>
/// One row of the Queries tab. Either half can be missing, and both cases are worth showing: a
/// statement no app sent is a job or another application, and a statement the database has no
/// entry for is simply cheap enough not to be ranked.
/// </summary>
public sealed record QueryRow
{
    public required string Fingerprint { get; init; }
    public AppQueryStat? App { get; init; }
    public DatabaseView? Database { get; init; }

    /// <summary>Whichever side has text for it; the app's is preferred because it is the app's shape.</summary>
    public string Sample => App?.Sample ?? Database?.Sample ?? "(no text)";

    public Severity Severity => Database?.Severity ?? Severity.Info;

    /// <summary>The N in N+1, as the client measured it inside a single operation.</summary>
    public int MaxRepeats => App?.MaxRepeats ?? 0;
}

/// <summary>
/// Puts the two halves of a statement on one row. This is the product's signature and the whole
/// reason <see cref="SqlFingerprint"/> has to give both sides the same key.
/// </summary>
public static class QueryJoin
{
    /// <summary>
    /// Joins what the app reported to what the newest scan found. Order is severity first, then
    /// the app's own time: the real ranking, which weighs traffic and recency, arrives in M3.
    /// </summary>
    public static IReadOnlyList<QueryRow> Build(
        IReadOnlyList<AppQueryStat> app, IReadOnlyList<StoredFinding> findings, string targetId)
    {
        var database = FromFindings(findings);

        var rows = app
            .Where(a => a.TargetId.Length == 0 || a.TargetId == targetId)
            .Select(a => new QueryRow
            {
                Fingerprint = a.Fingerprint,
                App = a,
                Database = database.GetValueOrDefault(a.Fingerprint),
            })
            .ToList();

        // A statement the database is spending time on that no instrumented app sent is a
        // feature of this table, not a gap in it: it is a job, a migration or another service.
        var seen = rows.Select(r => r.Fingerprint).ToHashSet(StringComparer.Ordinal);
        rows.AddRange(database
            .Where(d => !seen.Contains(d.Key))
            .Select(d => new QueryRow { Fingerprint = d.Key, Database = d.Value }));

        return rows
            .OrderByDescending(r => (int)r.Severity)
            .ThenByDescending(r => r.App?.DurationSum ?? 0)
            .ThenByDescending(r => r.Database?.TotalMs ?? 0)
            .ToList();
    }

    /// <summary>The newest scan's findings that carry a <c>query:</c> subject, keyed by fingerprint.</summary>
    public static IReadOnlyDictionary<string, DatabaseView> FromFindings(IReadOnlyList<StoredFinding> findings)
    {
        var views = new Dictionary<string, DatabaseView>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            foreach (var subject in finding.Subjects.Where(s => s.Kind == Subject.Query))
            {
                var view = ViewOf(finding);

                // The worst thing the database says about a statement is what belongs on the row.
                if (!views.TryGetValue(subject.Key, out var existing) || view.Severity > existing.Severity)
                {
                    views[subject.Key] = view;
                }
            }
        }

        return views;
    }

    private static DatabaseView ViewOf(StoredFinding finding)
    {
        using var evidence = Parse(finding.EvidenceJson);
        var root = evidence?.RootElement;

        // Postgres reports execution time, SQL Server CPU. Neither is renamed to look like the
        // other: a column that quietly means two things is worse than two labelled columns.
        var cpu = Number(root, "avg_cpu_ms") is not null || Number(root, "total_cpu_ms") is not null;

        return new DatabaseView
        {
            Severity = finding.Severity,
            Title = finding.Title,
            Sample = Text(root, "normalized") ?? Text(root, "query"),
            MeanMs = Number(root, "mean_exec_ms") ?? Number(root, "avg_cpu_ms") ?? 0,
            TotalMs = Number(root, "total_exec_ms") ?? Number(root, "total_cpu_ms") ?? 0,
            Calls = (long)(Number(root, "calls") ?? Number(root, "execution_count") ?? 0),
            Measure = cpu ? "CPU" : "time",
            FirstSeen = finding.FirstSeen,
        };
    }

    private static JsonDocument? Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Evidence is whatever a check wrote. A row with no numbers beats an exception page.
            return null;
        }
    }

    private static double? Number(JsonElement? root, string name) =>
        root?.ValueKind == JsonValueKind.Object && root.Value.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static string? Text(JsonElement? root, string name) =>
        root?.ValueKind == JsonValueKind.Object && root.Value.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
