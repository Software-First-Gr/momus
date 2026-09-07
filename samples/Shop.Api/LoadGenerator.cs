namespace Shop.Api;

/// <summary>
/// Keeps a realistic mix of traffic running so the database's statistics views have something to
/// say. Momus reads cumulative counters: one click on a scenario moves them, but only steady
/// traffic makes first-seen dates and "is this getting worse" mean anything.
/// </summary>
public sealed class LoadGenerator(
    SelfClient self,
    Telemetry telemetry,
    IHostApplicationLifetime lifetime,
    ILogger<LoadGenerator> logger,
    string startMode) : BackgroundService
{
    /// <summary>Roughly how many requests per second each mode aims for.</summary>
    private static readonly Dictionary<string, int> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["off"] = 0,
        ["light"] = 2,
        ["heavy"] = 20,
    };

    /// <summary>Starts at whatever <c>Traffic</c> says, so the compose stack is alive on first open.</summary>
    public string Mode { get; private set; } = "off";

    public bool SetMode(string mode)
    {
        if (!Rates.ContainsKey(mode)) return false;
        Mode = mode.ToLowerInvariant();
        telemetry.Log("info", $"Traffic set to {Mode}.");
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        SetMode(startMode);

        // The generator calls this app over HTTP, so it has to wait until the app is listening.
        var started = new TaskCompletionSource();
        await using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var rate = Rates[Mode];
            if (rate == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                continue;
            }

            try
            {
                // The mix a small shop would actually have: mostly order pages and cart writes,
                // a search now and then, a report occasionally.
                var (method, path) = Random.Shared.Next(100) switch
                {
                    < 45 => (HttpMethod.Get, $"/api/orders/{Random.Shared.Next(1, Schema.Orders + 1)}"),
                    < 75 => (HttpMethod.Post, $"/api/cart/{Random.Shared.Next(1, Schema.Customers + 1)}/items"),
                    < 95 => (HttpMethod.Get, "/api/search"),
                    _ => (HttpMethod.Get, "/api/reports/daily"),
                };

                telemetry.Operation(await self.HitAsync(method, path, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                telemetry.Error();
                telemetry.Log("error", $"Traffic: {ex.Message}");
                logger.LogWarning(ex, "Load generator iteration failed.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1000.0 / rate), ct);
        }
    }
}
