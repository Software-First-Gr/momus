using Microsoft.Extensions.Logging;
using Momus.Core.Ingest;
using Momus.Server.Store;

namespace Momus.Server;

/// <summary>
/// What happens to a posted window: it is stored, and any database the app mentioned that the
/// server does not already know becomes a target to scan. That second part is the point — the
/// developer configures the database once, in the application, and the two halves line up.
/// </summary>
public sealed class IngestHandler(
    MomusStore store,
    ScanScheduler scheduler,
    ILogger<IngestHandler> logger)
{
    private bool _sawFirstWindow;

    /// <summary>Rejects a batch that could not be joined to anything, and says why.</summary>
    public static string? Validate(IngestBatch? batch) => batch switch
    {
        null => "Body is not a Momus ingest batch.",
        { App.Name: null or "" } => "app.name is required.",
        _ when batch.Window.To < batch.Window.From => "window.to is before window.from.",
        _ => null,
    };

    public async Task<long> HandleAsync(IngestBatch batch, CancellationToken ct)
    {
        var windowId = await store.SaveIngestBatchAsync(batch, ct);
        await RegisterTargetsAsync(batch, ct);

        if (!_sawFirstWindow)
        {
            _sawFirstWindow = true;
            logger.LogInformation(
                "{App} {Version} is reporting ({Queries} statement(s), {Operations} operation(s) in the first window).",
                batch.App.Name, batch.App.Version ?? "(no version)", batch.Queries.Count, batch.Operations.Count);
        }

        return windowId;
    }

    /// <summary>
    /// Adds a database the app talks about, if it shared a connection string and the server does
    /// not already have that target. An existing target is never overwritten: a connection string
    /// set in the compose file or in Settings outranks one learned from an application.
    /// </summary>
    private async Task RegisterTargetsAsync(IngestBatch batch, CancellationToken ct)
    {
        if (batch.Targets.Count == 0) return;

        var known = (await store.TargetsAsync(ct)).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var target in batch.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.ConnectionString) || known.Contains(target.Id)) continue;

            await store.UpsertTargetAsync(new StoredTarget
            {
                Id = target.Id,
                Name = target.Database is { Length: > 0 } database ? database : target.Id,
                Provider = target.Provider,
                ConnectionString = target.ConnectionString,
                Source = "app",
            }, ct);

            // Scanned within a couple of seconds rather than at the end of the interval, because
            // this is someone's first run and an empty page is what they are looking at.
            scheduler.RequestScan(target.Id);

            logger.LogInformation(
                "{App} says it uses {Provider} database {Target}; scanning it too.",
                batch.App.Name, target.Provider, target.Id);
        }
    }
}
