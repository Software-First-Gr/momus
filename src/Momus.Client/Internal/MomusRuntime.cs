namespace Momus.Client.Internal;

/// <summary>
/// The one piece of static state in the client. <see cref="MomusOperation.Begin"/> is deliberately
/// a static API — code in a background job should be able to name what it is doing without having
/// a service injected for it — and this is how that call reaches the queue. It is set once, when
/// the container is built, and never changed after.
/// </summary>
internal static class MomusRuntime
{
    public static OperationQueue? Queue { get; private set; }

    public static void Use(OperationQueue queue) => Queue ??= queue;

    private static long _faults;

    /// <summary>
    /// Counts an exception the client caught in its own code inside an EF Core callback. Anything
    /// thrown there fails the application's query, so the interceptors catch everything, skip the
    /// statement and count it here; the exporter says so in the application's log.
    /// </summary>
    internal static void Fault() => Interlocked.Increment(ref _faults);

    internal static long TakeFaults() => Interlocked.Exchange(ref _faults, 0);

    /// <summary>For tests, which build more than one container in a process.</summary>
    internal static void Reset()
    {
        Queue = null;
        Interlocked.Exchange(ref _faults, 0);
    }
}
