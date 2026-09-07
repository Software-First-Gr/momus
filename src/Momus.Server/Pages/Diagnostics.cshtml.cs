using Microsoft.AspNetCore.Mvc.RazorPages;
using Momus.Server.Diagnostics;

namespace Momus.Server.Pages;

/// <summary>
/// The page to open when something is not as expected — and the page to copy from when asking
/// someone else about it.
/// </summary>
public sealed class DiagnosticsModel(DiagnosticsBuilder builder) : PageModel
{
    public DiagnosticsReport Report { get; private set; } = null!;
    public string Markdown { get; private set; } = "";

    public async Task OnGetAsync(CancellationToken ct)
    {
        Report = await builder.BuildAsync(ct);
        Markdown = DiagnosticsMarkdown.Render(Report);
    }

    public static string Css(Verdict verdict) => verdict switch
    {
        Verdict.Problem => "high",
        Verdict.Warning => "medium",
        _ => "ok",
    };

    public static string Label(Verdict verdict) => verdict switch
    {
        Verdict.Problem => "PROBLEM",
        Verdict.Warning => "CHECK",
        _ => "OK",
    };

    public string HeaderStatus => Report.AnyProblem
        ? "something is not working"
        : $"{Report.Joined} statement(s) matched on both sides";
}
