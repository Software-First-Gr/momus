namespace Momus.Core.Insights;

/// <summary>
/// What an insight is doing now. Mute and Fixed are user actions and arrive with their UI in M3;
/// the store already carries the column so an insight has a lifetime from the day it first fired.
/// </summary>
public static class InsightStatus
{
    public const string Open = "open";
    public const string Muted = "muted";
    public const string Fixed = "fixed";
}

/// <summary>
/// The sibling of <see cref="Finding"/>, and the reason Momus is not a nicer <c>pg_stat_statements</c>
/// viewer. A finding is what one side observed; an insight is what both sides mean together — this
/// endpoint runs that statement six times per request, and the database already dislikes the table.
/// </summary>
/// <remarks>
/// Identity is <see cref="Kind"/> plus the canonical form of <see cref="Subjects"/>, never the
/// title: titles carry live numbers and change every time they are recomputed, which would make
/// every insight look new on every evaluation.
/// </remarks>
public sealed record Insight
{
    /// <summary>Which rule produced it: <c>n_plus_one</c>, <c>hot_query_origin</c>, <c>db_finding</c>.</summary>
    public required string Kind { get; init; }

    public required Severity Severity { get; init; }

    /// <summary>One line, in plain language, naming the thing and the number that makes it true.</summary>
    public required string Title { get; init; }

    /// <summary>Why it is worth your afternoon, with both sides' numbers in it.</summary>
    public required string Detail { get; init; }

    public string? Recommendation { get; init; }

    /// <summary>What this is about. With <see cref="Kind"/> it is the identity across evaluations.</summary>
    public IReadOnlyList<Subject> Subjects { get; init; } = [];

    /// <summary>Structured supporting numbers, for the evidence pack (M3) and MCP (M4).</summary>
    public IReadOnlyDictionary<string, object?> Evidence { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// Extra identity for when subjects alone cannot tell two insights of one kind apart. Cache
    /// hit ratio and connection saturation are both about <c>server</c> and nothing else, so a
    /// rule that passes findings through has to say which check each came from.
    /// </summary>
    public string Discriminator { get; init; } = "";

    /// <summary>Kind plus sorted subjects: what makes this "the same insight" as last time.</summary>
    public string IdentityKey => Discriminator.Length == 0
        ? $"{Kind}|{Subject.Canonical(Subjects)}"
        : $"{Kind}|{Discriminator}|{Subject.Canonical(Subjects)}";
}
