using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Momus.Server.Insights;
using Momus.Server.Store;

namespace Momus.Server;

/// <summary>
/// Re-evaluates the rules on a short tick. It is not tied to the scan, because both halves move:
/// a scan lands every interval and a window lands every few seconds, and an insight is a
/// statement about the two of them together.
/// </summary>
/// <remarks>
/// Everything it reads is local SQLite over a bounded slice of time, so the tick can afford to be
/// short — and it matches the page's own refresh, so what you see is never more than a refresh
/// behind what is true.
/// </remarks>
public sealed class InsightService(
    MomusStore store,
    InsightEngine engine,
    ILogger<InsightService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, int> _counts = [];

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
                logger.LogWarning(ex, "Insight pass failed; trying again in {Seconds:N0}s.",
                    Interval.TotalSeconds);
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var target in await store.TargetsAsync(ct))
        {
            var context = await StoreInsightContext.LoadAsync(store, target.Id, InsightEngine.Window, ct);
            var insights = await engine.EvaluateAsync(context, ct);
            await store.SaveInsightsAsync(target.Id, insights, context.Now, ct);

            // One line when the number changes, not one every fifteen seconds.
            if (_counts.TryGetValue(target.Id, out var previous) && previous == insights.Count) continue;
            _counts[target.Id] = insights.Count;

            logger.LogInformation("{Target}: {Count} insight(s).", target.Name, insights.Count);
        }
    }
}
