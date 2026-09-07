using Momus.Client.Internal;

namespace Momus.Client;

/// <summary>
/// Names a unit of work that is not an HTTP request — an import job, a message handler, a timer.
/// Requests name themselves; anything else would otherwise be reported as ambient database work
/// with no operation to blame.
/// </summary>
/// <example>
/// <code>
/// using (MomusOperation.Begin("NightlyImport"))
/// {
///     await importer.RunAsync(ct);
/// }
/// </code>
/// </example>
public static class MomusOperation
{
    /// <summary>Opens an operation. Everything the database does until it is disposed belongs to it.</summary>
    public static IDisposable Begin(string name) => new Scope(name);

    /// <summary>The operation currently in scope, if any. Mostly useful in tests and diagnostics.</summary>
    public static string? Current => OperationContext.Current?.Name;

    private sealed class Scope : IDisposable
    {
        private readonly OperationContext _context;
        private bool _disposed;

        public Scope(string name) => _context = OperationContext.Begin("background", null, name);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            OperationContext.End(_context);
            if (_context.QueryCount > 0)
            {
                MomusRuntime.Queue?.Enqueue(_context.Complete(countsAsOperation: true));
            }
        }
    }
}
