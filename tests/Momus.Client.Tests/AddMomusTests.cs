using Microsoft.EntityFrameworkCore;
using SampleApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Momus.Client.Tests;

/// <summary>
/// The promise is one line and no other code. It is kept by rewriting the
/// <c>DbContextOptions&lt;T&gt;</c> registrations in the container, which is a trick rather than a
/// documented extension point — so it is re-proved here against every EF Core major the package
/// ships for, and against all three ways an application can register a context.
/// </summary>
[Collection(ClientCollection.Name)]
public class AddMomusTests
{
    [Fact]
    public async Task One_line_is_enough_for_a_statement_to_be_recorded()
    {
        await using var app = await TestApp.StartAsync();

        var operations = await app.RunAsync("ReadOneWidget",
            db => db.Widgets.Where(w => w.Id == 1).FirstOrDefaultAsync());

        var operation = Assert.Single(operations, o => o.Name == "ReadOneWidget");
        var query = Assert.Single(operation.Queries);

        Assert.Equal(1, query.Count);
        Assert.NotEmpty(query.Key);
        Assert.Equal(1, operation.QueryCount);
    }

    [Fact]
    public async Task A_pooled_context_is_hooked_too()
    {
        await using var app = await TestApp.StartAsync(
            (services, connection) => services.AddDbContextPool<WidgetDb>(o => o.UseSqlite(connection)));

        var operations = await app.RunAsync("Pooled",
            db => db.Widgets.Where(w => w.Id == 1).FirstOrDefaultAsync());

        Assert.NotEmpty(Assert.Single(operations, o => o.Name == "Pooled").Queries);
    }

    [Fact]
    public async Task A_context_from_a_factory_is_hooked_too()
    {
        await using var app = await TestApp.StartAsync(
            (services, connection) => services.AddDbContextFactory<WidgetDb>(o => o.UseSqlite(connection)));

        var operations = await app.RunAsync("FromFactory",
            db => db.Widgets.Where(w => w.Id == 1).FirstOrDefaultAsync());

        Assert.NotEmpty(Assert.Single(operations, o => o.Name == "FromFactory").Queries);
    }

    [Fact]
    public async Task Called_before_the_context_it_hooks_nothing_and_records_nothing()
    {
        // The documented ordering rule, asserted rather than trusted: there is nothing to rewrite
        // yet when AddMomus runs first, and the client says so at startup.
        await using var app = await TestApp.StartAsync(callMomusFirst: true);

        var operations = await app.RunAsync("TooEarly",
            db => db.Widgets.Where(w => w.Id == 1).FirstOrDefaultAsync());

        Assert.Empty(operations);
    }

    [Fact]
    public async Task Outside_Development_it_stays_off_unless_asked()
    {
        await using var app = await TestApp.StartAsync(
            configure: o => o.Enabled = null, environment: Environments.Production);

        Assert.Null(app.Queue);
    }

    [Fact]
    public async Task Outside_Development_one_setting_turns_it_on()
    {
        await using var app = await TestApp.StartAsync(
            configure: o => o.Enabled = true, environment: Environments.Production);

        var operations = await app.RunAsync("ProductionOn",
            db => db.Widgets.Where(w => w.Id == 1).FirstOrDefaultAsync());

        Assert.NotEmpty(Assert.Single(operations, o => o.Name == "ProductionOn").Queries);
    }

    [Fact]
    public async Task A_statement_outside_any_operation_is_still_counted()
    {
        await using var app = await TestApp.StartAsync();

        using (var scope = app.Services.CreateScope())
        {
            await app.Context(scope.ServiceProvider).Widgets.CountAsync();
        }

        var ambient = Assert.Single(app.Drain());
        Assert.False(ambient.CountsAsOperation);
        Assert.NotEmpty(ambient.Queries);
    }
}
