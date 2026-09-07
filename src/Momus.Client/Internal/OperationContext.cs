using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Momus.Client.Internal;

/// <summary>
/// One unit of work — an HTTP request, or a block named with <see cref="MomusOperation.Begin"/> — and
/// everything it did to the database. Queries are counted here, per operation, because the N in
/// N+1 is only visible from inside one request: forty executions of one statement across a minute
/// is a busy endpoint, forty inside one request is a bug.
/// </summary>
internal sealed class OperationContext(string kind, HttpContext? http, string? explicitName)
{
    private static readonly AsyncLocal<OperationContext?> Ambient = new();

    private readonly Dictionary<(string Key, string? CallSite), QueryTally> _queries = new();
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private OperationContext? _previous;
    private string? _name;

    public static OperationContext? Current
    {
        get => Ambient.Value;
        private set => Ambient.Value = value;
    }

    public string Kind { get; } = kind;
    public int Overflow { get; private set; }
    public double DbMs { get; private set; }
    public int QueryCount { get; private set; }
    public double ElapsedMs => Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;

    public static OperationContext Begin(string kind, HttpContext? http, string? explicitName)
    {
        var context = new OperationContext(kind, http, explicitName) { _previous = Current };
        Current = context;
        return context;
    }

    /// <summary>
    /// Restores whatever was in scope before, rather than clearing it: a named background block
    /// inside a request must not leave the rest of that request unattributed.
    /// </summary>
    public static void End(OperationContext context)
    {
        if (ReferenceEquals(Current, context)) Current = context._previous;
    }

    /// <summary>
    /// The operation's name, worked out the first time anything asks. Routing has run by then, so
    /// the endpoint is known — which is why the middleware can sit first in the pipeline and still
    /// produce "GET /orders/{id}" rather than "GET /orders/4711".
    /// </summary>
    public string Name => _name ??= Resolve();

    private string Resolve()
    {
        if (explicitName is { Length: > 0 }) return explicitName;

        if (http is not null)
        {
            var endpoint = http.Features.Get<IEndpointFeature>()?.Endpoint;
            if (endpoint is RouteEndpoint route)
            {
                return $"{http.Request.Method} /{route.RoutePattern.RawText?.TrimStart('/')}";
            }
            if (endpoint?.DisplayName is { Length: > 0 } display) return display;

            // No endpoint: middleware ran a query before routing, or nothing matched.
            return $"{http.Request.Method} {http.Request.Path}";
        }

        return Activity.Current?.DisplayName is { Length: > 0 } activity
            ? activity
            : Momus.Core.Ingest.IngestOperation.AmbientName;
    }

    /// <summary>Records one statement execution against this operation.</summary>
    public void Record(string key, string? callSite, string sample, double durationMs, long rows, bool failed, int maxKeys)
    {
        QueryCount++;
        DbMs += durationMs;

        var id = (key, callSite);
        if (!_queries.TryGetValue(id, out var tally))
        {
            if (_queries.Count >= maxKeys)
            {
                Overflow++;
                return;
            }
            tally = new QueryTally(sample);
            _queries[id] = tally;
        }

        tally.Add(durationMs, rows, failed);
    }

    /// <summary>Adds rows to a statement already recorded, once its reader has been read to the end.</summary>
    public void AddRows(string key, string? callSite, long rows)
    {
        if (rows <= 0) return;
        if (_queries.TryGetValue((key, callSite), out var tally)) tally.Rows += rows;
    }

    public CompletedOperation Complete(bool countsAsOperation) => new()
    {
        Name = Name,
        Kind = Kind,
        DurationMs = ElapsedMs,
        DbMs = DbMs,
        QueryCount = QueryCount,
        Overflow = Overflow,
        CountsAsOperation = countsAsOperation,
        Queries = _queries.Select(q => new QueryExecutions
        {
            Key = q.Key.Key,
            CallSite = q.Key.CallSite,
            Sample = q.Value.Sample,
            Count = q.Value.Count,
            SumMs = q.Value.SumMs,
            MaxMs = q.Value.MaxMs,
            Rows = q.Value.Rows,
            Errors = q.Value.Errors,
            Histogram = q.Value.Histogram,
        }).ToArray(),
    };

    private sealed class QueryTally(string sample)
    {
        public string Sample { get; } = sample;
        public long Count { get; private set; }
        public double SumMs { get; private set; }
        public double MaxMs { get; private set; }
        public long Rows { get; set; }
        public long Errors { get; private set; }
        public long[] Histogram { get; } = new long[Core.Ingest.Timing.Buckets];

        public void Add(double durationMs, long rows, bool failed)
        {
            Count++;
            SumMs += durationMs;
            if (durationMs > MaxMs) MaxMs = durationMs;
            Rows += rows;
            if (failed) Errors++;
            Histogram[Core.Ingest.Timing.BucketFor(durationMs)]++;
        }
    }
}

/// <summary>What crosses the channel to the exporter: one finished operation, already folded.</summary>
internal sealed class CompletedOperation
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required double DurationMs { get; init; }
    public required double DbMs { get; init; }
    public required int QueryCount { get; init; }
    public required int Overflow { get; init; }

    /// <summary>
    /// False for statements that ran outside any operation. Their timings still belong in the
    /// query stats; counting them as requests would inflate every per-operation average.
    /// </summary>
    public required bool CountsAsOperation { get; init; }

    public required IReadOnlyList<QueryExecutions> Queries { get; init; }
}

internal sealed class QueryExecutions
{
    public required string Key { get; init; }
    public string? CallSite { get; init; }
    public required string Sample { get; init; }
    public required long Count { get; init; }
    public required double SumMs { get; init; }
    public required double MaxMs { get; init; }
    public required long Rows { get; init; }
    public required long Errors { get; init; }
    public required long[] Histogram { get; init; }
}
