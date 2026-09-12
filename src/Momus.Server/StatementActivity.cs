using Momus.Core;
using Momus.Server.Store;
using static System.FormattableString;

namespace Momus.Server;

/// <summary>
/// Turns the database's statement counters, which run from the last statistics reset, into what
/// happened over the stretch of time the app side is talking about.
/// </summary>
/// <remarks>
/// <para>
/// pg_stat_statements and dm_exec_query_stats only ever add up. Ranked as they are, a seed INSERT
/// that ran once five days ago stays "#3 by total time" for as long as nobody resets the view —
/// found on the demo, where exactly that held a Fix first slot while the Queries tab beside it
/// said "the last 60 min". The check cannot do better: it has no memory, and the CLI has to keep
/// working without one. The server has the previous scans, so it keeps each statement's counters
/// per scan and ranks on the difference.
/// </para>
/// <para>
/// The numbers are rewritten into the evidence keys the check already used, so the Queries tab,
/// <c>hot_query_origin</c> and the N+1 rule read the window without knowing it is one. The
/// lifetime numbers stay beside them under <c>lifetime_*</c>.
/// </para>
/// <para>
/// One cost is accepted: a statement missing from the baseline counts from zero. That is right for
/// a statement that is new, and overstates one that was merely below the cut-off the scan fetched
/// — bounded by fetching ten times more statements than are kept.
/// </para>
/// </remarks>
public static class StatementActivity
{
    /// <summary>Statement findings kept per scan, once ranked on the window.</summary>
    public const int Keep = 50;

    /// <summary>The checks whose findings are statement counters rather than observations.</summary>
    public static readonly IReadOnlySet<string> CheckIds =
        new HashSet<string>(StringComparer.Ordinal) { "pg.top_queries", "mssql.top_cpu_queries" };

    public sealed record Result(ScanReport Report, IReadOnlyList<StatementSample> Samples);

    public static Result Apply(ScanReport report, StatementBaseline? baseline, int keep = Keep)
    {
        var samples = new List<StatementSample>();

        var checks = report.Checks.Select(check =>
        {
            if (!check.Succeeded || !CheckIds.Contains(check.CheckId)) return check;

            var (findings, counted) = Rank(check.Findings, baseline, report.StartedAt, keep);
            samples.AddRange(counted);
            return check with { Findings = findings };
        }).ToList();

        return new Result(report with { Checks = checks }, samples);
    }

    /// <summary>"in the last hour", "in the last 12 min", or "since statistics were reset" when there is no baseline yet.</summary>
    public static string Span(TimeSpan? window) => window switch
    {
        null => "since statistics were reset",
        { TotalMinutes: < 1 } w => Invariant($"in the last {Math.Max(1, (int)Math.Round(w.TotalSeconds))} s"),
        { TotalMinutes: < 59.5 } w => Invariant($"in the last {(int)Math.Round(w.TotalMinutes)} min"),
        { TotalHours: < 1.5 } => "in the last hour",
        { } w => Invariant($"in the last {(int)Math.Round(w.TotalHours)} h"),
    };

    private static (IReadOnlyList<Finding> Findings, IReadOnlyList<StatementSample> Samples) Rank(
        IReadOnlyList<Finding> findings, StatementBaseline? baseline, DateTimeOffset now, int keep)
    {
        var other = new List<Finding>();
        var statements = new Dictionary<string, Statement>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            var key = finding.Subjects.Where(s => s.Kind == Subject.Query).Select(s => s.Key).FirstOrDefault();
            var counters = Counters(finding);

            // "pg_stat_statements is not installed" is a finding about the check, not a statement.
            if (key is null || counters is null)
            {
                other.Add(finding);
                continue;
            }

            // Two entries in the view can normalize to one fingerprint; the finding is per statement.
            statements[key] = statements.TryGetValue(key, out var seen)
                ? seen with { Calls = seen.Calls + counters.Value.Calls, TotalMs = seen.TotalMs + counters.Value.TotalMs }
                : new Statement(key, finding, counters.Value.Cpu, counters.Value.Calls, counters.Value.TotalMs);
        }

