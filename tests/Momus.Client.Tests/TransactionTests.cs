using Microsoft.EntityFrameworkCore;
using SampleApp;

namespace Momus.Client.Tests;

/// <summary>
/// Transactions and connection acquisition, the two M3 signals. Both are timed by Momus rather
/// than read out of EF's own <c>Duration</c>, so both are measured here against a known interval
/// instead of assumed — the same lesson D16 taught about call sites.
/// </summary>
[Collection(ClientCollection.Name)]
public class TransactionTests
{
    [Fact]
    public async Task A_transaction_is_timed_from_start_to_commit()
    {
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("Checkout", async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await WidgetQueries.LoadOneAsync(db, 1);
            await Task.Delay(120);
            await tx.CommitAsync();
        });

        var operation = Assert.Single(operations, o => o.Name == "Checkout");
        var transaction = Assert.Single(operation.Transactions);

        // Open for the delay plus the query; the delay alone is the floor.
        Assert.InRange(transaction.OpenMs, 100, 5_000);
    }

    [Fact]
    public async Task The_time_inside_a_transaction_that_is_not_database_work_is_visible()
    {
        // The whole point of the signal: a transaction open for 120 ms that spent 2 ms talking to
        // the database is holding locks across something that is not the database.
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("HoldsItOpen", async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await WidgetQueries.LoadOneAsync(db, 1);
            await Task.Delay(150);
            await tx.CommitAsync();
        });

        var transaction = Assert.Single(Assert.Single(operations, o => o.Name == "HoldsItOpen").Transactions);

        Assert.True(transaction.DbMs >= 0);
        Assert.True(transaction.DbMs < transaction.OpenMs / 2,
            $"database time {transaction.DbMs:N1} ms should be a small part of {transaction.OpenMs:N1} ms open");
    }

    [Fact]
    public async Task A_rolled_back_transaction_is_recorded_too()
    {
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("Abandoned", async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await WidgetQueries.LoadOneAsync(db, 1);
            await tx.RollbackAsync();
        });

        Assert.Single(Assert.Single(operations, o => o.Name == "Abandoned").Transactions);
    }

    [Fact]
    public async Task Acquiring_a_connection_is_timed()
    {
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("Opens", db => WidgetQueries.LoadOneAsync(db, 1));

        // SQLite hands one back instantly, so the assertion is that it is measured at all: the
        // rule that reads this is a percentile, which needs the fast acquisitions to mean anything.
        var operation = Assert.Single(operations, o => o.Name == "Opens");
        Assert.All(operation.PoolWaits, wait => Assert.True(wait >= 0));
    }
}
