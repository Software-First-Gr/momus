using System.Collections.Concurrent;
using System.Diagnostics;

namespace Momus.Client.Internal;

/// <summary>
/// Where in your code a statement comes from. The stack is walked the first time a given statement
/// is seen in a given operation and then cached forever, so the cost is bounded by how many
/// distinct call sites the application has, not by how much traffic it serves.
/// </summary>
/// <remarks>
/// It only works because <see cref="MomusCommandInterceptor"/> calls it from the *executing*
/// callbacks. Measured on EF Core 10 against Npgsql: at <c>ReaderExecutedAsync</c> the physical
/// stack is <c>RelationalCommand.MoveNext</c> over <c>AsyncStateMachineBox</c> over
/// <c>ExecutionContext.RunInternal</c> and nothing else — every application frame is a logical
/// continuation by then, so the walk finds the framework and stops. Before execution the caller
/// is still there, about a dozen frames up.
/// </remarks>
internal static class CallSites
{
    /// <summary>An application with more distinct call sites than this has bigger problems.</summary>
    private const int MaxCached = 5_000;

    private static readonly ConcurrentDictionary<(string Key, string Operation), string?> Cache = new();

    /// <summary>
    /// Namespace roots that are never the answer: the framework, the providers, and Momus itself —
    /// including its own adapter packages, which are as much "not your code" as EF Core is.
    /// </summary>
    private static readonly string[] Ignored =
    [
        "Momus.Client", "Microsoft", "System", "Npgsql", "Oracle", "MySql", "Dapper",
    ];

    /// <summary>
    /// The call site for a statement in an operation. An EF Core <c>TagWith</c> /
    /// <c>TagWithCallSite</c> comment, passed as <c>tag</c>, always wins over a stack walk.
    /// </summary>
    public static string? For(string key, string operation, string? tag)
    {
        if (tag is { Length: > 0 }) return Shorten(tag);

        // Look before checking the ceiling: a call site already known stays known. Only *new*
        // ones are refused once the cache is full, so hitting the limit degrades the answer for
        // unseen statements rather than losing every answer already paid for.
        if (Cache.TryGetValue((key, operation), out var known)) return known;
        if (Cache.Count >= MaxCached) return null;

        return Cache.GetOrAdd((key, operation), static _ => Capture());
    }

    private static string? Capture()
    {
        var stack = new StackTrace(fNeedFileInfo: true);

        for (var i = 0; i < stack.FrameCount; i++)
        {
            var frame = stack.GetFrame(i);
            var method = frame?.GetMethod();
            var type = method?.DeclaringType;
            if (type is null) continue;

            // Async methods are compiled into a nested state machine; the interesting name is the
            // type that declares it, not "<OrderPageAsync>d__12".
            var declaring = type.DeclaringType ?? type;
            if (IsFramework(declaring.Namespace ?? "")) continue;

            var file = frame!.GetFileName();
            var line = frame.GetFileLineNumber();
            if (file is { Length: > 0 } && line > 0) return $"{Path.GetFileName(file)}:{line}";

            // No PDB next to the assembly: a type and method name still points at the right place.
            var name = method!.Name is "MoveNext" ? UnwrapAsyncName(type.Name) : method.Name;
            return $"{declaring.Name}.{name}";
        }

        return null;
    }

    /// <summary>
    /// Matches on namespace boundaries rather than raw prefixes. A plain <c>StartsWith("Npgsql")</c>
    /// also swallows an application namespace called <c>NpgsqlHelpers</c>, and the call site it
    /// then reports is a frame further up that belongs to nobody the user recognises.
    /// </summary>
    private static bool IsFramework(string ns) => Ignored.Any(root =>
        ns.Equals(root, StringComparison.Ordinal) ||
        ns.StartsWith(root + ".", StringComparison.Ordinal));

    /// <summary>"&lt;OrderPageAsync&gt;d__12" is the compiler's name for "OrderPageAsync".</summary>
    private static string UnwrapAsyncName(string stateMachineName)
    {
        var start = stateMachineName.IndexOf('<');
        var end = stateMachineName.IndexOf('>');
        return start >= 0 && end > start ? stateMachineName[(start + 1)..end] : stateMachineName;
    }

    private static string Shorten(string tag) =>
        tag.Length <= 200 ? tag : tag[..200];
}
