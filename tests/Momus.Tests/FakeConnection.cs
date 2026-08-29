using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Momus.Tests;

/// <summary>A DbConnection that opens instantly and does nothing — for engine tests without a server.</summary>
public sealed class FakeConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

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

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
}
