using System.Text;
using System.Text.Json;
using Momus.Server.Store;
using static System.FormattableString;

namespace Momus.Server.Insights;

/// <summary>
/// One insight as Markdown, written to be pasted into a coding agent. That is the actual delivery
/// mechanism for most of what Momus finds: nobody reads a dashboard and then types the numbers
/// into a prompt by hand, so the numbers, the statement and the versions travel together.
/// </summary>
public static class EvidencePack
{
    public static string Render(
        StoredInsight insight, StoredTarget? target, StoredApp? app, string serverVersion)
    {
        var md = new StringBuilder();

        md.AppendLine($"## {insight.Title}");
        md.AppendLine();
        md.Append(Invariant($"`{insight.Kind}` · **{insight.Severity}** · first seen {insight.FirstSeen:u}, "));
        md.AppendLine(Invariant($"seen {insight.SeenCount}×"));
        md.AppendLine();
        md.AppendLine(insight.Detail);

        if (insight.Recommendation is { Length: > 0 } recommendation)
        {
            md.AppendLine();
            md.AppendLine($"**What to try:** {recommendation}");
        }

        var evidence = Parse(insight.EvidenceJson);
        if (Statement(evidence) is { Length: > 0 } statement)
        {
            md.AppendLine();
            md.AppendLine("### The statement");
            md.AppendLine();
            md.AppendLine("```sql");
            md.AppendLine(statement);
            md.AppendLine("```");
            md.AppendLine();
            md.AppendLine("_Normalized: every literal and parameter is a `?`. No value from your " +
                          "database or your users appears in this text._");
        }

        if (evidence.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("### Numbers");
            md.AppendLine();
            md.AppendLine("| | |");
            md.AppendLine("| --- | --- |");
            foreach (var (key, value) in evidence.Where(e => e.Key != "statement"))
            {
                md.AppendLine($"| {key.Replace('_', ' ')} | {value} |");
            }
        }

        md.AppendLine();
        md.AppendLine("### Where this came from");
        md.AppendLine();
        foreach (var subject in insight.Subjects) md.AppendLine($"- `{subject}`");
        if (target is not null)
        {
            md.Append(Invariant($"- Database `{target.Name}` ({target.Provider}{Database(target)}), "));
            md.AppendLine(Invariant($"{Engine(target.ServerVersion)}"));
        }
        if (app is not null)
        {
            md.AppendLine(Invariant(
                $"- Application `{app.Name}` {app.Version ?? "(no version)"} in {app.Environment ?? "?"}"));
        }
        md.AppendLine($"- Momus {serverVersion}");

        return md.ToString();
    }

    /// <summary>Postgres's version() carries the compiler it was built with. Nobody needs that here.</summary>
    private static string Engine(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "version unknown";
        var comma = version.IndexOf(',');
        return comma > 0 ? version[..comma] : version;
    }

    private static string Database(StoredTarget target) =>
        target.DatabaseName is { Length: > 0 } name ? $"/{name}" : "";

    /// <summary>The normalized SQL, when the insight is about a statement.</summary>
    private static string? Statement(IReadOnlyDictionary<string, string> evidence) =>
        evidence.GetValueOrDefault("statement");

    /// <summary>
    /// Evidence is whatever a rule wrote, so it is read as loosely as it was written. A pack with
    /// no numbers in it is worth more than an exception page.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Parse(string json)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return values;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;

                values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
            }
        }
        catch (JsonException)
        {
            // Nothing to add, and nothing worth failing a page over.
        }

        return values;
    }
}
