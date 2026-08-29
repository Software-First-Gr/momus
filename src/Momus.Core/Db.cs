using System.Data.Common;

namespace Momus.Core;

/// <summary>Minimal ADO.NET helpers so provider packages don't need an ORM.</summary>
public static class Db
{
    public static async Task<List<Dictionary<string, object?>>> QueryAsync(
        DbConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
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
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }

    public static long ToLong(object? value) => value is null or DBNull ? 0 : Convert.ToInt64(value);

    public static double ToDouble(object? value) => value is null or DBNull ? 0 : Convert.ToDouble(value);

    public static string ToStr(object? value) => value is null or DBNull ? "" : Convert.ToString(value) ?? "";
}
