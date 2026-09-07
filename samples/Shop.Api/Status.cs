using Microsoft.EntityFrameworkCore;

namespace Shop.Api;

/// <summary>Row counts for the control panel, read from the planner's estimates rather than counted.</summary>
public static class Status
{
    public sealed record TableRow(string Name, long Rows, long SeqScans, long IndexScans, long DeadRows);

    public static async Task<List<TableRow>> TablesAsync(ShopDb db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT relname, n_live_tup, seq_scan, COALESCE(idx_scan, 0) AS idx_scan, n_dead_tup
            FROM pg_stat_user_tables
            ORDER BY n_live_tup DESC
            """;

        var rows = new List<TableRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TableRow(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        }
        return rows;
    }
}
