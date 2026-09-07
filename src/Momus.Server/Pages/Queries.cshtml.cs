using Microsoft.AspNetCore.Mvc.RazorPages;
using Momus.Server.Store;

namespace Momus.Server.Pages;

/// <summary>
/// The Queries tab: one row per statement, with the application's view and the database's view of
/// it side by side. Everything else Momus does, something else already does too; this is the part
/// that needs both halves, and it exists only because both compute the same fingerprint.
/// </summary>
public sealed class QueriesModel(MomusStore store, ServerOptions options) : PageModel
{
    /// <summary>How far back the app-side numbers reach. Long enough to be stable, short enough to be now.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>More rows than this and the page is a data dump rather than an answer.</summary>
    public const int MaxRows = 60;

    public IReadOnlyList<StoredTarget> Targets { get; private set; } = [];
    public StoredTarget? Selected { get; private set; }
    public IReadOnlyList<StoredApp> Apps { get; private set; } = [];
    public StoredWindow? LatestWindow { get; private set; }
    public IReadOnlyList<QueryRow> Rows { get; private set; } = [];
    public IReadOnlyList<AppOperationStat> Operations { get; private set; } = [];
    public int HiddenRows { get; private set; }
    public int Port => options.Port;

    /// <summary>True once any application has ever posted a window. Drives the empty state.</summary>
    public bool AnyAppReported => Apps.Count > 0;

    /// <summary>
    /// A client that has stopped sending is the first-run problem, so it is a pill rather than
    /// an empty table nobody can explain.
    /// </summary>
    public bool ClientIsQuiet => LatestWindow is null ||
                                 DateTimeOffset.UtcNow - LatestWindow.ReceivedAt > TimeSpan.FromMinutes(2);

    public async Task OnGetAsync(string? target, CancellationToken ct)
    {
        Targets = await store.TargetsAsync(ct);
        Selected = Targets.FirstOrDefault(t => t.Id == target) ?? Targets.FirstOrDefault();

        Apps = await store.AppsAsync(ct);
        LatestWindow = await store.LatestWindowAsync(ct: ct);

        var since = DateTimeOffset.UtcNow - Window;
        var app = await store.AppQueryStatsAsync(since, ct: ct);
        Operations = (await store.AppOperationStatsAsync(since, ct: ct)).Take(10).ToList();

        var findings = Selected is null ? [] : await store.LatestFindingsAsync(Selected.Id, ct);
        var rows = QueryJoin.Build(app, findings, Selected?.Id ?? "");

        HiddenRows = Math.Max(0, rows.Count - MaxRows);
        Rows = rows.Take(MaxRows).ToList();
    }

    public string HeaderStatus
    {
        get
        {
            if (Apps.Count == 0) return "no application reporting yet";
            var app = Apps[0];
            var version = app.Version is { Length: > 0 } v ? $" {v}" : "";
            return $"{app.Name}{version} · window {Humanize.Ago(LatestWindow?.ReceivedAt)}";
        }
    }
}
