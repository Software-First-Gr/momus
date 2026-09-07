using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Momus.Server.Store;

namespace Momus.Server.Pages;

/// <summary>Add and remove targets. Targets that came from the environment are shown but not
/// removable here, because the next restart would bring them straight back.</summary>
public sealed class SettingsModel(
    MomusStore store,
    ScanScheduler scheduler,
    IScanTargetFactory factory,
    ServerOptions options) : PageModel
{
    public IReadOnlyList<StoredTarget> Targets { get; private set; } = [];
    public IReadOnlyList<string> Providers => factory.Providers;
    public string DataDirectory => Path.GetFullPath(options.DataDirectory);
    public TimeSpan ScanInterval => options.ScanInterval;

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => Targets = await store.TargetsAsync(ct);

    public async Task<IActionResult> OnPostAddAsync(
        string name, string provider, string connectionString, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        connectionString = (connectionString ?? "").Trim();

        if (connectionString.Length == 0)
        {
            Error = "A connection string is required.";
            return RedirectToPage();
        }

        if (name.Length == 0) name = provider;

        try
        {
            // Fail here rather than silently every minute in the scheduler.
            factory.Create(provider, connectionString);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return RedirectToPage();
        }

        var spec = new TargetSpec(name, provider, connectionString, "ui");
        await store.UpsertTargetAsync(new StoredTarget
        {
            Id = spec.Id,
            Name = spec.Name,
            Provider = spec.Provider,
            ConnectionString = spec.ConnectionString,
            Source = spec.Source,
        }, ct);

        scheduler.RequestScan(spec.Id);
        Message = $"Added {spec.Name}. The first scan runs in a moment.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string id, CancellationToken ct)
    {
        await store.RemoveTargetAsync(id, ct);
        Message = $"Removed {id} and everything Momus remembered about it.";
        return RedirectToPage();
    }
}
