using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Momus.Client.Internal;
using Momus.Core.Ingest;
using SampleApp;

namespace Momus.Client.Tests;

/// <summary>
/// One operation, several threads. A request that runs two contexts at once — the
/// <c>IDbContextFactory</c> pattern, <c>await Task.WhenAll(a.ToListAsync(), b.CountAsync())</c> —
/// carries one <c>OperationContext</c> in its execution context, so the interceptors record into it
/// from two threads at the same moment. Mostly nothing fails when that goes wrong: the numbers are
/// just smaller than what ran, and every insight is a threshold over those numbers. Now and then a
/// map corrupted by two inserts at once made the snapshot throw, out of the application's own
/// <c>using</c> block.
/// </summary>
[Collection(ClientCollection.Name)]
public class ConcurrencyTests
{
    // Before the lock, 18 to 20 of these 20 rounds were wrong in every one of 15 runs across the
    // three frameworks, and one run in about fifteen threw from Complete (2026-09-24, 14 cores).
    // The whole test takes about a second per framework.
    private const int Rounds = 20;
    private const int Workers = 8;
    private const int Iterations = 25;

    /// <summary>One statement per iteration, each its own key: only the alias differs, and a literal would not.</summary>
    private static readonly string[] PerIteration =
        Enumerable.Range(0, Iterations).Select(i => $"select 1 as n{i}").ToArray();

    [Fact]
    public async Task Statements_run_at_once_from_several_contexts_are_all_counted()
    {
        // A SqliteConnection is not safe across threads, so each worker gets its own connection to
        // one database: a named shared-cache database lives for as long as one connection holds it.
        var connectionString = $"Data Source=momus-concurrency-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();

        await using var app = await TestApp.StartAsync(
            register: (services, _) => services.AddDbContextFactory<WidgetDb>(o => o.UseSqlite(connectionString)),
            // Every connection opened is a pool-wait record and every transaction a transaction
            // record, both bounded by this ceiling; it must not be what the test ends up measuring.
            configure: o => o.MaxKeysPerOperation = 10_000);

        await app.RunAsync("Seed", async db =>
        {
            db.Widgets.Add(new Widget { Name = "a" });
            await db.SaveChangesAsync();
        });

        var factory = app.Services.GetRequiredService<IDbContextFactory<WidgetDb>>();
        var wrong = new List<string>();

        for (var round = 0; round < Rounds; round++)
        {
            using (MomusOperation.Begin("Concurrent"))
            {
                // Task.Run rather than a bare WhenAll: SQLite completes its "async" calls
                // synchronously, so without a thread each the workers would take turns.
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var workers = Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
                {
                    await using var db = await factory.CreateDbContextAsync();
                    await start.Task;

                    for (var i = 0; i < Iterations; i++)
                    {
                        // Deferred, so eight open transactions do not queue for SQLite's one writer.
                        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadUncommitted))
                        {
                            await WidgetQueries.LoadOneAsync(db, 1);
                            await tx.CommitAsync();
                        }

                        await db.Widgets.CountAsync();

                        // A statement new to the operation, reached by every worker at about the same
                        // time: this is what exercises the insert into the per-operation map.
                        await db.Database.ExecuteSqlRawAsync(PerIteration[i]);
                    }
                })).ToArray();

                start.SetResult();
                await Task.WhenAll(workers);
            }

            var operation = Assert.Single(app.Drain(), o => o.Name == "Concurrent");
            var problems = Check(operation);
            if (problems.Count > 0) wrong.Add($"round {round}: {string.Join("; ", problems)}");
        }

        var faults = MomusRuntime.TakeFaults();
        Assert.True(wrong.Count == 0 && faults == 0,
            $"{wrong.Count} of {Rounds} rounds miscounted, {faults} exceptions caught inside the client." +
            Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void A_snapshot_does_not_move_when_a_statement_records_after_it()
    {
        // A statement finishing on another thread just as the operation ends can record after the
        // snapshot was taken. It cannot be in the snapshot, and it must not reach into it either:
        // the exporter folds the snapshot on its own thread.
        var operation = OperationContext.Begin(IngestOperation.Background, null, "Late");
        OperationContext.End(operation);

        operation.Record("k", null, "select ?", 1.5, rows: 1, failed: false, maxKeys: 200);
        var snapshot = operation.Complete(countsAsOperation: true);
        operation.Record("k", null, "select ?", 1.5, rows: 1, failed: false, maxKeys: 200);

        var query = Assert.Single(snapshot.Queries);
        Assert.Equal(1, query.Count);
        Assert.Equal(1, query.Histogram.Sum());
    }

    private static List<string> Check(CompletedOperation operation)
    {
        const int each = Workers * Iterations;
        var problems = new List<string>();

        void Expect(string what, double expected, double actual)
        {
            if (actual != expected) problems.Add(FormattableString.Invariant($"{what} {actual} (expected {expected})"));
        }

        // Per iteration: the lookup, the count, and one of the per-iteration statements.
        Expect("statements", each * 3, operation.QueryCount);
        Expect("distinct statements", 2 + Iterations, operation.Queries.Count);
        Expect("overflow", 0, operation.Overflow);

        var lookup = operation.Queries.Where(q => q.Sample.Contains("limit", StringComparison.OrdinalIgnoreCase)).ToList();
        var count = operation.Queries.Where(q => q.Sample.Contains("count", StringComparison.OrdinalIgnoreCase)).ToList();
        Expect("lookup keys", 1, lookup.Count);
        Expect("count keys", 1, count.Count);
        if (lookup.Count == 1)
        {
            Expect("lookup executions", each, lookup[0].Count);
            Expect("lookup rows", each, lookup[0].Rows);
        }
        if (count.Count == 1)
        {
            Expect("count executions", each, count[0].Count);
            Expect("count rows", each, count[0].Rows);
        }

        foreach (var query in operation.Queries.Except(lookup).Except(count))
        {
            Expect($"executions of '{query.Sample}'", Workers, query.Count);
        }

        foreach (var query in operation.Queries)
        {
            Expect($"histogram of '{query.Sample}'", query.Count, query.Histogram.Sum());
        }

        // The operation's database time is the sum of its statements' times, added in another
        // order: equal to rounding, and short by whole statements when an update was lost.
        var sum = operation.Queries.Sum(q => q.SumMs);
        if (Math.Abs(operation.DbMs - sum) > 1e-6 * Math.Max(1, sum))
        {
            problems.Add(FormattableString.Invariant(
                $"database time {operation.DbMs:F4} ms against {sum:F4} ms across its statements"));
        }

        Expect("transactions", each, operation.Transactions.Count);

        // A connection is opened for the transaction, for the count and for the raw statement.
        Expect("connection acquisitions", each * 3, operation.PoolWaits.Count);

        return problems;
    }
}
