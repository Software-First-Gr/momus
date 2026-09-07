using Momus.Core;

namespace Momus.Server.Pages;

/// <summary>Small formatting helpers the pages share. Dates are relative because the question is
/// always "how long has this been true", never "what o'clock was it".</summary>
public static class Humanize
{
    public static string Ago(DateTimeOffset? at) => at is null ? "never" : Ago(DateTimeOffset.UtcNow - at.Value);

    public static string Ago(TimeSpan span) => span switch
    {
        { TotalSeconds: < 10 } => "just now",
        { TotalSeconds: < 60 } => $"{span.TotalSeconds:N0}s ago",
        { TotalMinutes: < 60 } => $"{span.TotalMinutes:N0} min ago",
        { TotalHours: < 24 } => $"{span.TotalHours:N0}h ago",
        { TotalDays: < 14 } => $"{span.TotalDays:N0} days ago",
        _ => $"{span.TotalDays / 7:N0} weeks ago",
    };

    /// <summary>How long a finding has been true — the one thing the CLI could never tell you.</summary>
    public static string Since(DateTimeOffset first)
    {
        var span = DateTimeOffset.UtcNow - first;
        return span switch
        {
            { TotalMinutes: < 2 } => "first seen just now",
            { TotalMinutes: < 60 } => $"first seen {span.TotalMinutes:N0} min ago",
            { TotalHours: < 24 } => $"first seen {span.TotalHours:N0}h ago",
            _ => $"first seen {span.TotalDays:N0} days ago",
        };
    }

    public static string Css(Severity severity) => severity.ToString().ToLowerInvariant();

    public static string Label(Severity severity) => severity.ToString().ToUpperInvariant();
}
