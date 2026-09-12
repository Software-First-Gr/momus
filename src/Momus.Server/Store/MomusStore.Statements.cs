using Microsoft.Data.Sqlite;

namespace Momus.Server.Store;

/// <summary>One statement's counters as the database reported them at one scan.</summary>
public sealed record StatementSample(string Fingerprint, long Calls, double TotalMs);

/// <summary>The counters from an earlier scan to subtract, and when they were read.</summary>
public sealed record StatementBaseline(DateTimeOffset CapturedAt, IReadOnlyDictionary<string, StatementSample> Samples);

public sealed partial class MomusStore
{
    /// <summary>
    /// A baseline is at most two windows old, so three hours is enough to always have one and few
    /// enough rows that five hundred statements a scan never add up to much.
    /// </summary>
    public static readonly TimeSpan StatementSampleRetention = TimeSpan.FromHours(3);

    /// <summary>
    /// The scan to subtract from one taken at <paramref name="at"/>: the newest one at least a
    /// <paramref name="window"/> old, or failing that the oldest one inside it — which is what a
    /// server that started twenty minutes ago has. Never older than two windows, because a
    /// difference over a day is not "the last hour" and would be labelled as if it were.
    /// </summary>
    public async Task<StatementBaseline?> StatementBaselineAsync(
        string targetId, DateTimeOffset at, TimeSpan window, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        long scanId;
        DateTimeOffset capturedAt;

        await using (var cmd = Command(connection, """
            SELECT s.id, s.started_at
            FROM scans s
            WHERE s.target_id = $target AND s.succeeded = 1
              AND s.started_at < $at AND s.started_at >= $oldest
              AND EXISTS (SELECT 1 FROM statement_samples ss WHERE ss.scan_id = s.id)
            ORDER BY CASE WHEN s.started_at <= $cutoff THEN 0 ELSE 1 END,
                     CASE WHEN s.started_at <= $cutoff THEN s.started_at END DESC,
                     s.started_at ASC
            LIMIT 1
            """, null,
            [("$target", targetId), ("$at", Write(at)), ("$cutoff", Write(at - window)),
             ("$oldest", Write(at - window - window))]))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            scanId = reader.GetInt64(0);
            capturedAt = Read(reader.GetString(1));
        }

        var samples = new Dictionary<string, StatementSample>(StringComparer.Ordinal);

        await using (var cmd = Command(connection,
            "SELECT fingerprint, calls, total_ms FROM statement_samples WHERE scan_id = $scan",
            null, [("$scan", scanId)]))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var sample = new StatementSample(reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2));
                samples[sample.Fingerprint] = sample;
            }
        }

        return new StatementBaseline(capturedAt, samples);
    }

    /// <summary>Drops the counters of scans older than <paramref name="olderThan"/>. The scans themselves stay.</summary>
    public async Task<int> TrimStatementSamplesAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var removed = 0;

        await WriteAsync(async connection =>
        {
            await using var cmd = Command(connection, """
                DELETE FROM statement_samples
                WHERE scan_id IN (SELECT id FROM scans WHERE started_at < $cutoff)
                """, null, [("$cutoff", Write(olderThan))]);
            removed = await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        return removed;
    }

    private static async Task SaveStatementSamplesAsync(SqliteConnection connection,
        System.Data.Common.DbTransaction tx, long scanId, string targetId,
        IReadOnlyList<StatementSample> samples, CancellationToken ct)
    {
        if (samples.Count == 0) return;

        // One prepared command for up to five hundred rows, rather than five hundred commands.
        await using var cmd = Command(connection, """
            INSERT OR REPLACE INTO statement_samples (scan_id, target_id, fingerprint, calls, total_ms)
            VALUES ($scan, $target, $fingerprint, $calls, $total)
            """, tx,
            [("$scan", scanId), ("$target", targetId), ("$fingerprint", ""), ("$calls", 0L), ("$total", 0.0)]);

        foreach (var sample in samples)
        {
            cmd.Parameters["$fingerprint"].Value = sample.Fingerprint;
            cmd.Parameters["$calls"].Value = sample.Calls;
            cmd.Parameters["$total"].Value = sample.TotalMs;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
