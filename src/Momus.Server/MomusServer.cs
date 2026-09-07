using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
        builder.Logging.AddSimpleConsole(c => { c.SingleLine = true; c.TimestampFormat = "HH:mm:ss "; });

        // The interesting log line is "scanned shop: 3 findings", not one entry per page refresh.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        var store = new MomusStore(options.DatabasePath);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IScanTargetFactory, ScanTargetFactory>();
        builder.Services.AddSingleton<ScanScheduler>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScanScheduler>());
        builder.Services.AddRazorPages().AddApplicationPart(typeof(MomusServer).Assembly);

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Momus");

        await store.InitializeAsync(ct);
        await SeedTargetsAsync(store, options, logger, ct);

        app.MapRazorPages();
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = Version }));

        logger.LogInformation("Momus {Version} on http://localhost:{Port}, data in {Data}.",
            Version, options.Port, Path.GetFullPath(options.DataDirectory));

        await app.RunAsync(ct);
        return 0;
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
