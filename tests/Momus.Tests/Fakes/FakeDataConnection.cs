using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Momus.Tests.Fakes;

/// <summary>
/// A DbConnection that answers queries from canned rows instead of a server. Responses are matched
/// by a substring of the SQL, so a test says "when a check reads pg_stat_user_tables, hand it these
/// rows" without repeating the query. Anything unmatched returns no rows, which is a check's healthy
/// path — so a test only has to script the cases it cares about.
/// </summary>
public sealed class FakeDataConnection : DbConnection
{
    private readonly List<(string Match, List<Dictionary<string, object?>> Rows)> _responses = [];
    private ConnectionState _state = ConnectionState.Closed;

    /// <summary>Every command text this connection was asked to run, in order.</summary>
    public List<string> ExecutedSql { get; } = [];

    /// <summary>Script one response. <paramref name="match"/> is matched case-insensitively.</summary>
    public FakeDataConnection When(string match, params Dictionary<string, object?>[] rows)
    {
        _responses.Add((match, rows.ToList()));
        return this;
    }

    /// <summary>Convenience for single-column scalar answers, e.g. an extension-presence probe.</summary>
    public FakeDataConnection WhenScalar(string match, string column, object? value) =>
        When(match, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [column] = value });

    internal List<Dictionary<string, object?>> Rows(string sql)
    {
        ExecutedSql.Add(sql);
        foreach (var (match, rows) in _responses)
        {
            if (sql.Contains(match, StringComparison.OrdinalIgnoreCase)) return rows;
        }
        return [];
    }

    [AllowNull]
    public override string ConnectionString { get; set; } = "";
    public override string Database => "testdb";
    public override string DataSource => "fake";
    public override string ServerVersion => "0.0";
    public override ConnectionState State => _state;

    public override void Open() => _state = ConnectionState.Open;
    public override void Close() => _state = ConnectionState.Closed;
    public override void ChangeDatabase(string databaseName) { }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new FakeCommand(this);
}

internal sealed class FakeCommand(FakeDataConnection connection) : DbCommand
{
    [AllowNull]
    public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; } = connection;
    protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override int ExecuteNonQuery() => 0;

    public override object? ExecuteScalar()
    {
        var rows = connection.Rows(CommandText);
        return rows.Count == 0 ? null : rows[0].Values.FirstOrDefault();
    }

    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new FakeParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        new FakeReader(connection.Rows(CommandText));
}

internal sealed class FakeParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    [AllowNull] public override string ParameterName { get; set; } = "";
    public override int Size { get; set; }
    [AllowNull] public override string SourceColumn { get; set; } = "";
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override void ResetDbType() { }
}

internal sealed class FakeParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => _items;

    public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains((DbParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
}

internal sealed class FakeReader(List<Dictionary<string, object?>> rows) : DbDataReader
{
    private int _index = -1;

    private Dictionary<string, object?> Current => rows[_index];
    private string[] Columns => field ??= rows.Count == 0 ? [] : Current.Keys.ToArray();

    public override int FieldCount => Columns.Length;
    public override bool HasRows => rows.Count > 0;
    public override bool IsClosed => _index >= rows.Count;
    public override int RecordsAffected => 0;
    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => Current[name] ?? DBNull.Value;

    public override bool Read() => ++_index < rows.Count;
    public override bool NextResult() => false;

    public override string GetName(int ordinal) => Columns[ordinal];
    public override int GetOrdinal(string name) => Array.FindIndex(Columns, c =>
        string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

    public override object GetValue(int ordinal) => Current[Columns[ordinal]] ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => Current[Columns[ordinal]] is null or DBNull;

    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override Type GetFieldType(int ordinal) => GetValue(ordinal).GetType();
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal));
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => Convert.ToString(GetValue(ordinal)) ?? "";

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override IEnumerator GetEnumerator() => rows.GetEnumerator();
}
