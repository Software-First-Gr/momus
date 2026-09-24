using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Momus.Core.Ingest;
using Momus.Server.Diagnostics;
using Momus.Server.Insights;
using Momus.Server.Store;

namespace Momus.Server;

/// <summary>
/// `momus serve`: the scheduler, the store and one page, hosted in the same binary as the CLI.
/// One process, one SQLite file, no second database to run.
/// </summary>
public static class MomusServer
{
    /// <summary>Version of the running server, shown in the header so a bug report can name it.</summary>
    public static string Version { get; } =
        typeof(MomusServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static async Task<int> RunAsync(ServerOptions options, CancellationToken ct = default)
    {
        var app = await BuildAsync(options, ct);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Momus");

        logger.LogInformation("Momus {Version} on http://localhost:{Port}, data in {Data}.",
            Version, options.Port, Path.GetFullPath(options.DataDirectory));
        if (options.IngestPort is { } ingestPort)
        {
            logger.LogInformation("Ingest only on port {IngestPort}, {Key}.", ingestPort,
                options.IngestKey is { Length: > 0 } ? "key required" : "NO KEY: anything that can reach it may report");
        }

        await app.RunAsync(ct);
        return 0;
    }

    /// <summary>
    /// The server, built and ready to start: what <see cref="RunAsync"/> runs, and what the tests
    /// start on ports of their own.
    /// </summary>
    public static async Task<WebApplication> BuildAsync(ServerOptions options, CancellationToken ct = default)
    {
        if (options.Problem() is { } problem) throw new ArgumentException(problem, nameof(options));

        // The UI, the insight text and the evidence packs are English, and their numbers should
        // read as English wherever the server runs. Without this, a machine with a Greek locale
        // renders "2.543 times a minute at 0,2 ms" inside an English sentence.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture =
            System.Globalization.CultureInfo.InvariantCulture;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.IngestPort is { } port
            ? [$"http://{options.ListenAddress}:{options.Port}", $"http://{options.ListenAddress}:{port}"]
            : [$"http://{options.ListenAddress}:{options.Port}"]);
        builder.Logging.AddSimpleConsole(c => { c.SingleLine = true; c.TimestampFormat = "HH:mm:ss "; });

        // The interesting log line is "scanned shop: 3 findings", not one entry per page refresh.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        // The key ring signs antiforgery tokens and nothing else, and it lives on the same volume
        // as the connection strings — which DESIGN.md already names as the trust boundary. The
        // "no XML encryptor configured" warning on Linux is not something a user can act on.
        builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

        var store = new MomusStore(options.DatabasePath);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IScanTargetFactory, ScanTargetFactory>();
        builder.Services.AddSingleton<ScanScheduler>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScanScheduler>());
        builder.Services.AddSingleton<IngestHandler>();
        builder.Services.AddSingleton<IngestGate>();
        builder.Services.AddSingleton<InsightEngine>();
        builder.Services.AddSingleton<DiagnosticsBuilder>();
        builder.Services.AddScoped<Pages.HeaderInfo>();
        builder.Services.AddHostedService<RetentionService>();
        builder.Services.AddHostedService<InsightService>();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(MomusServer).Assembly);

        // Antiforgery needs a key ring. Keeping it on the volume means the Settings form still
        // works after a restart, and it silences two warnings that suggest a problem there isn't.
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory, "keys")))
            .SetApplicationName("Momus");

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Momus");

        await store.InitializeAsync(ct);
        await SeedTargetsAsync(store, options, logger, ct);

        if (options.IngestPort is { } ingestPort) GuardIngestPort(app, ingestPort);

        app.MapRazorPages();
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = Version }));
        MapIngest(app);

        // The same report the page shows, for a terminal: curl it and paste the output.
        app.MapGet("/api/v1/diagnostics", async (DiagnosticsBuilder diagnostics, CancellationToken ct) =>
            Results.Ok(await diagnostics.BuildAsync(ct)));

        return app;
    }

    /// <summary>
    /// On the ingest port, the ingest endpoint and the health check are all there is. Judged by the
    /// port the connection arrived on, never by the Host header: the caller writes that one, so a
    /// request to the ingest port claiming to be for the main one must still find nothing.
    /// </summary>
    private static void GuardIngestPort(WebApplication app, int ingestPort) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var allowed =
                (HttpMethods.IsPost(request.Method) && request.Path == IngestPath) ||
                (HttpMethods.IsGet(request.Method) && request.Path == "/healthz");

            if (context.Connection.LocalPort == ingestPort && !allowed)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });

    private const string IngestPath = "/api/v1/ingest";

    /// <summary>
    /// The application half of the wire: one endpoint, one frozen contract (DESIGN.md), and no
    /// way for a client to ask the server to do anything but remember what it sent. With
    /// <c>MOMUS_INGEST_KEY</c> set, only a client that sends the key gets that far.
    /// </summary>
    /// <remarks>
    /// The batch is bound by hand rather than through model binding so that a client one version
    /// ahead gets "that is not a batch" and a 400, not a 500 from the framework.
    /// </remarks>
    private static void MapIngest(WebApplication app)
    {
        app.MapPost(IngestPath, async (HttpRequest request, IngestHandler handler, IngestGate gate, CancellationToken ct) =>
        {
            if (!gate.Admits(request.Headers[IngestJson.KeyHeader]))
            {
                gate.Refuse(request.HttpContext.Connection.RemoteIpAddress?.ToString());
                return Results.Unauthorized();
            }

            IngestBatch? batch;
            try
            {
                batch = await request.ReadFromJsonAsync<IngestBatch>(IngestJson.Options, ct);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (IngestHandler.Validate(batch) is { } error) return Results.BadRequest(new { error });

            var windowId = await handler.HandleAsync(batch!, ct);
            return Results.Accepted($"/api/v1/windows/{windowId}", new { window = windowId });
        });
    }

    /// <summary>
    /// Targets given on the command line or in the environment are written to the store on every
    /// start, so the compose file stays the source of truth while targets added in the UI survive.
    /// </summary>
    private static async Task SeedTargetsAsync(
        MomusStore store, ServerOptions options, ILogger logger, CancellationToken ct)
    {
        foreach (var spec in options.Targets)
        {
            await store.UpsertTargetAsync(new StoredTarget
            {
                Id = spec.Id,
                Name = spec.Name,
                Provider = spec.Provider,
                ConnectionString = spec.ConnectionString,
                Source = spec.Source,
            }, ct);

            logger.LogInformation("Target {Name} ({Provider}) from {Source}.", spec.Name, spec.Provider, spec.Source);
        }

        var targets = await store.TargetsAsync(ct);
        if (targets.Count == 0)
        {
            logger.LogInformation(
                "No targets yet. Add one at http://localhost:{Port}/settings, or set " +
                "MOMUS_TARGETS__0__PROVIDER and MOMUS_TARGETS__0__CONNECTIONSTRING.", options.Port);
        }
    }
}
