using Momus.Core;
using Momus.Server.Insights;
using Momus.Server.Store;

namespace Momus.Server.Diagnostics;

/// <summary>Reads the store and turns it into the verdicts on the diagnostics page.</summary>
public sealed class DiagnosticsBuilder(MomusStore store, ServerOptions options)
{
    /// <summary>
    /// The process's own start, not this type's: a static initialised on first use runs when the
    /// diagnostics page is first opened, which reports an uptime of zero every time.
    /// </summary>
    private static readonly DateTimeOffset Started = ProcessStart();

    private static DateTimeOffset ProcessStart()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            // Some sandboxes refuse to say. An uptime of "since this page was first opened" is
            // wrong but harmless, and better than failing the page that exists to diagnose things.
            return DateTimeOffset.UtcNow;
        }
    }

    /// <summary>A client quiet for longer than this has stopped, or cannot reach the server.</summary>
    private static readonly TimeSpan ClientIsQuiet = TimeSpan.FromMinutes(2);

    public async Task<DiagnosticsReport> BuildAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - InsightEngine.Window;

        var targets = new List<TargetHealth>();
        var joined = 0;
        var appStatements = 0;
        var databaseStatements = 0;
        var insights = 0;

        foreach (var target in await store.TargetsAsync(ct))
        {
            var findings = await store.LatestFindingsAsync(target.Id, ct);
            var statementFindings = QueryJoin.FromFindings(findings.Select(f => f.ToView()).ToList());

            targets.Add(new TargetHealth
            {
                Id = target.Id,
                Name = target.Name,
                Provider = target.Provider,
                Source = target.Source,
                DatabaseName = target.DatabaseName,
                ServerVersion = target.ServerVersion,
                LastScanAt = target.LastScanAt,
                LastError = target.LastError,
                ScansKept = await store.ScanCountAsync(target.Id, ct),
                Findings = findings.Count,
                StatementFindings = statementFindings.Count,
                FailedChecks = await store.LatestCheckFailuresAsync(target.Id, ct),
            });

            // The join, measured exactly the way the Queries tab measures it.
            var context = await StoreInsightContext.LoadAsync(store, target.Id, InsightEngine.Window, ct);
            var rows = QueryJoin.Build(context.QueryStats, context.LatestFindings, target.Id);

            joined += rows.Count(r => r is { App: not null, Database: not null });
            appStatements += rows.Count(r => r.App is not null);
            databaseStatements += rows.Count(r => r.Database is not null);
            insights += (await store.CurrentInsightsAsync(target.Id, ct)).Count;
        }

        var report = new DiagnosticsReport
        {
            ServerVersion = MomusServer.Version,
            Uptime = now - Started,
            DataDirectory = Path.GetFullPath(options.DataDirectory),
            DatabaseBytes = FileSize(store.DatabasePath),
            SchemaVersion = await store.SchemaVersionAsync(ct),
            ScanInterval = options.ScanInterval,
            Window = InsightEngine.Window,
            Targets = targets,
            Apps = await store.AppsAsync(ct),
            Ingest = await store.IngestHealthAsync(since, ct),
            LatestWindow = await store.LatestWindowAsync(ct: ct),
            Joined = joined,
            AppStatements = appStatements,
            DatabaseStatements = databaseStatements,
            Insights = insights,
        };

        return report with { Checks = Judge(report, now) };
    }

    /// <summary>
    /// The verdicts, in the order someone would work through them: can it see a database, can it
    /// hear an application, and do the two halves actually meet.
    /// </summary>
    private static IReadOnlyList<Check> Judge(DiagnosticsReport report, DateTimeOffset now)
    {
        var checks = new List<Check>();

        // ---- the database half
        if (report.Targets.Count == 0)
        {
            checks.Add(new Check(Verdict.Problem, "No database to scan.",
                "Add one on the Settings page, or start Momus with --target."));
        }

        foreach (var target in report.Targets)
        {
            if (target.LastError is { } error)
            {
                checks.Add(new Check(Verdict.Problem, $"{target.Name}: the last scan failed — {error}",
                    "Usually the host, the password, or a firewall. Momus retries every interval."));
            }
            else if (target.LastScanAt is null)
            {
                checks.Add(new Check(Verdict.Warning, $"{target.Name} has not been scanned yet.",
                    "Give it one scan interval."));
            }
            else if (now - target.LastScanAt > report.ScanInterval * 3)
            {
                checks.Add(new Check(Verdict.Warning,
                    $"{target.Name} was last scanned {Humanize(now - target.LastScanAt.Value)} ago, " +
                    $"which is longer than three intervals."));
            }
            else
            {
                checks.Add(new Check(Verdict.Ok,
                    $"{target.Name}: scanned {Humanize(now - target.LastScanAt.Value)} ago, " +
                    $"{target.Findings} finding(s)."));
            }

            if (target.FailedChecks.Count > 0)
            {
                checks.Add(new Check(Verdict.Warning,
                    $"{target.Name}: {target.FailedChecks.Count} check(s) could not run — " +
                    string.Join("; ", target.FailedChecks.Select(f => $"{f.CheckId}: {f.Error}")),
                    "Almost always a permission the scanning user does not have."));
            }

            // The single most common half-broken install, and it looks exactly like a quiet
            // database: every other check works and statement ranking is simply absent.
            if (target.LastError is null && target.LastScanAt is not null && target.StatementFindings == 0)
            {
                checks.Add(new Check(Verdict.Problem,
                    $"{target.Name}: the database is not reporting any statements.",
                    target.Provider.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)
                        ? "Either pg_stat_statements is not installed, or the scanning user cannot " +
                          "see other users' statements. GRANT pg_monitor fixes the second; the first " +
                          "needs the extension in shared_preload_libraries and CREATE EXTENSION."
                        : "The scanning login likely lacks VIEW SERVER STATE (or VIEW DATABASE STATE " +
                          "on Azure SQL), so the query statistics views come back empty."));
            }
        }

        // ---- the application half
        if (report.Apps.Count == 0)
        {
            checks.Add(new Check(Verdict.Warning, "No application has ever reported.",
                "Add Momus.Client and call builder.AddMomus() after your AddDbContext calls. " +
                "Everything database-side works without it."));
        }
        else if (report.LatestWindow is null || now - report.LatestWindow.ReceivedAt > ClientIsQuiet)
        {
            checks.Add(new Check(Verdict.Problem,
                $"The last window arrived {Humanize(now - (report.LatestWindow?.ReceivedAt ?? now))} ago.",
                "The application is stopped, or its Momus:Endpoint does not reach this server. " +
                "The application's own log says which, once."));
        }
        else
        {
            checks.Add(new Check(Verdict.Ok,
                $"{report.Apps[0].Name} is reporting: {report.Ingest.WindowsSince} window(s) " +
                $"and {report.Ingest.DistinctStatements} distinct statement(s) in the last hour."));
        }

        if (report.Ingest.Dropped > 0)
        {
            checks.Add(new Check(Verdict.Warning,
                $"The client dropped {report.Ingest.Dropped:N0} operation(s) in the last hour.",
                "Its queue filled up, so those requests are missing from every number here. " +
                "Raise Momus:QueueCapacity, or lower Momus:FlushSeconds so windows leave sooner."));
        }

        if (report.Ingest.Overflow > 0)
        {
            checks.Add(new Check(Verdict.Warning,
                $"{report.Ingest.Overflow:N0} execution(s) went unattributed in the last hour.",
                "A window hit its key limit. Raise Momus:MaxKeysPerWindow if the application really " +
                "does run that many distinct statements."));
        }

        // ---- the two halves meeting
        if (report.AppStatements > 0 && report.Ingest.StatementsWithCallSite == 0)
        {
            checks.Add(new Check(Verdict.Problem,
                "No statement has a call site, so nothing can be mapped to your code.",
                "Deploy the portable PDBs next to the assembly. If they are there, this is a bug " +
                "in Momus worth reporting with this page."));
        }
        else if (report.AppStatements > 0 && report.Ingest.CallSiteCoverage < 0.5)
        {
            checks.Add(new Check(Verdict.Warning,
                $"Only {DiagnosticsMarkdown.Percent(report.Ingest.CallSiteCoverage)} of statements carry a call site."));
        }

        if (report.AppStatements > 0 && report.DatabaseStatements > 0)
        {
            checks.Add(report.Joined == 0
                ? new Check(Verdict.Problem,
                    $"The app reported {report.AppStatements} statement(s) and the database ranked " +
                    $"{report.DatabaseStatements}, and none of them matched.",
                    "The two sides are fingerprinting the same SQL differently, which is the one " +
                    "failure that makes Momus pointless. Worth reporting with this page.")
                : new Check(Verdict.Ok,
                    $"{report.Joined} statement(s) matched on both sides. " +
                    $"{report.Insights} insight(s) from it."));
        }

        return checks;
    }

    private static long FileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Humanize(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{span.TotalSeconds:N0}s",
        { TotalMinutes: < 60 } => $"{span.TotalMinutes:N0} min",
        { TotalHours: < 24 } => $"{span.TotalHours:N0}h",
        _ => $"{span.TotalDays:N0} days",
    };
}
