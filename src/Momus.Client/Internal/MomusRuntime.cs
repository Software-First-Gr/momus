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

    /// <summary>For tests, which build more than one container in a process.</summary>
    internal static void Reset() => Queue = null;
}
