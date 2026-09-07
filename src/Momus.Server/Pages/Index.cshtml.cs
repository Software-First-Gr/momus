using Microsoft.AspNetCore.Mvc.RazorPages;
using Momus.Server.Store;

namespace Momus.Server.Pages;

/// <summary>
/// What to fix first. Five cards, ordered by one score — severity, weighted by how much of the
/// application's traffic actually hits the subject, weighted by how recently it started — because
/// a Critical nothing calls loses to a High on the busiest endpoint.
/// </summary>
public sealed class IndexModel(MomusStore store, ServerOptions options) : PageModel
{
    /// <summary>Five. A list that needs scrolling is a list nobody works through.</summary>
    public const int Cards = 5;

    public IReadOnlyList<StoredTarget> Targets { get; private set; } = [];
    public StoredTarget? Selected { get; private set; }
    public IReadOnlyList<InsightCard> Top { get; private set; } = [];
    public int TotalInsights { get; private set; }
    public StoredApp? App { get; private set; }
    public StoredWindow? LatestWindow { get; private set; }
    public int Port => options.Port;

    public async Task OnGetAsync(string? target, CancellationToken ct)
    {
        Targets = await store.TargetsAsync(ct);
        Selected = Targets.FirstOrDefault(t => t.Id == target) ?? Targets.FirstOrDefault();
        App = (await store.AppsAsync(ct)).FirstOrDefault();
        LatestWindow = await store.LatestWindowAsync(ct: ct);

        if (Selected is null) return;

        var insights = await store.CurrentInsightsAsync(Selected.Id, ct: ct);
        TotalInsights = insights.Count;

        Top = InsightCard.For(
            insights.Take(Cards).ToList(),
            await store.HourlyCallsAsync(ct: ct),
            Selected, App, MomusServer.Version);
    }

    public string HeaderStatus => Selected is null
        ? "no target yet"
        : $"{Selected.Name} · scanned {Humanize.Ago(Selected.LastScanAt)}";
}
