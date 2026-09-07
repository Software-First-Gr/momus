namespace Momus.Core;

/// <summary>
/// A typed handle on the thing a finding is about — a query, a table, an index, a session,
/// the database or the server. Subjects are the join key between the two halves of Momus:
/// the database side produces them from statistics views, the app side produces the same
/// <c>query:</c> keys from the SQL it executes, and the insight engine joins on them
/// instead of parsing free text out of <see cref="Finding.Evidence"/>.
/// </summary>
/// <param name="Kind">One of the constants on this type, e.g. <see cref="Query"/>.</param>
/// <param name="Key">Identity within the kind: a fingerprint, "schema.table", an index name, a pid.</param>
public sealed record Subject(string Kind, string Key)
{
    /// <summary>A normalized statement, keyed by <see cref="SqlFingerprint"/>.</summary>
    public const string Query = "query";
    public const string Table = "table";
    public const string Index = "index";
    /// <summary>A backend/session, keyed by pid (Postgres) or spid (SQL Server).</summary>
    public const string Session = "session";
    public const string Database = "database";
    /// <summary>The instance as a whole: the subject of server-wide findings such as cache hit ratio.</summary>
    public const string Server = "server";

    public static Subject ForQuery(string fingerprint) => new(Query, fingerprint);
    public static Subject ForTable(string schemaQualifiedName) => new(Table, schemaQualifiedName);
    public static Subject ForIndex(string name) => new(Index, name);
    public static Subject ForSession(long id) => new(Session, id.ToString());
    public static Subject ForDatabase(string name) => new(Database, name);

    /// <summary>The whole instance. Server-wide findings have no narrower subject.</summary>
    public static Subject ForServer() => new(Server, "");

    /// <summary>Round-trips with <see cref="Parse"/>; also what the UI and MCP show.</summary>
    public override string ToString() => Key.Length == 0 ? Kind : $"{Kind}:{Key}";

    public static Subject Parse(string text)
    {
        var i = text.IndexOf(':');
        return i < 0 ? new Subject(text, "") : new Subject(text[..i], text[(i + 1)..]);
    }

    /// <summary>
    /// Order-independent text for a set of subjects. With the check id it forms the identity of a
    /// finding across scans, so "the same finding" survives a restart and can carry a first-seen date.
    /// </summary>
    public static string Canonical(IEnumerable<Subject> subjects) =>
        string.Join('|', subjects.Select(s => s.ToString()).OrderBy(s => s, StringComparer.Ordinal));
}
