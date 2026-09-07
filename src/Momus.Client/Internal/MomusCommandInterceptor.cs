using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Momus.Core;
using Momus.Core.Ingest;

namespace Momus.Client.Internal;

/// <summary>
/// The one place the client touches your data access. Every statement EF Core executes is
/// fingerprinted, timed and attributed to the operation that ran it. Nothing is read from the
/// command's parameters and nothing is read from the results — only the statement's shape.
/// </summary>
internal sealed class MomusCommandInterceptor(
    MomusOptions options,
    TargetRegistry targets,
    OperationQueue queue) : DbCommandInterceptor
{
    /// <summary>Fingerprints by command text. EF sends the same handful of strings over and over.</summary>
    private readonly ConcurrentDictionary<string, SqlFingerprint.Result> _fingerprints = new();

    private const int MaxFingerprints = 5_000;

    // ---- executing ---------------------------------------------------------------------
    //
    // These overrides record nothing. They exist to walk the stack while the application's own
    // frames are still on it: by the time the matching "executed" callback runs, every await
    // inside EF Core and the ADO.NET provider has resumed on a continuation, and the physical
    // stack holds nothing above the framework. A call site captured here is cached by
    // (fingerprint, operation), so the executed side finds it already answered.

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result)
    {
        Warm(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result,
        CancellationToken ct = default)
    {
        Warm(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData data, InterceptionResult<int> result)
    {
        Warm(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData data, InterceptionResult<int> result,
        CancellationToken ct = default)
    {
        Warm(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData data, InterceptionResult<object> result)
    {
        Warm(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData data, InterceptionResult<object> result,
        CancellationToken ct = default)
    {
        Warm(command);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Answers "where did this come from" once per statement per operation and caches it. Only
    /// statements inside a named operation are warmed: those are the ones whose call site the
    /// product promises, and an ambient statement has no operation to key the cache by yet.
    /// </summary>
    private void Warm(DbCommand command)
    {
        if (OperationContext.Current is not { } operation) return;
        if (command.CommandText is not { Length: > 0 } text) return;

        var fingerprint = Fingerprint(text);
        CallSites.For(fingerprint.Key, operation.Name, fingerprint.Tag);
    }

    // ---- executed ----------------------------------------------------------------------

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData data, DbDataReader result)
    {
        Record(command, data, rows: 0, failed: false);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
    {
        Record(command, data, rows: 0, failed: false);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData data, int result)
    {
        Record(command, data, rows: result, failed: false);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData data, int result, CancellationToken ct = default)
    {
        Record(command, data, rows: result, failed: false);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData data, object? result)
    {
        Record(command, data, rows: 1, failed: false);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData data, object? result, CancellationToken ct = default)
    {
        Record(command, data, rows: 1, failed: false);
        return ValueTask.FromResult(result);
    }

    // ---- failed ------------------------------------------------------------------------
    // A statement that threw is worth more than one that worked, so it is recorded too.

    public override void CommandFailed(DbCommand command, CommandErrorEventData data) =>
        Record(command, data, rows: 0, failed: true);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData data, CancellationToken ct = default)
    {
        Record(command, data, rows: 0, failed: true);
        return Task.CompletedTask;
    }

    // ---- rows --------------------------------------------------------------------------

    /// <summary>
    /// A reader's row count is only known once it has been read to the end, which happens after
    /// the execution was already recorded — so the rows are added to the entry afterwards.
    /// </summary>
    /// <remarks>
    /// <c>ReadCount</c> counts reads, not rows, and the difference is exactly one: the read that
    /// finds the end. Measured on EF Core 8, 9 and 10 across every shape that matters —
    /// <c>FirstOrDefaultAsync</c> hit and miss, <c>ToListAsync</c> of three and of none,
    /// <c>SingleOrDefaultAsync</c>, <c>CountAsync</c>, <c>AnyAsync</c> — it is rows + 1 in all of
    /// them, including the ones that look like they stop early. So one is subtracted, and
    /// <c>Momus.Client.Tests</c> pins those shapes so a change in EF's enumeration is a failing
    /// test rather than a row count that is quietly wrong everywhere.
    /// </remarks>
    public override InterceptionResult DataReaderDisposing(
        DbCommand command, DataReaderDisposingEventData data, InterceptionResult result)
    {
        var operation = OperationContext.Current;
        if (operation is not null && data.ReadCount > 0 && command.CommandText is { Length: > 0 } text)
        {
            var fingerprint = Fingerprint(text);
            var callSite = CallSites.For(fingerprint.Key, operation.Name, fingerprint.Tag);
            operation.AddRows(fingerprint.Key, callSite, data.ReadCount - 1);
        }

        return result;
    }

    // ---- the work ----------------------------------------------------------------------

    private void Record(DbCommand command, CommandEndEventData data, long rows, bool failed)
    {
        var text = command.CommandText;
        if (string.IsNullOrEmpty(text)) return;

        var fingerprint = Fingerprint(text);
        var durationMs = data.Duration.TotalMilliseconds;
        var operation = OperationContext.Current;

        if (operation is not null)
        {
            var callSite = CallSites.For(fingerprint.Key, operation.Name, fingerprint.Tag);
            operation.Record(fingerprint.Key, callSite, fingerprint.Text,
                durationMs, rows, failed, options.MaxKeysPerOperation);

            targets.Register(data.Context);
            return;
        }

        // Outside any operation: a hosted service, a startup migration, a background timer. The
        // timing still belongs in the query stats; it just is not an operation of its own.
        var loose = OperationContext.Begin(IngestOperation.Ambient, null, null);
        try
        {
            var callSite = CallSites.For(fingerprint.Key, loose.Name, fingerprint.Tag);
            loose.Record(fingerprint.Key, callSite, fingerprint.Text,
                durationMs, rows, failed, options.MaxKeysPerOperation);
            targets.Register(data.Context);
            queue.Enqueue(loose.Complete(countsAsOperation: false));
        }
        finally
        {
            OperationContext.End(loose);
        }
    }

    /// <summary>
    /// Fingerprints are cached by command text: EF sends the same handful of strings over and over,
    /// and hashing each one every time would be the client's largest cost by far.
    /// </summary>
    private SqlFingerprint.Result Fingerprint(string text)
    {
        if (_fingerprints.TryGetValue(text, out var cached)) return cached;

        var result = SqlFingerprint.Analyze(text);

        // Past the ceiling, keep working but stop growing: a pathological app that builds SQL by
        // string concatenation must not turn this cache into a leak.
        if (_fingerprints.Count < MaxFingerprints) _fingerprints.TryAdd(text, result);

        return result;
    }
}
