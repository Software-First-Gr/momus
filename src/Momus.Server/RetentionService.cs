using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Momus.Server.Store;

namespace Momus.Server;

/// <summary>
/// Keeps the file from growing forever: raw five-second windows are folded into hourly rows after
/// a day, and hourly rows are dropped after a week. Both numbers are the free tier's (D10) and
/// changing them is the whole of what Pro changes here.
/// </summary>
public sealed class RetentionService(MomusStore store, ILogger<RetentionService> logger) : BackgroundService
{
    /// <summary>Often enough that an hour's windows are folded soon after they age out.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often the file is actually shrunk. Deleting rows in SQLite frees pages for reuse but
    /// does not return them, so a store that has held a week of windows keeps that size forever
    /// without this. It rewrites the whole file, so it is a nightly job and not a ten-minute one.
    /// </summary>
    private static readonly TimeSpan VacuumEvery = TimeSpan.FromHours(24);

    private DateTimeOffset _lastVacuum = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct)) return;
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Housekeeping failing is never a reason to stop serving the page.
                logger.LogWarning(ex, "Retention pass failed; trying again in {Minutes:N0} min.",
                    Interval.TotalMinutes);
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var folded = await store.RollupAsync(now - MomusStore.RawRetention, ct);
        var removed = await store.TrimAsync(now - MomusStore.RollupRetention, ct);

        if (folded > 0 || removed > 0)
        {
            logger.LogInformation("Retention: folded {Folded} window(s), dropped {Removed} hourly row(s).",
                folded, removed);
        }

        if (now - _lastVacuum < VacuumEvery) return;
        _lastVacuum = now;

        var before = store.FileSizeBytes;
        await store.VacuumAsync(ct);
        var after = store.FileSizeBytes;

        logger.LogInformation("Retention: vacuumed, {Before} to {After}.",
            Diagnostics.DiagnosticsMarkdown.Size(before), Diagnostics.DiagnosticsMarkdown.Size(after));
    }
}
