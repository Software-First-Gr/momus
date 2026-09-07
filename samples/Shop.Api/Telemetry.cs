using System.Collections.Concurrent;

namespace Shop.Api;

/// <summary>
/// What the control panel shows: counters and a short log of what the app just did. This is the
/// app's own view of itself, deliberately naive — the point of the demo is that it agrees or
/// disagrees with what Momus reads out of the database.
/// </summary>
public sealed class Telemetry
{
    private readonly ConcurrentQueue<LogLine> _log = new();
    private long _operations;
    private long _queries;
    private long _errors;

    public long Operations => Interlocked.Read(ref _operations);
    public long Queries => Interlocked.Read(ref _queries);
    public long Errors => Interlocked.Read(ref _errors);
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void Operation(int queries)
    {
        Interlocked.Increment(ref _operations);
        Interlocked.Add(ref _queries, queries);
    }

    public void Error() => Interlocked.Increment(ref _errors);

    public void Log(string level, string message)
    {
        _log.Enqueue(new LogLine(DateTimeOffset.UtcNow, level, message));
        while (_log.Count > 200 && _log.TryDequeue(out _)) { }
    }

    public IReadOnlyList<LogLine> Recent() => _log.Reverse().Take(60).ToList();

    public sealed record LogLine(DateTimeOffset At, string Level, string Message);
}
