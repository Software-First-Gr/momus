using Microsoft.AspNetCore.Mvc.RazorPages;
using Momus.Core.Insights;
using Momus.Server.Store;

namespace Momus.Server.Pages;

/// <summary>
/// Findings over time with the deploys drawn on top of them. One chart, because the only question
/// worth a chart here is "did this start when we shipped something", and one line answers it.
/// </summary>
public sealed class HistoryModel(MomusStore store) : PageModel
{
    public static readonly TimeSpan Span = TimeSpan.FromDays(7);

    public IReadOnlyList<StoredTarget> Targets { get; private set; } = [];
    public StoredTarget? Selected { get; private set; }
    public IReadOnlyList<FindingsAtHour> History { get; private set; } = [];
    public IReadOnlyList<DeployView> Deploys { get; private set; } = [];
    public DateTimeOffset From { get; private set; }
    public DateTimeOffset To { get; private set; }

    public async Task OnGetAsync(string? target, CancellationToken ct)
    {
        To = DateTimeOffset.UtcNow;
        From = To - Span;

        Targets = await store.TargetsAsync(ct);
        Selected = Targets.FirstOrDefault(t => t.Id == target) ?? Targets.FirstOrDefault();
        Deploys = (await store.DeploysAsync(ct: ct)).Where(d => d.FirstSeen >= From).ToList();

        if (Selected is not null) History = await store.FindingHistoryAsync(Selected.Id, From, ct);
    }

    /// <summary>Where a moment sits across the chart, 0 to 1. Anything outside the span is clamped.</summary>
    public double X(DateTimeOffset at)
    {
        var total = (To - From).TotalSeconds;
        return total <= 0 ? 0 : Math.Clamp((at - From).TotalSeconds / total, 0, 1);
    }

    public int Peak => History.Count == 0 ? 1 : Math.Max(1, History.Max(h => h.Findings));

    public string HeaderStatus => Selected is null
        ? "no target yet"
        : $"{Selected.Name} · {Deploys.Count} deploy{(Deploys.Count == 1 ? "" : "s")} this week";
}
