using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Momus.Core.Ingest;

namespace Momus.Client.Internal;

/// <summary>
/// Folds finished operations into a window and posts it every few seconds. Everything expensive
/// happens here, on one background thread, well away from any request: the requests only ever
/// hand over a finished object and carry on.
/// </summary>
internal sealed class MomusExporter(
    MomusOptions options,
    OperationQueue queue,
    TargetRegistry targets,
    IHttpClientFactory clients,
    IHostEnvironment environment,
    ILogger<MomusExporter> logger) : BackgroundService
{
    /// <summary>Name of the HttpClient registered by <c>AddMomus</c>.</summary>
    public const string HttpClientName = "momus";

    // A plain object rather than System.Threading.Lock: this assembly still targets net8.0.
    private readonly object _gate = new();
    private Window _window = new(options.MaxKeysPerWindow);
    private bool _serverIsDown;
    private bool _saidHello;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation(
            "Momus is watching {App} and reporting to {Endpoint} every {Seconds:N0}s.",
            App.Name, options.Endpoint, options.FlushInterval.TotalSeconds);

        var folding = FoldAsync(ct);

        try
        {
            using var timer = new PeriodicTimer(options.FlushInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down: send whatever the last few seconds produced before going quiet.
        }

        queue.Complete();
        await folding;
        await FlushAsync(CancellationToken.None);
    }

    /// <summary>Drains the queue into the current window. One reader, so the window needs no locking of its own.</summary>
    private async Task FoldAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var operation in queue.Reader.ReadAllAsync(ct))
            {
                lock (_gate) _window.Add(operation);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Momus stopped folding operations.");
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        Window window;
        lock (_gate)
        {
            if (_window.IsEmpty && _saidHello) return;
            window = _window;
            _window = new Window(options.MaxKeysPerWindow);
        }

        var dropped = queue.TakeDropped();
        if (dropped > 0)
        {
            logger.LogWarning("Momus dropped {Count} operations: the exporter could not keep up.", dropped);
        }

        var known = targets.All();
        var batch = window.ToBatch(App, known, known.Count == 1 ? known[0].Id : null) with
        {
            Client = new IngestClient(ClientVersion, dropped),
        };

        try
        {
            var client = clients.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync("api/v1/ingest", batch, IngestJson.Options, ct);
            response.EnsureSuccessStatusCode();

            if (_serverIsDown)
            {
                logger.LogInformation("Momus server is back at {Endpoint}.", options.Endpoint);
                _serverIsDown = false;
            }
            _saidHello = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One line, once. A dead Momus server must not fill an application's log.
            if (!_serverIsDown)
            {
                logger.LogWarning(
                    "Momus server at {Endpoint} is not answering ({Message}); dropping windows until it does.",
                    options.Endpoint, ex.Message);
                _serverIsDown = true;
            }
        }
    }

    /// <summary>
    /// Who is reporting. The version matters most: a new one appearing is what makes "this got
    /// slower with the last deploy" answerable at all.
    /// </summary>
    private IngestApp App => _app ??= new IngestApp(
        options.AppName is { Length: > 0 } name ? name : Assembly.GetEntryAssembly()?.GetName().Name ?? "app",
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        $"{System.Environment.MachineName}:{System.Environment.ProcessId}",
        options.Environment is { Length: > 0 } env ? env : environment.EnvironmentName);

    private IngestApp? _app;

    /// <summary>
    /// The client's own version, so a surprising number on the page can be traced to a build.
    /// </summary>
    private static readonly string? ClientVersion =
        typeof(MomusExporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0];
}
