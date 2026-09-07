using Microsoft.AspNetCore.Mvc.RazorPages;
using Momus.Core;
using Momus.Server.Store;

namespace Momus.Server.Pages;

/// <summary>
/// What the two halves mean together, worst first. Ordering is severity then how recently it was
/// seen; the score that weighs traffic and recency — and the five "Fix first" cards it fills —
/// arrives in M3.
/// </summary>
public sealed class InsightsModel(MomusStore store, ServerOptions options) : PageModel
{
    public IReadOnlyList<StoredTarget> Targets { get; private set; } = [];
    public StoredTarget? Selected { get; private set; }
    public IReadOnlyList<StoredInsight> Insights { get; private set; } = [];
    public bool AnyAppReported { get; private set; }
    public int Port => options.Port;

    /// <summary>
    /// Which kinds need the app side to say anything. With no client reporting, the tab still
    /// works — it just cannot answer "who runs this", and it should say so rather than look empty.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>
    {
        ["n_plus_one"] = "N+1",
        ["hot_query_origin"] = "HOT QUERY",
        ["db_finding"] = "DATABASE",
    };

    public async Task OnGetAsync(string? target, CancellationToken ct)
    {
        Targets = await store.TargetsAsync(ct);
        Selected = Targets.FirstOrDefault(t => t.Id == target) ?? Targets.FirstOrDefault();
        AnyAppReported = (await store.AppsAsync(ct)).Count > 0;

        if (Selected is not null) Insights = await store.CurrentInsightsAsync(Selected.Id, ct);
    }

    public int Count(Severity severity) => Insights.Count(i => i.Severity == severity);

    public string Label(string kind) => Kinds.GetValueOrDefault(kind, kind.ToUpperInvariant());

    /// <summary>Insights that needed both halves. Zero of them with an app reporting is worth knowing.</summary>
    public int JoinedCount => Insights.Count(i => i.Kind is "n_plus_one" or "hot_query_origin");

    public string HeaderStatus => Selected is null
        ? "no target yet"
        : $"{Selected.Name} · {Insights.Count} insight{(Insights.Count == 1 ? "" : "s")}";
}