        var window = baseline is null ? (TimeSpan?)null : now - baseline.CapturedAt;

        var ranked = statements.Values
            .Select(s => Difference(s, baseline))
            .Where(d => d.Calls > 0 && !StatementKind.IsBookkeeping(Text(d.Statement.Finding)))
            .OrderByDescending(d => d.TotalMs)
            .ThenByDescending(d => d.Calls)
            .Take(keep)
            .Select((d, i) => Rewrite(d.Statement, d.Calls, d.TotalMs, i + 1, window))
            .ToList();

        var samples = statements.Values.Select(s => new StatementSample(s.Fingerprint, s.Calls, s.TotalMs)).ToList();
        return ([.. ranked, .. other], samples);
    }

    private static (Statement Statement, long Calls, double TotalMs) Difference(Statement statement, StatementBaseline? baseline)
    {
        if (baseline is null || !baseline.Samples.TryGetValue(statement.Fingerprint, out var before))
        {
            return (statement, statement.Calls, statement.TotalMs);
        }

        // Fewer calls than last time means the counters started again — a reset, or the statement
        // was evicted and came back — so everything it has now happened since the baseline.
        if (statement.Calls < before.Calls) return (statement, statement.Calls, statement.TotalMs);

        return (statement, statement.Calls - before.Calls, Math.Max(0, statement.TotalMs - before.TotalMs));
    }

    private static Finding Rewrite(Statement statement, long calls, double totalMs, int rank, TimeSpan? window)
    {
        var mean = totalMs / calls;
        var span = Span(window);

        var evidence = new Dictionary<string, object?>(statement.Finding.Evidence)
        {
            ["window_seconds"] = window is { } w ? Math.Round(w.TotalSeconds) : null,
            ["lifetime_calls"] = statement.Calls,
            ["lifetime_total_ms"] = Math.Round(statement.TotalMs, 1),
        };

        if (statement.Cpu)
        {
            evidence["execution_count"] = calls;
            evidence["total_cpu_ms"] = Math.Round(totalMs, 1);
            evidence["avg_cpu_ms"] = Math.Round(mean, 2);
        }
        else
        {
            evidence["calls"] = calls;
            evidence["total_exec_ms"] = Math.Round(totalMs, 1);
            evidence["mean_exec_ms"] = Math.Round(mean, 2);
        }

        return statement.Finding with
        {
            Title = statement.Cpu
                ? Invariant($"#{rank} query by CPU {span}: {Prose.Millis(totalMs)} across {Prose.Count(calls, "execution")}")
                : Invariant($"#{rank} query by total time {span}: {Prose.Millis(totalMs)} across {Prose.Count(calls, "call")}"),
            Detail = statement.Cpu
                ? Invariant($"Average CPU {Prose.Millis(mean)} per execution {span}. Query text (truncated) is in the evidence.")
                : Invariant($"Mean execution time {Prose.Millis(mean)} {span}. Query text (truncated) is in the evidence."),
            Evidence = evidence,
        };
    }

    private static (bool Cpu, long Calls, double TotalMs)? Counters(Finding finding)
    {
        var evidence = finding.Evidence;

        if (evidence.TryGetValue("total_exec_ms", out var total) && evidence.TryGetValue("calls", out var calls))
        {
            return (false, Db.ToLong(calls), Db.ToDouble(total));
        }

        if (evidence.TryGetValue("total_cpu_ms", out total) && evidence.TryGetValue("execution_count", out calls))
        {
            return (true, Db.ToLong(calls), Db.ToDouble(total));
        }

        return null;
    }

    private static string? Text(Finding finding) =>
        (finding.Evidence.GetValueOrDefault("normalized") ?? finding.Evidence.GetValueOrDefault("query")) as string;

    private sealed record Statement(string Fingerprint, Finding Finding, bool Cpu, long Calls, double TotalMs);
}
