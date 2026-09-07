using System.Security.Cryptography;
using System.Text;

namespace Momus.Core;

/// <summary>
/// Turns SQL into a stable key that identifies the same statement no matter who is looking at it.
/// This is the join between Momus's two halves: the app sees <c>WHERE "Id" = @p0</c>, Postgres
/// stores <c>WHERE "Id" = $1</c> and SQL Server hands back a statement slice out of
/// <c>dm_exec_sql_text</c>. All three normalize to one text and therefore to one key.
/// </summary>
/// <remarks>
/// Normalization: comments are stripped (the first one is kept as a possible call site, because
/// EF Core's <c>TagWith</c> puts it there), literals and parameter markers become <c>?</c>,
/// <c>IN</c> lists and repeated <c>VALUES</c> groups collapse to one group, keywords are
/// lowercased while identifiers keep their case, and whitespace is rendered canonically.
/// </remarks>
public static class SqlFingerprint
{
    /// <summary>Result of normalizing one statement.</summary>
    /// <param name="Text">Canonical text: what the key is computed from and what the UI shows.</param>
    /// <param name="Key">First 16 hex chars of SHA-256 over <paramref name="Text"/>.</param>
    /// <param name="Tag">First comment in the statement, if any — an EF <c>TagWith</c> call site.</param>
    public readonly record struct Result(string Text, string Key, string? Tag);

    /// <summary>The 16-hex-char key for a statement. Empty input gets the empty-string key.</summary>
    public static string Compute(string? sql) => Analyze(sql).Key;

    /// <summary>The canonical text of a statement, without hashing it.</summary>
    public static string Normalize(string? sql) => Analyze(sql).Text;

    /// <summary>Normalized text, key and leading comment in one pass.</summary>
    public static Result Analyze(string? sql)
    {
        var (tokens, tag) = Tokenize(sql ?? "");
        CollapseGroups(tokens);
        var text = Render(tokens);
        return new Result(text, Hash(text), tag);
    }

