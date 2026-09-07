using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Momus.Client.Internal;

/// <summary>
/// How long an operation waited to get a connection. Normally nothing; when the pool is exhausted
/// it is the whole latency, and the database's own connection-saturation finding is the same
/// problem seen from the other side.
/// </summary>
/// <remarks>
/// Every acquisition is recorded, not just slow ones: a percentile needs the fast ones to mean
/// anything, and an unfiltered distribution is what the <c>pool_wait</c> rule reads.
/// </remarks>
internal sealed class MomusConnectionInterceptor(MomusOptions options) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData data) =>
        Record(data);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData data, CancellationToken ct = default)
    {
        Record(data);
        return Task.CompletedTask;
    }

    private void Record(ConnectionEndEventData data) =>
        OperationContext.Current?.RecordPoolWait(
            data.Duration.TotalMilliseconds, options.MaxKeysPerOperation);
}
