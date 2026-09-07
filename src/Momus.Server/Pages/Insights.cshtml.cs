using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Momus.Core;
using Momus.Core.Insights;
using Momus.Server.Store;

namespace Momus.Server.Pages;

/// <summary>
/// Every insight, in the order they are worth fixing. The home page shows the first five; this is
/// the rest of them, plus the ones that have been muted out of the way.
/// </summary>
public sealed class InsightsModel(MomusStore store) : PageModel
{
    public IReadOnlyList<StoredTarget> Targets { get; private set; } = [];
    public StoredTarget? Selected { get; private set; }
    public IReadOnlyList<InsightCard> Cards { get; private set; } = [];
    public IReadOnlyList<InsightCard> Muted { get; private set; } = [];
    public bool AnyAppReported { get; private set; }

    /// <summary>Plain-language names for the rules, so the page never says "n_plus_one" at anyone.</summary>
    private static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>
    {
        ["n_plus_one"] = "N+1",
        ["hot_query_origin"] = "HOT QUERY",
        ["regression"] = "REGRESSION",
        ["transaction_held_open"] = "TRANSACTION",
        ["pool_wait"] = "POOL",
        ["db_finding"] = "DATABASE",
    };

    public static string KindLabel(string kind) =>
        Kinds.GetValueOrDefault(kind, kind.Replace('_', ' ').ToUpperInvariant());

    public async Task OnGetAsync(string? target, CancellationToken ct) => await LoadAsync(target, ct);

    /// <summary>
    /// Mute, unmute, or mark fixed. Nothing is deleted: an insight that was marked fixed and comes
    /// back reopens itself, and that is a different story from one that is new.
    /// </summary>
    public async Task<IActionResult> OnPostStatusAsync(
        string? target, string identity, string status, CancellationToken ct)
    {
        var targets = await store.TargetsAsync(ct);
        var selected = targets.FirstOrDefault(t => t.Id == target) ?? targets.FirstOrDefault();

        if (selected is not null && !string.IsNullOrEmpty(identity))
        {
            await store.SetInsightStatusAsync(selected.Id, identity, status, ct);
        }

        return RedirectToPage(new { target = selected?.Id });
    }

    private async Task LoadAsync(string? target, CancellationToken ct)
    {
        Targets = await store.TargetsAsync(ct);
        Selected = Targets.FirstOrDefault(t => t.Id == target) ?? Targets.FirstOrDefault();

        var apps = await store.AppsAsync(ct);
        AnyAppReported = apps.Count > 0;
        if (Selected is null) return;

        var all = await store.CurrentInsightsAsync(Selected.Id, includeMuted: true, ct);
        var series = await store.HourlyCallsAsync(ct: ct);
        var app = apps.FirstOrDefault();

        // Muted ones are shown here and nowhere else: this is the page with the button that
        // undoes it, and an insight you can never unmute is one you have deleted by accident.
        Cards = InsightCard.For(
            all.Where(i => i.Status != InsightStatus.Muted).ToList(), series, Selected, app,
            MomusServer.Version);

        Muted = InsightCard.For(
            all.Where(i => i.Status == InsightStatus.Muted).ToList(), series, Selected, app,
            MomusServer.Version);
    }

    public int Count(Severity severity) => Cards.Count(c => c.Insight.Severity == severity);

    /// <summary>Insights that needed both halves. Zero with an app reporting is worth knowing.</summary>
    public int JoinedCount => Cards.Count(c =>
        c.Insight.Kind is "n_plus_one" or "hot_query_origin" or "regression"
            or "transaction_held_open" or "pool_wait");

    public string HeaderStatus => Selected is null
        ? "no target yet"
        : $"{Selected.Name} · {Cards.Count} insight{(Cards.Count == 1 ? "" : "s")}";
}
