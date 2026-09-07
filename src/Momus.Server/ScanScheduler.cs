using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Momus.Core;
using Momus.Server.Store;

namespace Momus.Server;

/// <summary>
/// Scans every target on a schedule and writes the result to the store. A target that cannot be
/// reached is recorded as a failed scan and tried again on the next tick: nothing here is allowed
/// to stop the loop, because a server that dies on one bad connection string is worse than useless.
/// </summary>
public sealed class ScanScheduler(
    MomusStore store,
    ServerOptions options,
    IScanTargetFactory factory,
    ILogger<ScanScheduler> logger) : BackgroundService
{
    private readonly CollectorEngine _engine = new();
    private readonly ConcurrentDictionary<string, byte> _requested = new();

    /// <summary>How long ago each target was last scanned, for the header's staleness pill.</summary>
    public DateTimeOffset? LastTick { get; private set; }

    /// <summary>Scan this target on the next tick, whatever its schedule says. The UI's "Scan now".</summary>
    public void RequestScan(string targetId) => _requested[targetId] = 0;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Scanning every {Interval}.", Describe(options.ScanInterval));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan tick failed; continuing.");
            }

            // Ticks are short so a target added in the UI is scanned within seconds rather than
            // at the end of the interval.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        LastTick = DateTimeOffset.UtcNow;

        foreach (var target in await store.TargetsAsync(ct))
        {
            var requested = _requested.TryRemove(target.Id, out _);
            var due = target.LastScanAt is null ||
                      DateTimeOffset.UtcNow - target.LastScanAt >= options.ScanInterval;

            if (!requested && !due) continue;

            await ScanAsync(target, ct);
        }
    }

    /// <summary>Scans one target now and stores the outcome, whether it worked or not.</summary>
    public async Task ScanAsync(StoredTarget target, CancellationToken ct = default)
    {
        try
        {
            var report = await RunWithHostFallbackAsync(target, ct);
            var scanId = await store.SaveScanAsync(target.Id, report, ct);

            var failed = report.Checks.Count(c => !c.Succeeded);
            logger.LogInformation(
                "Scanned {Target}: {Findings} finding(s) from {Checks} checks in {Ms:N0} ms{Failed} (scan {ScanId}).",
                target.Name, report.Checks.Sum(c => c.Findings.Count), report.Checks.Count,
                report.Duration.TotalMilliseconds, failed > 0 ? $", {failed} check(s) failed" : "", scanId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not scan {Target}: {Message}", target.Name, ex.Message);
            await store.RecordScanFailureAsync(target.Id, ex.Message, ct);
        }
    }

    /// <summary>
    /// Runs the scan, and if a containerised server cannot reach a <c>localhost</c> target, tries
    /// once more against the Docker host and keeps the string that worked.
    /// </summary>
    private async Task<ScanReport> RunWithHostFallbackAsync(StoredTarget target, CancellationToken ct)
    {
        try
        {
            return await _engine.ScanAsync(factory.Create(target.Provider, target.ConnectionString), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && DockerHostFallback.InContainer)
        {
            var alternative = DockerHostFallback.Alternative(target.ConnectionString);
            if (alternative is null) throw;

            logger.LogInformation(
                "{Target} is not reachable at localhost from inside the container ({Message}); trying {Host}.",
                target.Name, ex.Message, DockerHostFallback.DockerHost);

            var report = await _engine.ScanAsync(factory.Create(target.Provider, alternative), ct);

            await store.UpsertTargetAsync(target with { ConnectionString = alternative }, ct);
            logger.LogInformation("{Target} now points at {Host}.", target.Name, DockerHostFallback.DockerHost);
            return report;
        }
    }

    private static string Describe(TimeSpan interval) => interval switch
    {
        { TotalHours: >= 1 } => $"{interval.TotalHours:N0}h",
        { TotalMinutes: >= 1 } => $"{interval.TotalMinutes:N0}m",
        _ => $"{interval.TotalSeconds:N0}s",
    };
}
