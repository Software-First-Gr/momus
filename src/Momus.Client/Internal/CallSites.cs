using System.Collections.Concurrent;
using System.Diagnostics;

namespace Momus.Client.Internal;

/// <summary>
/// Where in your code a statement comes from. The stack is walked the first time a given statement
/// is seen in a given operation and then cached forever, so the cost is bounded by how many
/// distinct call sites the application has, not by how much traffic it serves.
/// </summary>
internal static class CallSites
{
    /// <summary>An application with more distinct call sites than this has bigger problems.</summary>
    private const int MaxCached = 5_000;

    private static readonly ConcurrentDictionary<(string Key, string Operation), string?> Cache = new();

    /// <summary>Namespaces that are never the answer: the framework, and Momus itself.</summary>
    private static readonly string[] Ignored =
    [
        "Momus.Client", "Microsoft.", "System.", "Npgsql", "Oracle.", "MySql.", "Dapper",
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
            var ns = declaring.Namespace ?? "";
            if (Ignored.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal))) continue;

            var file = frame!.GetFileName();
            var line = frame.GetFileLineNumber();
            if (file is { Length: > 0 } && line > 0) return $"{Path.GetFileName(file)}:{line}";

            // No PDB next to the assembly: a type and method name still points at the right place.
            var name = method!.Name is "MoveNext" ? UnwrapAsyncName(type.Name) : method.Name;
            return $"{declaring.Name}.{name}";
        }

        return null;
    }

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
