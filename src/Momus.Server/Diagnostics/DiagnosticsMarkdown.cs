using System.Text;
using static System.FormattableString;

namespace Momus.Server.Diagnostics;

/// <summary>
/// The report as something you can paste into an issue, a chat, or a coding agent. It is the whole
/// reason the diagnostics page exists in this shape: "it isn't working" is not a bug report, and
/// asking someone to describe their database by hand gets a worse answer than asking them to copy
/// twenty lines.
/// </summary>
/// <remarks>No connection string appears here. Check that again before adding a field.</remarks>
public static class DiagnosticsMarkdown
{
    public static string Render(DiagnosticsReport report)
    {
        var md = new StringBuilder();

        md.AppendLine("## Momus diagnostics");
        md.AppendLine();
        md.AppendLine(Invariant($"- Server `{report.ServerVersion}`, up {Short(report.Uptime)}, schema v{report.SchemaVersion}"));
        md.AppendLine(Invariant($"- Store {report.DatabaseBytes / 1024.0 / 1024:N1} MB, scanning every {Short(report.ScanInterval)}"));
        md.AppendLine(Invariant($"- Client `{report.Ingest.ClientVersion ?? "none reporting"}`"));
        md.AppendLine();

        md.AppendLine("### What Momus thinks");
        md.AppendLine();
        foreach (var check in report.Checks)
        {
            var mark = check.Verdict switch
            {
                Verdict.Problem => "**PROBLEM**",
                Verdict.Warning => "**WARNING**",
                _ => "OK",
            };

            md.AppendLine($"- {mark} — {check.Headline}");
            if (check.WhatToDo is { } todo) md.AppendLine($"  - {todo}");
        }
        md.AppendLine();

        md.AppendLine("### Databases");
        md.AppendLine();
        if (report.Targets.Count == 0)
        {
            md.AppendLine("_None configured._");
        }
        else
        {
            md.AppendLine("| Target | Provider | Server | Source | Scans | Findings | Statement findings | Last error |");
            md.AppendLine("| --- | --- | --- | --- | ---: | ---: | ---: | --- |");
            foreach (var t in report.Targets)
            {
                md.Append(Invariant($"| {t.Name} | {t.Provider} | {Engine(t.ServerVersion)} | {t.Source} | "));
                md.AppendLine(Invariant($"{t.ScansKept} | {t.Findings} | {t.StatementFindings} | {t.LastError ?? "—"} |"));
            }
        }
        md.AppendLine();

        md.AppendLine("### Applications");
        md.AppendLine();
        if (report.Apps.Count == 0)
        {
            md.AppendLine("_None reporting._");
        }
        else
        {
            md.AppendLine("| App | Version | Environment | Instances | Last seen |");
            md.AppendLine("| --- | --- | --- | ---: | --- |");
            foreach (var a in report.Apps)
            {
                md.Append(Invariant($"| {a.Name} | {a.Version ?? "?"} | {a.Environment ?? "?"} | "));
                md.AppendLine(Invariant($"{a.InstanceCount} | {Short(DateTimeOffset.UtcNow - a.LastSeen)} ago |"));
            }
        }
        md.AppendLine();

        md.AppendLine(Invariant($"### The join, over the last {Short(report.Window)}"));
        md.AppendLine();
        md.AppendLine(Invariant($"- Statements from the app: {report.AppStatements}"));
        md.AppendLine(Invariant($"- Statements the database ranks: {report.DatabaseStatements}"));
        md.AppendLine(Invariant($"- **Matched on both sides: {report.Joined}**"));
        md.AppendLine(Invariant($"- With a call site: {report.Ingest.StatementsWithCallSite} of {report.Ingest.DistinctStatements} ({Percent(report.Ingest.CallSiteCoverage)})"));
        md.AppendLine(Invariant($"- Insights: {report.Insights}"));
        md.AppendLine(Invariant($"- Windows: {report.Ingest.WindowsSince} received, {report.Ingest.RawWindows} raw and {report.Ingest.HourlyWindows} hourly kept"));
        md.AppendLine(Invariant($"- Dropped by the client: {report.Ingest.Dropped}, unattributed: {report.Ingest.Overflow}"));

        return md.ToString();
    }

    /// <summary>
    /// Postgres answers <c>version()</c> with the compiler it was built by. The build host is not
    /// what anyone reading a bug report needs, and it makes the table unreadable.
    /// </summary>
    private static string Engine(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "?";

        var comma = version.IndexOf(',');
        var trimmed = comma > 0 ? version[..comma] : version;
        return trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
    }

    /// <summary>Invariant culture writes "67 %"; English writes "67%".</summary>
    internal static string Percent(double fraction) => Invariant($"{fraction * 100:N0}%");

    private static string Short(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => Invariant($"{span.TotalSeconds:N0}s"),
        { TotalMinutes: < 60 } => Invariant($"{span.TotalMinutes:N0}m"),
        { TotalHours: < 48 } => Invariant($"{span.TotalHours:N0}h"),
        _ => Invariant($"{span.TotalDays:N0}d"),
    };
}
