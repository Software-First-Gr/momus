using System.Collections.Concurrent;
using System.Data.Common;

namespace Momus.Core;

/// <summary>Minimal ADO.NET helpers so provider packages don't need an ORM.</summary>
public static class Db
{
    // Every statement Momus itself has sent in this process, by fingerprint. The statement views
    // record Momus's own reads beside the application's, and on the demo they were about forty of
    // the fifty rows. A comment marker would not do: Postgres ignores comments when it groups
    // statements, so a database scanned before the marker existed keeps showing the old text.
    private static readonly ConcurrentDictionary<string, byte> OwnSql = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> OwnKeys = new(StringComparer.Ordinal);

    /// <summary>Records a statement as Momus's own, so the top-queries checks can leave it out.</summary>
    public static void RememberOwn(string sql)
    {
        if (!OwnSql.TryAdd(sql, 0)) return;
        foreach (var statement in SqlFingerprint.Split(sql)) OwnKeys.TryAdd(statement.Key, 0);
    }

    /// <summary>True when a fingerprint belongs to a statement Momus itself has sent.</summary>
    public static bool IsOwn(string fingerprint) => OwnKeys.ContainsKey(fingerprint);

    public static async Task<List<Dictionary<string, object?>>> QueryAsync(
        DbConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        RememberOwn(sql);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    public static async Task<object?> ScalarAsync(
        DbConnection connection, string sql, CancellationToken ct)
    {
        RememberOwn(sql);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }

    public static long ToLong(object? value) => value is null or DBNull ? 0 : Convert.ToInt64(value);

    public static double ToDouble(object? value) => value is null or DBNull ? 0 : Convert.ToDouble(value);

    public static string ToStr(object? value) => value is null or DBNull ? "" : Convert.ToString(value) ?? "";
}
