using Momus.Core.Ingest;

namespace Momus.Client.Internal;

/// <summary>
/// A few seconds of the application's life, folded down to one row per statement per operation.
/// This is the only thing that ever leaves the process, and it holds no literal, no parameter
/// value and no result row — only shapes, counts and times.
/// </summary>
internal sealed class Window(int maxKeys)
{
    private readonly Dictionary<(string Key, string Operation, string? CallSite), QueryAggregate> _queries = new();
    private readonly Dictionary<string, OperationAggregate> _operations = new();
    private readonly Dictionary<(string Operation, string? CallSite), TransactionAggregate> _transactions = new();
    private readonly Dictionary<string, PoolAggregate> _pool = new();

    public DateTimeOffset From { get; } = DateTimeOffset.UtcNow;
    public long Overflow { get; private set; }
    public bool IsEmpty => _queries.Count == 0 && _operations.Count == 0 &&
                           _transactions.Count == 0 && _pool.Count == 0 && Overflow == 0;

    public void Add(CompletedOperation operation)
    {
        Overflow += operation.Overflow;

        if (operation.CountsAsOperation)
        {
            if (!_operations.TryGetValue(operation.Name, out var op))
            {
                _operations[operation.Name] = op = new OperationAggregate(operation.Kind);
            }
            op.Add(operation.DurationMs, operation.QueryCount, operation.DbMs);
        }

        foreach (var transaction in operation.Transactions)
        {
            var id = (operation.Name, transaction.CallSite);
            if (!_transactions.TryGetValue(id, out var tx))
            {
                if (_transactions.Count >= maxKeys) continue;
                _transactions[id] = tx = new TransactionAggregate();
            }
            tx.Add(transaction.OpenMs, transaction.DbMs);
        }

        foreach (var wait in operation.PoolWaits)
        {
            if (!_pool.TryGetValue(operation.Name, out var pool))
            {
                if (_pool.Count >= maxKeys) continue;
                _pool[operation.Name] = pool = new PoolAggregate();
            }
            pool.Add(wait);
        }

        foreach (var query in operation.Queries)
        {
            var id = (query.Key, operation.Name, query.CallSite);
            if (!_queries.TryGetValue(id, out var aggregate))
            {
                if (_queries.Count >= maxKeys)
                {
                    Overflow += query.Count;
                    continue;
                }
                _queries[id] = aggregate = new QueryAggregate(query.Sample);
            }

            aggregate.Add(query);
        }
    }

    /// <summary>Attaches this window to an app and its targets, ready to be posted.</summary>
    public IngestBatch ToBatch(IngestApp app, IReadOnlyList<IngestTarget> targets, string? targetId) => new()
    {
        App = app,
        Window = new IngestWindow(From, DateTimeOffset.UtcNow),
        Targets = targets,
        Overflow = Overflow,
        Operations = _operations.Select(o => new IngestOperation(
            o.Key,
            o.Value.Kind,
            o.Value.Count,
            new Stat(o.Value.SumMs, o.Value.MaxMs),
            new Counts(o.Value.QuerySum, o.Value.QueryMax),
            new Stat(o.Value.DbMs, 0))).ToArray(),
        Queries = _queries.Select(q => new IngestQuery(
            q.Key.Key,
            targetId,
            q.Key.Operation,
            q.Key.CallSite,
            q.Value.Sample,
            q.Value.Count,
            new Timing(q.Value.SumMs, q.Value.MaxMs, q.Value.Histogram),
            q.Value.Rows,
            q.Value.MaxRepeats,
            q.Value.Errors)).ToArray(),
        Transactions = _transactions.Select(t => new IngestTransaction(
            t.Key.Operation,
            t.Key.CallSite,
            t.Value.Count,
            new Timing(t.Value.OpenSum, t.Value.OpenMax, t.Value.Histogram),
            new Stat(t.Value.DbSum, t.Value.DbMax))).ToArray(),
        Pool = _pool.Select(p => new IngestPool(
            p.Key,
            p.Value.Count,
            new Timing(p.Value.Sum, p.Value.Max, p.Value.Histogram))).ToArray(),
    };

    private sealed class TransactionAggregate
    {
        public long Count { get; private set; }
        public double OpenSum { get; private set; }
        public double OpenMax { get; private set; }
        public double DbSum { get; private set; }
        public double DbMax { get; private set; }
        public long[] Histogram { get; } = new long[Timing.Buckets];

        public void Add(double openMs, double dbMs)
        {
            Count++;
            OpenSum += openMs;
            if (openMs > OpenMax) OpenMax = openMs;
            DbSum += dbMs;
            if (dbMs > DbMax) DbMax = dbMs;
            Histogram[Timing.BucketFor(openMs)]++;
        }
    }

    private sealed class PoolAggregate
    {
        public long Count { get; private set; }
        public double Sum { get; private set; }
        public double Max { get; private set; }
        public long[] Histogram { get; } = new long[Timing.Buckets];

        public void Add(double waitMs)
        {
            Count++;
            Sum += waitMs;
            if (waitMs > Max) Max = waitMs;
            Histogram[Timing.BucketFor(waitMs)]++;
        }
    }

    private sealed class QueryAggregate(string sample)
    {
        public string Sample { get; } = sample;
        public long Count { get; private set; }
        public double SumMs { get; private set; }
        public double MaxMs { get; private set; }
        public long Rows { get; private set; }
        public long Errors { get; private set; }
        public int MaxRepeats { get; private set; }
        public long[] Histogram { get; } = new long[Timing.Buckets];

        public void Add(QueryExecutions execution)
        {
            Count += execution.Count;
            SumMs += execution.SumMs;
            if (execution.MaxMs > MaxMs) MaxMs = execution.MaxMs;
            Rows += execution.Rows;
            Errors += execution.Errors;

            // The N in N+1: how many times this statement ran inside a single operation, at worst.
            if (execution.Count > MaxRepeats) MaxRepeats = (int)Math.Min(execution.Count, int.MaxValue);

            for (var i = 0; i < Histogram.Length && i < execution.Histogram.Length; i++)
            {
                Histogram[i] += execution.Histogram[i];
            }
        }
    }

    private sealed class OperationAggregate(string kind)
    {
        public string Kind { get; } = kind;
        public long Count { get; private set; }
        public double SumMs { get; private set; }
        public double MaxMs { get; private set; }
        public long QuerySum { get; private set; }
        public long QueryMax { get; private set; }
        public double DbMs { get; private set; }

        public void Add(double durationMs, int queries, double dbMs)
        {
            Count++;
            SumMs += durationMs;
            if (durationMs > MaxMs) MaxMs = durationMs;
            QuerySum += queries;
            if (queries > QueryMax) QueryMax = queries;
            DbMs += dbMs;
        }
    }
}
