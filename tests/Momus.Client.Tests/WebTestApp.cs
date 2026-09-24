using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Momus.Client.Internal;
using SampleApp;
using Data = Microsoft.Data.Sqlite;

namespace Momus.Client.Tests;

/// <summary>
/// A real ASP.NET Core application on a real Kestrel port, for what <see cref="TestApp"/> cannot
/// show: the client's middleware, and requests whose lifetime is the thing under test. Kestrel
/// rather than a test server, so a WebSocket is an actual upgraded connection and needs no package.
/// </summary>
internal sealed class WebTestApp : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Data.SqliteConnection _connection;

    private WebTestApp(WebApplication app, Data.SqliteConnection connection)
    {
        _app = app;
        _connection = connection;
    }

    public Uri BaseAddress { get; private set; } = null!;

    public IServiceProvider Services => _app.Services;

    /// <param name="map">The application's endpoints.</param>
    /// <param name="configure">Client options, after the test default of an endpoint nothing listens on.</param>
    /// <param name="export">
    /// Keeps the exporter running, for tests about what reaches the server. Otherwise it is removed,
    /// because it would drain the queue the test reads.
    /// </param>
    /// <param name="logs">Where the application's log goes, for tests about what the client says.</param>
    public static async Task<WebTestApp> StartAsync(
        Action<WebApplication>? map = null,
        Action<MomusOptions>? configure = null,
        bool export = false,
        ILoggerProvider? logs = null)
    {
        MomusRuntime.Reset();

        var connection = new Data.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<WidgetDb>(o => o.UseSqlite(connection));
        builder.AddMomus(o =>
        {
            o.Endpoint = "http://127.0.0.1:1";
            configure?.Invoke(o);
        });
        if (logs is not null) builder.Logging.AddProvider(logs);

        if (!export)
        {
            builder.Services.Remove(builder.Services.Single(s => s.ImplementationType == typeof(MomusExporter)));
        }

        var app = builder.Build();
        map?.Invoke(app);

        var web = new WebTestApp(app, connection);
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WidgetDb>().Database.EnsureCreatedAsync();
        }

        await app.StartAsync();
        web.BaseAddress = new Uri(app.Urls.First());
        web.Drain(); // the schema's DDL, recorded as ambient
        return web;
    }

    /// <summary>Whatever the client has finished and queued for the exporter so far.</summary>
    public IReadOnlyList<CompletedOperation> Drain()
    {
        var queue = _app.Services.GetRequiredService<OperationQueue>();
        var operations = new List<CompletedOperation>();
        while (queue.Reader.TryRead(out var operation)) operations.Add(operation);
        return operations;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
        MomusRuntime.Reset();
    }
}
