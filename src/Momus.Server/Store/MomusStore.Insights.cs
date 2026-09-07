using System.Text.Json;
using Momus.Core;
using Momus.Core.Insights;

namespace Momus.Server.Store;

/// <summary>
/// Where insights live between evaluations. One row per insight per target, for as long as it
/// keeps being true — so "you have an N+1 on that endpoint" can be followed by "since Tuesday".
/// </summary>
public sealed partial class MomusStore
{
    /// <summary>
    /// Writes the result of one evaluation. Insights already known keep their first-seen date and
    /// get a new last-seen; ones that stopped firing are left alone rather than deleted, because
    /// an insight that came back is worth telling apart from one that is new.
    /// </summary>
    public async Task SaveInsightsAsync(
        string targetId, IReadOnlyList<Insight> insights, DateTimeOffset at, CancellationToken ct = default)
    {
        if (insights.Count == 0) return;

        await WriteAsync(async connection =>
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            var now = Write(at);

            foreach (var insight in insights)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO insights (target_id, kind, identity_key, severity, title, detail,
                                          recommendation, evidence, subjects, first_seen, last_seen,
                                          seen_count, status, score)
                    VALUES ($target, $kind, $identity, $severity, $title, $detail, $recommendation,
                            $evidence, $subjects, $now, $now, 1, 'open', $score)
                    ON CONFLICT(target_id, identity_key) DO UPDATE SET
                        last_seen = excluded.last_seen,
                        seen_count = insights.seen_count + 1,
                        severity = excluded.severity,
                        title = excluded.title,
                        detail = excluded.detail,
                        recommendation = excluded.recommendation,
                        evidence = excluded.evidence,
                        subjects = excluded.subjects,
                        score = excluded.score,
                        -- Marked fixed and back again: worth saying so, because it means the fix
                        -- did not hold rather than that nobody has looked at it yet.
                        reopened = CASE WHEN insights.status = 'fixed' THEN 1 ELSE insights.reopened END,
                        status = CASE WHEN insights.status = 'fixed' THEN 'open'
                                      ELSE insights.status END
                    """, ct, tx,
                    ("$target", targetId), ("$kind", insight.Kind), ("$identity", insight.IdentityKey),
                    ("$severity", (int)insight.Severity), ("$title", insight.Title),
                    ("$detail", insight.Detail), ("$recommendation", insight.Recommendation),
                    ("$evidence", JsonSerializer.Serialize(insight.Evidence, EvidenceJson)),
                    ("$subjects", string.Join(' ', insight.Subjects.Select(s => s.ToString()))),
                    ("$now", now), ("$score", insight.Score));
            }

            await tx.CommitAsync(ct);
        }, ct);
    }

    /// <summary>
    /// The insights from the newest evaluation, in the order they are worth fixing. "Newest" is
    /// the largest last-seen for this target: an insight that stopped firing keeps its history but
    /// leaves the page, the same way a finding that stopped appearing leaves the findings list.
    /// </summary>
    /// <param name="includeMuted">
    /// The page that offers Unmute has to be able to see them; every other caller wants them out
    /// of the way, which is what muting them was for.
    /// </param>
    public async Task<IReadOnlyList<StoredInsight>> CurrentInsightsAsync(
        string targetId, bool includeMuted = false, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT kind, identity_key, severity, title, detail, recommendation, evidence, subjects,
                   first_seen, last_seen, seen_count, status, score, reopened
            FROM insights
            WHERE target_id = $target
              AND last_seen = (SELECT MAX(last_seen) FROM insights WHERE target_id = $target)
              AND ($muted = 1 OR status <> 'muted')
            ORDER BY score DESC, severity DESC, first_seen
            """;
        cmd.Parameters.AddWithValue("$target", targetId);
        cmd.Parameters.AddWithValue("$muted", includeMuted ? 1 : 0);

        var insights = new List<StoredInsight>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            insights.Add(new StoredInsight
            {
                Kind = reader.GetString(0),
                IdentityKey = reader.GetString(1),
                Severity = (Severity)reader.GetInt32(2),
                Title = reader.GetString(3),
                Detail = reader.GetString(4),
                Recommendation = reader.IsDBNull(5) ? null : reader.GetString(5),
                EvidenceJson = reader.GetString(6),
                Subjects = reader.GetString(7).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Subject.Parse).ToList(),
                FirstSeen = Read(reader.GetString(8)),
                LastSeen = Read(reader.GetString(9)),
                SeenCount = reader.GetInt32(10),
                Status = reader.GetString(11),
                Score = reader.GetDouble(12),
                Reopened = reader.GetInt64(13) == 1,
            });
        }
        return insights;
    }
}

/// <summary>An insight as the store holds it: what the rule said, plus how long it has been true.</summary>
public sealed record StoredInsight
{
    public required string Kind { get; init; }
    public required string IdentityKey { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public string? Recommendation { get; init; }
    public required string EvidenceJson { get; init; }
    public IReadOnlyList<Subject> Subjects { get; init; } = [];
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public int SeenCount { get; init; }
    public string Status { get; init; } = InsightStatus.Open;

    /// <summary>Severity × traffic share × recency, as of the last evaluation.</summary>
    public double Score { get; init; }

    /// <summary>Marked fixed at some point, and fired again since. The fix did not hold.</summary>
    public bool Reopened { get; init; }

    /// <summary>True the first time it fired — the difference between "new" and "still there".</summary>
    public bool IsNew => SeenCount <= 1;
}

public sealed partial class MomusStore
{
    /// <summary>
    /// Mute or mark fixed. A muted insight stops being shown and keeps accumulating history; a
    /// fixed one that fires again reopens itself, which is what the upsert already does.
    /// </summary>
    public async Task SetInsightStatusAsync(
        string targetId, string identityKey, string status, CancellationToken ct = default)
    {
        if (status is not (InsightStatus.Open or InsightStatus.Muted or InsightStatus.Fixed))
        {
            throw new ArgumentException($"Unknown insight status '{status}'.", nameof(status));
        }

        await WriteAsync(async connection => await ExecuteAsync(connection, """
            UPDATE insights SET status = $status
            WHERE target_id = $target AND identity_key = $identity
            """, ct, null,
            ("$target", targetId), ("$identity", identityKey), ("$status", status)), ct);
    }

    /// <summary>When each insight was first seen, so ranking can favour what started today.</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> InsightFirstSeenAsync(
        string targetId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT identity_key, first_seen FROM insights WHERE target_id = $target";
        cmd.Parameters.AddWithValue("$target", targetId);

        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) seen[reader.GetString(0)] = Read(reader.GetString(1));
        return seen;
    }
}