    private static string Hash(string normalized)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 8));
    }

    // ---- 1. tokenize ------------------------------------------------------------------
    // A scanner, not a chain of regexes: only a scanner knows that a '?' inside a string
    // literal is not a parameter and that "$1" is a parameter while "$tag$...$tag$" is not.

    private enum Kind { Word, Keyword, Placeholder, Punct }

    private readonly record struct Token(Kind Kind, string Text);

    private static (List<Token> Tokens, string? Tag) Tokenize(string sql)
    {
        var tokens = new List<Token>(64);
        string? tag = null;
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            // -- line comment
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOfAny(['\n', '\r'], i);
                if (end < 0) end = sql.Length;
                tag ??= Trimmed(sql[(i + 2)..end]);
                i = end;
                continue;
            }

            // /* block comment */
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var body = end < 0 ? sql[(i + 2)..] : sql[(i + 2)..end];
                tag ??= Trimmed(body);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }

            // 'string literal', with '' as the escape
            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(new Token(Kind.Placeholder, "?"));
                continue;
            }

            // "quoted identifier" (Postgres, standard) — keeps its case, it is a name
            if (c == '"')
            {
                var end = sql.IndexOf('"', i + 1);
                if (end < 0) end = sql.Length - 1;
                tokens.Add(new Token(Kind.Word, sql[i..(end + 1)]));
                i = end + 1;
                continue;
            }

            // [quoted identifier] (SQL Server)
            if (c == '[')
            {
                var end = sql.IndexOf(']', i + 1);
                if (end < 0) end = sql.Length - 1;
                tokens.Add(new Token(Kind.Word, sql[i..(end + 1)]));
                i = end + 1;
                continue;
            }

            // N'unicode literal' / X'hex' / B'bits'
            if ((c is 'N' or 'n' or 'X' or 'x' or 'B' or 'b') && i + 1 < sql.Length && sql[i + 1] == '\'')
            {
                i++;
                continue; // the literal itself is handled by the '\'' branch on the next pass
            }

            // $1 parameter, or $tag$ dollar-quoted body $tag$ (function bodies)
            if (c == '$')
            {
                if (i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]))
                {
                    i += 2;
                    while (i < sql.Length && char.IsAsciiDigit(sql[i])) i++;
                    tokens.Add(new Token(Kind.Placeholder, "?"));
                    continue;
                }

                var close = sql.IndexOf('$', i + 1);
                if (close > 0)
                {
                    var delimiter = sql[i..(close + 1)];
                    var bodyEnd = sql.IndexOf(delimiter, close + 1, StringComparison.Ordinal);
                    if (bodyEnd > 0)
                    {
                        i = bodyEnd + delimiter.Length;
                        tokens.Add(new Token(Kind.Placeholder, "?"));
                        continue;
                    }
                }
                i++;
                continue;
            }

            // @p0 / @__id_0 (SQL Server, EF) and :p1 (Oracle-style, some providers)
            if ((c == '@' || c == ':') && i + 1 < sql.Length && (char.IsLetterOrDigit(sql[i + 1]) || sql[i + 1] == '_'))
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                tokens.Add(new Token(Kind.Placeholder, "?"));
                continue;
            }

            // bare ? marker (ODBC style)
            if (c == '?')
            {
                tokens.Add(new Token(Kind.Placeholder, "?"));
                i++;
                continue;
            }

            // number: 12, 1.5, 1e-3, 0x1F. A leading sign stays an operator, so "x - 1" and
            // "x - 2" collapse the same way whether or not the sign is glued to the digits.
            if (char.IsAsciiDigit(c))
            {
                i++;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '.' ||
                                          ((sql[i] is '+' or '-') && sql[i - 1] is 'e' or 'E'))) i++;
                tokens.Add(new Token(Kind.Placeholder, "?"));
                continue;
            }

            // identifier or keyword
            if (char.IsLetter(c) || c == '_' || c == '#')
            {
                var start = i;
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '#' or '$')) i++;
                var word = sql[start..i];
                tokens.Add(Keywords.Contains(word)
                    ? new Token(Kind.Keyword, word.ToLowerInvariant())
                    : new Token(Kind.Word, word));
                continue;
            }

            // everything else is punctuation or an operator, one char at a time
            tokens.Add(new Token(Kind.Punct, c.ToString()));
            i++;
        }

        return (tokens, tag);
    }

    private static string? Trimmed(string text)
    {
        var t = text.Trim();
        return t.Length == 0 ? null : t;
    }

    // ---- 2. collapse repeated groups --------------------------------------------------
    // "IN (?, ?, ?)" and "IN (?)" are the same query with a different list length, and a batch
    // insert differs from a single insert only in how many VALUES groups follow. Collapsing both
    // keeps one key per statement instead of one per list length.

    private static void CollapseGroups(List<Token> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is { Kind: Kind.Keyword, Text: "in" } &&
                i + 1 < tokens.Count && tokens[i + 1].Text == "(")
            {
                var close = MatchParen(tokens, i + 1);
                if (close > 0 && AllPlaceholders(tokens, i + 2, close))
                {
                    // in ( ? , ? , ? ) -> in ( ? )
                    tokens.RemoveRange(i + 2, close - (i + 2));
                    tokens.Insert(i + 2, new Token(Kind.Placeholder, "?"));
                }
                continue;
            }

            if (tokens[i] is { Kind: Kind.Keyword, Text: "values" })
            {
                var first = i + 1;
                if (first >= tokens.Count || tokens[first].Text != "(") continue;
                var close = MatchParen(tokens, first);
                if (close < 0) continue;

                // drop every ", (...)" group that follows the first one
                while (close + 2 < tokens.Count && tokens[close + 1].Text == "," && tokens[close + 2].Text == "(")
                {
                    var nextClose = MatchParen(tokens, close + 2);
                    if (nextClose < 0) break;
                    tokens.RemoveRange(close + 1, nextClose - close);
                }
            }
        }
    }

    private static int MatchParen(List<Token> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Punct) continue;
            if (tokens[i].Text == "(") depth++;
            else if (tokens[i].Text == ")" && --depth == 0) return i;
        }
        return -1;
    }

    private static bool AllPlaceholders(List<Token> tokens, int from, int toExclusive)
    {
        for (var i = from; i < toExclusive; i++)
        {
            if (tokens[i].Kind == Kind.Placeholder) continue;
            if (tokens[i] is { Kind: Kind.Punct, Text: "," }) continue;
            return false;
        }
        return from < toExclusive;
    }

    // ---- 3. render --------------------------------------------------------------------
    // Canonical spacing, so two texts that differ only in formatting hash the same.

    private static string Render(List<Token> tokens)
    {
        var sb = new StringBuilder(128);
        for (var i = 0; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;
            if (sb.Length > 0 && NeedsSpace(tokens[i - 1].Text, text)) sb.Append(' ');
            sb.Append(text);
        }
        return sb.ToString();
    }

    private static bool NeedsSpace(string previous, string next)
    {
        if (next is "," or ")" or ";" or "." or "::") return false;
        if (previous is "(" or "." or "::") return false;
        return true;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "all", "alter", "and", "any", "as", "asc", "begin", "between", "by", "case", "cast",
        "coalesce", "commit", "create", "cross", "current_date", "current_timestamp", "declare",
        "default", "delete", "desc", "distinct", "do", "drop", "else", "end", "escape", "except",
        "exec", "execute", "exists", "false", "fetch", "first", "for", "from", "full", "group",
        "having", "if", "ilike", "in", "index", "inner", "insert", "intersect", "into", "is",
        "join", "key", "left", "like", "limit", "next", "not", "null", "nulls", "offset", "on",
        "only", "or", "order", "outer", "over", "partition", "primary", "returning", "right",
        "rollback", "rows", "select", "set", "some", "table", "then", "top", "transaction", "true",
        "union", "unique", "update", "using", "values", "view", "when", "where", "with",
    };
}
