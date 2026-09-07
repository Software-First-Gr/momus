using System.Text.Json;
using Momus.Core;
using Momus.Server.Insights;
using Momus.Server.Store;

namespace Momus.Server.Pages;

/// <summary>
/// One insight, with everything the page needs around it: the day's shape, where in the code it
/// is, what the database said, and the Markdown to paste somewhere else.
/// </summary>
public sealed record InsightCard
{
    public required StoredInsight Insight { get; init; }
    public IReadOnlyList<long>? Series { get; init; }
    public required string EvidencePack { get; init; }

    /// <summary>The chip that makes it actionable rather than interesting.</summary>
    public string? CallSite { get; init; }

    /// <summary>What the database's own scan says about the same subject, if anything.</summary>
    public string? DatabaseSays { get; init; }

    public string? Operation { get; init; }

    /// <summary>Ties a form's controls together for the actions.</summary>
    public string DomId => "i" + Math.Abs(Insight.IdentityKey.GetHashCode()).ToString();

    public static IReadOnlyList<InsightCard> For(
        IReadOnlyList<StoredInsight> insights,
        IReadOnlyDictionary<string, long[]> series,
        StoredTarget? target,
        StoredApp? app,
        string serverVersion) =>
        insights.Select(i => new InsightCard
        {
            Insight = i,
            Series = SeriesFor(i, series),
            EvidencePack = Insights.EvidencePack.Render(i, target, app, serverVersion),
            CallSite = Text(i, "call_site"),
            DatabaseSays = Text(i, "database_says"),
            Operation = Text(i, "operation"),
        }).ToList();

    /// <summary>The day's shape for whichever subject this insight is about.</summary>
    private static long[]? SeriesFor(StoredInsight insight, IReadOnlyDictionary<string, long[]> series)
    {
        foreach (var subject in insight.Subjects)
        {
            if (subject.Kind is not (Subject.Query or Subject.Operation)) continue;
            if (series.TryGetValue(subject.ToString(), out var values)) return values;
        }
        return null;
    }

    private static string? Text(StoredInsight insight, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(insight.EvidenceJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(key, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
