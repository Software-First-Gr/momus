using static System.FormattableString;

namespace Momus.Core;

/// <summary>
/// Numbers inside Momus's own sentences: a count that agrees with its noun, and an amount of time
/// in the unit a person would say it in. Invariant, like the rest of Momus's prose.
/// </summary>
/// <remarks>
/// Found on the running demo: "0.0s across 2,910 calls", "across 1 calls" and a session "idle in
/// transaction for 00:05:04". Each one is correct and each one reads like a log line.
/// </remarks>
public static class Prose
{
    /// <summary>"1 call", "2,910 calls".</summary>
    public static string Count(long n, string singular, string? plural = null) =>
        Invariant($"{n:N0} {(n == 1 ? singular : plural ?? singular + "s")}");

    /// <summary>An amount given in milliseconds: "0.02 ms", "7.5 ms", "140 ms", "4.7 s", "3.2 min", "1.5 h".</summary>
    /// <remarks>Two places below 10 ms, because a hot statement at 0.02 ms each is common and "0.0 ms" says nothing.</remarks>
    public static string Millis(double ms) => ms switch
    {
        < 10 => Invariant($"{ms:0.##} ms"),
        < 1_000 => Invariant($"{ms:N0} ms"),
        < 60_000 => Invariant($"{ms / 1_000:N1} s"),
        < 3_600_000 => Invariant($"{ms / 60_000:N1} min"),
        _ => Invariant($"{ms / 3_600_000:N1} h"),
    };

    /// <summary>How long something has been going on: "42 s", "5 min 4 s", "1 h 12 min", "3 days 4 h".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        return span switch
        {
            { TotalMinutes: < 1 } => Invariant($"{span.Seconds} s"),
            { TotalHours: < 1 } => Invariant($"{(int)span.TotalMinutes} min {span.Seconds} s"),
            { TotalDays: < 1 } => Invariant($"{(int)span.TotalHours} h {span.Minutes} min"),
            _ => Invariant($"{Count((int)span.TotalDays, "day")} {span.Hours} h"),
        };
    }
}
