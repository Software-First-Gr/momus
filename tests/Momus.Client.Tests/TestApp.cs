using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Momus.Client;
using Momus.Client.Internal;
using SampleApp;
using Data = Microsoft.Data.Sqlite;

namespace Momus.Client.Tests;

/// <summary>
/// The client has process-global state by design — <c>MomusOperation.Begin</c> is a static API, so
/// the queue it reaches is a static too — and the call-site cache is shared. Tests that build more
/// than one container in one process therefore have to run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClientCollection
{
    public const string Name = "client";
}

/// <summary>
/// One application with one database, wired the way a real one is: <c>AddDbContext</c> first, then
/// one call to <c>AddMomus</c> and nothing else.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly Data.SqliteConnection _connection;

    private TestApp(IHost host, Data.SqliteConnection connection, OperationQueue? queue)
    {
        _host = host;
        _connection = connection;
        Queue = queue;
    }

    /// <summary>Null when Momus decided to stay off, which is itself worth asserting.</summary>
    public OperationQueue? Queue { get; }

    public IServiceProvider Services => _host.Services;

    /// <param name="register">How the application registers its context, which is what is under test.</param>
    /// <param name="configure">Overrides applied after configuration, as a real caller would.</param>
    /// <param name="callMomusFirst">
    /// Calls <c>AddMomus</c> before the context is registered — the mistake the client is supposed
    /// to survive and complain about, rather than silently report nothing.
    /// </param>
    /// <param name="environment">Defaults to Development, where the client is on without being asked.</param>
    public static async Task<TestApp> StartAsync(
        Action<IServiceCollection, Data.SqliteConnection>? register = null,
        Action<MomusOptions>? configure = null,
        bool callMomusFirst = false,
        string? environment = null)
    {
        MomusRuntime.Reset();

        var connection = new Data.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment ?? Environments.Development,
        });

        register ??= static (services, c) => services.AddDbContext<WidgetDb>(o => o.UseSqlite(c));

        if (callMomusFirst) builder.AddMomus(Options(configure));
        register(builder.Services, connection);
        if (!callMomusFirst) builder.AddMomus(Options(configure));

        var host = builder.Build();

        // The exporter is a hosted service and the host is never started, so nothing is posted
        // anywhere: what the interceptor recorded stays in the queue for the test to read.
        var queue = host.Services.GetService<OperationQueue>();

        var app = new TestApp(host, connection, queue);
        await app.CreateSchemaAsync();
        return app;
    }

    /// <summary>Everything a request does, from one scope, under one named operation.</summary>
    public async Task<IReadOnlyList<CompletedOperation>> RunAsync(
        string operation, Func<WidgetDb, Task> work)
    {
        using (var scope = _host.Services.CreateScope())
        {
            var db = Context(scope.ServiceProvider);
            using (MomusOperation.Begin(operation))
            {
                await work(db);
            }
        }

        return Drain();
    }

    /// <summary>Whatever the queue holds now, including ambient statements recorded outside an operation.</summary>
    public IReadOnlyList<CompletedOperation> Drain()
    {
        var operations = new List<CompletedOperation>();
        while (Queue?.Reader.TryRead(out var operation) == true) operations.Add(operation);
        return operations;
    }

    /// <summary>Resolves the context however this application registered it.</summary>
    public WidgetDb Context(IServiceProvider services) =>
        services.GetService<WidgetDb>() ??
        services.GetRequiredService<IDbContextFactory<WidgetDb>>().CreateDbContext();

    private async Task CreateSchemaAsync()
    {
        using var scope = _host.Services.CreateScope();
        await Context(scope.ServiceProvider).Database.EnsureCreatedAsync();

        // The DDL above is real database work and the client records it, correctly, as ambient.
        // Clearing it here keeps every test's assertions about its own statements.
        Drain();
    }

    private static Action<MomusOptions> Options(Action<MomusOptions>? configure) => o =>
    {
        // Nothing is ever posted in these tests; the endpoint only has to be somewhere the
        // exporter would not accidentally reach if it ever did run.
        o.Endpoint = "http://127.0.0.1:1";
        configure?.Invoke(o);
    };

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        await _connection.DisposeAsync();
        MomusRuntime.Reset();
    }
}
