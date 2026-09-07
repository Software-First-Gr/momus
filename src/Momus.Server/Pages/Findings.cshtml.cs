using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Momus.Core;
using Momus.Server.Store;

namespace Momus.Server.Pages;

/// <summary>
/// The database's own scan, unchanged: this is the console report kept as a tab so that nothing
/// the collector produces is ever hidden behind an interpretation of it.
/// </summary>
public sealed class FindingsModel(MomusStore store, ScanScheduler scheduler, ServerOptions options) : PageModel
{
    public IReadOnlyList<StoredTarget> Targets { get; private set; } = [];
    public StoredTarget? Selected { get; private set; }
    public StoredScan? LatestScan { get; private set; }
    /// <summary>
    /// How many findings from one check the page shows. The top-queries check keeps fifty rows for
    /// the store — that is the raw material for the Queries tab — but a page that lists all fifty
    /// buries the two that matter.
    /// </summary>
    public const int MaxPerCheck = 8;

    public IReadOnlyList<StoredFinding> Findings { get; private set; } = [];

    /// <summary>Findings held back per check, so the page can say what it is not showing.</summary>
    public IReadOnlyDictionary<string, int> Hidden { get; private set; } =
        new Dictionary<string, int>();
    public IReadOnlyList<StoredCheckFailure> Failures { get; private set; } = [];
    public int ScanCount { get; private set; }
    public TimeSpan ScanInterval => options.ScanInterval;
    public int Port => options.Port;

    public async Task OnGetAsync(string? target, CancellationToken ct)
    {
        await LoadAsync(target, ct);
    }

    public async Task<IActionResult> OnPostScanNowAsync(string? target, CancellationToken ct)
    {
        await LoadAsync(target, ct);
        if (Selected is not null) scheduler.RequestScan(Selected.Id);

        // Straight back to the page: the scheduler picks the request up within a couple of seconds.
        return RedirectToPage(new { target = Selected?.Id });
    }

    private async Task LoadAsync(string? target, CancellationToken ct)
    {
        Targets = await store.TargetsAsync(ct);
        Selected = Targets.FirstOrDefault(t => t.Id == target) ?? Targets.FirstOrDefault();
        if (Selected is null) return;

        LatestScan = await store.LatestScanAsync(Selected.Id, ct);
        ScanCount = await store.ScanCountAsync(Selected.Id, ct);
        var all = await store.LatestFindingsAsync(Selected.Id, ct);
        Findings = all.GroupBy(f => f.CheckId).SelectMany(g => g.Take(MaxPerCheck)).ToList();
        Hidden = all.GroupBy(f => f.CheckId)
            .Where(g => g.Count() > MaxPerCheck)
            .ToDictionary(g => g.Key, g => g.Count() - MaxPerCheck);

        Failures = await store.LatestCheckFailuresAsync(Selected.Id, ct);
    }

    public int Count(Severity severity) => Findings.Count(f => f.Severity == severity);

    /// <summary>Checks in this category with findings the page is not showing.</summary>
    public IEnumerable<(string CheckId, int Count)> HiddenIn(IEnumerable<StoredFinding> group) =>
        group.Select(f => f.CheckId).Distinct()
            .Where(Hidden.ContainsKey)
            .Select(id => (id, Hidden[id]));

    /// <summary>A scan older than three intervals means the loop is stuck or the database is gone.</summary>
    public bool IsStale => Selected?.LastScanAt is null ||
                           DateTimeOffset.UtcNow - Selected.LastScanAt > options.ScanInterval * 3;

    public string HeaderStatus => Selected is null
        ? "no target yet"
        : $"{Selected.Name} · scanned {Humanize.Ago(Selected.LastScanAt)}";
}
