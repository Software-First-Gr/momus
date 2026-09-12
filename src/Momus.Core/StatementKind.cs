namespace Momus.Core;

/// <summary>What kind of work a statement is, as far as its text can say.</summary>
public static class StatementKind
{
    /// <summary>
    /// Statements that manage a session or a transaction rather than read or write data. They sit
    /// in every statistics view by the thousand — Npgsql's pool sends <c>DISCARD ALL</c> each time
    /// a connection goes back — and there is nothing in any of them to tune.
    /// </summary>
    private static readonly string[] Bookkeeping =
    [
        "discard", "set", "reset", "show", "begin", "start transaction", "commit", "end",
        "rollback", "abort", "savepoint", "release", "deallocate", "listen", "unlisten",
    ];

    public static bool IsBookkeeping(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return false;
        var text = sql.TrimStart();

        foreach (var keyword in Bookkeeping)
        {
            if (!text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            if (text.Length > keyword.Length && IsWordChar(text[keyword.Length])) continue;

            // T-SQL's SET @total = (SELECT ...) is an assignment wrapped around a real query.
            if (keyword == "set" && text[keyword.Length..].TrimStart().StartsWith('@')) return false;

            return true;
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
