using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Momus.Client.Internal;

/// <summary>
/// How long a transaction was open, and how much of that was actually database work. The gap
/// between the two is the interesting number: a transaction held open across an HTTP call or a
/// file write makes every other session queue behind its locks, and nothing in the application's
/// own metrics says so.
/// </summary>
/// <remarks>
/// The open time is measured here rather than read from EF's <c>Duration</c>, which means one
/// thing on a start event and another on a commit event. Timing it directly is unambiguous, and
/// <c>Momus.Client.Tests</c> pins it against a transaction held open for a known interval.
/// </remarks>
internal sealed class MomusTransactionInterceptor(MomusOptions options) : DbTransactionInterceptor
{
    /// <summary>An application with more transactions open at once than this is not one Momus can help.</summary>
    private const int MaxInFlight = 1_000;

    private readonly ConcurrentDictionary<Guid, Open> _open = new();

    public override DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData data, DbTransaction result)
    {
        Track(data.TransactionId);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData data, DbTransaction result,
        CancellationToken ct = default)
    {
        Track(data.TransactionId);
        return ValueTask.FromResult(result);
    }

    /// <summary>A transaction the application started itself and handed to EF still counts.</summary>
    public override DbTransaction TransactionUsed(
        DbConnection connection, TransactionEventData data, DbTransaction result)
    {
        Track(data.TransactionId);
        return result;
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData data) =>
        Close(data.TransactionId);

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
    {
        Close(data.TransactionId);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData data) =>
        Close(data.TransactionId);

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
    {
        Close(data.TransactionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The backstop. A transaction that neither commits nor rolls back — a connection dropped
    /// mid-flight — ends here; one abandoned without any event at all simply occupies a slot until
    /// the in-flight ceiling, which is why there is a ceiling.
    /// </summary>
    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData data) =>
        Close(data.TransactionId);

    private void Track(Guid id)
    {
        if (OperationContext.Current is not { } operation) return;
        if (_open.Count >= MaxInFlight) return;

        _open.TryAdd(id, new Open(
            Stopwatch.GetTimestamp(),
            operation.DbMsSoFar,
            CallSites.For($"tx:{id}", operation.Name, null)));
    }

    private void Close(Guid id)
    {
        if (!_open.TryRemove(id, out var open)) return;
        if (OperationContext.Current is not { } operation) return;

        operation.RecordTransaction(
            Stopwatch.GetElapsedTime(open.StartedAt).TotalMilliseconds,
            operation.DbMsSoFar - open.DbMsAtStart,
            open.CallSite,
            options.MaxKeysPerOperation);
    }

    /// <param name="DbMsAtStart">
    /// Database time the operation had already spent, so the time inside this transaction is a
    /// subtraction rather than a second set of counters.
    /// </param>
    private readonly record struct Open(long StartedAt, double DbMsAtStart, string? CallSite);
}
