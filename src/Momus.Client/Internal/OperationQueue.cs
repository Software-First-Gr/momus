using System.Threading.Channels;

namespace Momus.Client.Internal;

/// <summary>
/// The handover between your request threads and the exporter. A request writes one finished
/// operation and moves on; if the exporter has fallen behind, the write is dropped and counted.
/// Instrumentation is allowed to lose data. It is not allowed to slow the application down.
/// </summary>
internal sealed class OperationQueue
{
    private readonly Channel<CompletedOperation> _channel;
    private long _dropped;

    public OperationQueue(MomusOptions options)
    {
        _channel = Channel.CreateBounded<CompletedOperation>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
    }

    public ChannelReader<CompletedOperation> Reader => _channel.Reader;

    /// <summary>Operations dropped because the queue was full, since the last time this was read.</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    public void Enqueue(CompletedOperation operation)
    {
        if (!_channel.Writer.TryWrite(operation)) Interlocked.Increment(ref _dropped);
    }

    public void Complete() => _channel.Writer.TryComplete();
}
