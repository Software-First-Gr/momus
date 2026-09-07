using System.Text.RegularExpressions;
using Momus.Core;
using Momus.Core.Insights;

namespace Momus.Server.Insights;

/// <summary>
/// Which statements appear to touch a table, and which tables a statement appears to touch.
/// </summary>
/// <remarks>
/// This is a whole-word match of the bare table name against normalized statement text, not a SQL
/// parser. It is deliberately the cheap version, and it is honest about what it costs: a table
/// called <c>orders</c> matches a statement that only mentions it in a comment or a column alias.
/// Both uses can afford that — one raises an N+1 from Medium to High, the other adds a line
/// naming the endpoints that touch a table — and neither invents a finding that was not there.
/// A real reference parser belongs with M3's ranking, where a wrong join would change an order.
/// </remarks>
public static class TableMatch
{
    /// <summary>Bare name of a <c>table:</c> subject: <c>public.order_lines</c> becomes <c>order_lines</c>.</summary>
    public static string BareName(string subjectKey)
    {
        var dot = subjectKey.LastIndexOf('.');
        return dot < 0 ? subjectKey : subjectKey[(dot + 1)..];
    }

    public static bool Mentions(string? statement, string tableSubjectKey)
    {
        if (string.IsNullOrEmpty(statement)) return false;

        var name = BareName(tableSubjectKey);
        if (name.Length == 0) return false;

        return Regex.IsMatch(statement, $@"(?<![\w.]){Regex.Escape(name)}\b",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>The statements the app reported that appear to touch this table.</summary>
    public static IEnumerable<QueryStatView> Touching(IEnumerable<QueryStatView> queries, string tableSubjectKey) =>
        queries.Where(q => Mentions(q.Sample, tableSubjectKey));

    /// <summary>
    /// The worst thing the database says about the tables a statement names — the "or on its
    /// table" half of the N+1 rule.
    /// </summary>
    public static FindingView? WorstTableFinding(IReadOnlyList<FindingView> findings, string? statement) =>
        findings
            .Where(f => f.Subjects.Any(s => s.Kind == Subject.Table && Mentions(statement, s.Key)))
            .OrderByDescending(f => (int)f.Severity)
            .FirstOrDefault();
}
