using System.Collections.Concurrent;

namespace Shop.Api;

/// <summary>
/// Scenarios that have to last minutes — an idle transaction, a held pool of connections — run
/// here so the HTTP call returns immediately and the UI can show a countdown and a Stop button.
/// Postgres only calls a transaction a problem after five minutes, and waiting for that with the
/// browser spinning would teach the wrong lesson.
/// </summary>
public sealed class BackgroundJobs(ILogger<BackgroundJobs> logger, Telemetry telemetry) : IDisposable
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public sealed record Job(string Id, string Title, DateTimeOffset StartedAt, DateTimeOffset EndsAt)
    {
        internal CancellationTokenSource Cts { get; init; } = new();
        public int SecondsLeft => Math.Max(0, (int)(EndsAt - DateTimeOffset.UtcNow).TotalSeconds);
    }

    public IReadOnlyList<Job> Running => _jobs.Values.OrderBy(j => j.StartedAt).ToList();

    /// <summary>Starts <paramref name="work"/> under an id. Starting the same id twice replaces the first.</summary>
    public Job Start(string id, string title, TimeSpan duration, Func<CancellationToken, Task> work)
    {
        Stop(id);
        var job = new Job(id, title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + duration);
        _jobs[id] = job;
        telemetry.Log("info", $"{title} started, for {duration.TotalMinutes:N0} min.");

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token);
                timeout.CancelAfter(duration);
                await work(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: either the duration elapsed or the user pressed Stop.
            }
            catch (Exception ex)
            {
                telemetry.Error();
                telemetry.Log("error", $"{title} failed: {ex.Message}");
                logger.LogWarning(ex, "Background scenario {Id} failed.", id);
            }
            finally
            {
                if (_jobs.TryRemove(id, out var finished))
                {
                    finished.Cts.Dispose();
                    telemetry.Log("info", $"{title} finished.");
                }
            }
        });

        return job;
    }

    public bool Stop(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        job.Cts.Cancel();
        return true;
    }

    public void Dispose()
    {
        foreach (var job in _jobs.Values) job.Cts.Cancel();
    }
}
