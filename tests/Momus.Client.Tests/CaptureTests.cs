using Microsoft.EntityFrameworkCore;
using SampleApp;

namespace Momus.Client.Tests;

/// <summary>
/// What the client actually reports about a statement: where it came from, what its shape is, and
/// how many times one operation ran it. Every insight the server produces is a threshold over
/// exactly these three numbers, so wrong here is wrong everywhere.
/// </summary>
[Collection(ClientCollection.Name)]
public class CaptureTests
{
    [Fact]
    public async Task The_call_site_is_the_line_that_ran_the_query()
    {
        // Regression test for D16. Before it, this was null in every asynchronous application:
        // the stack was walked from the executed callback, where the caller's frames are gone.
        // A null call site looks exactly like one the walker could not name, so nothing failed —
        // and mapping a query to code is the thing the product ranks above everything else.
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("FindTheCallSite", db => WidgetQueries.LoadOneAsync(db, 1));

        var query = Assert.Single(Assert.Single(operations, o => o.Name == "FindTheCallSite").Queries);

        // The file and line of the application code that issued it, not of the framework above it.
        Assert.NotNull(query.CallSite);
        Assert.StartsWith("WidgetQueries.cs:", query.CallSite);
    }

    [Fact]
    public async Task No_literal_and_no_parameter_value_leaves_the_process()
    {
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("Secrets",
            db => db.Widgets.Where(w => w.Name == "hunter2").FirstOrDefaultAsync());

        var query = Assert.Single(Assert.Single(operations, o => o.Name == "Secrets").Queries);

        Assert.DoesNotContain("hunter2", query.Sample);
        Assert.Contains("?", query.Sample);
    }

    [Fact]
    public async Task Two_shapes_of_one_query_share_a_key_and_two_different_ones_do_not()
    {
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("Shapes", async db =>
        {
            await db.Widgets.Where(w => w.Id == 1).FirstOrDefaultAsync();
            await db.Widgets.Where(w => w.Id == 999).FirstOrDefaultAsync();
            await db.Widgets.CountAsync();
        });

        var queries = Assert.Single(operations, o => o.Name == "Shapes").Queries;

        // Two lookups differing only by the value are one statement; the count is another.
        Assert.Equal(2, queries.Count);
        Assert.Equal(2, queries.Single(q => q.Count == 2).Count);
    }

    [Fact]
    public async Task A_loop_is_reported_as_repeats_inside_one_operation()
    {
        // This number is the N in N+1, and NPlusOneInsight fires on it at five.
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("LooksLikeAnNPlusOne",
            db => WidgetQueries.LoadEachAsync(db, Enumerable.Range(1, 6)));

        var operation = Assert.Single(operations, o => o.Name == "LooksLikeAnNPlusOne");
        Assert.Equal(6, Assert.Single(operation.Queries).Count);
        Assert.Equal(6, operation.QueryCount);
    }

    [Theory]
    // EF counts reads, not rows, and the read that finds the end is the difference. Every shape
    // here was measured on all three majors before the client subtracted that one — including the
    // ones that look like they stop early, which do not. If EF ever changes how it enumerates,
    // this is the test that says so, instead of every row count in the product being quietly off.
    [InlineData("first-hit", 1)]
    [InlineData("first-miss", 0)]
    [InlineData("list-all", 3)]
    [InlineData("list-none", 0)]
    [InlineData("single", 1)]
    [InlineData("count", 1)]
    public async Task Rows_are_the_rows_that_came_back(string shape, long expected)
    {
        await using var app = await TestApp.StartAsync();

        await app.RunAsync("Seed", async db =>
        {
            db.Widgets.AddRange(
                new Widget { Name = "a" }, new Widget { Name = "b" }, new Widget { Name = "c" });
            await db.SaveChangesAsync();
        });

        var operations = await app.RunAsync(shape, db => shape switch
        {
            "first-hit" => WidgetQueries.LoadOneAsync(db, 1),
            "first-miss" => WidgetQueries.LoadOneAsync(db, 999),
            "list-all" => WidgetQueries.LoadAllAsync(db),
            "list-none" => db.Widgets.Where(w => w.Id > 500).ToListAsync(),
            "single" => db.Widgets.Where(w => w.Id == 2).SingleOrDefaultAsync(),
            _ => db.Widgets.CountAsync(),
        });

        var query = Assert.Single(Assert.Single(operations, o => o.Name == shape).Queries);
        Assert.Equal(expected, query.Rows);
    }

    [Fact]
    public async Task A_failed_statement_is_counted_as_an_error_and_not_swallowed()
    {
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("Broken", async db =>
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                db.Database.ExecuteSqlRawAsync("select * from no_such_table"));
        });

        var query = Assert.Single(Assert.Single(operations, o => o.Name == "Broken").Queries);
        Assert.Equal(1, query.Errors);
    }
}
