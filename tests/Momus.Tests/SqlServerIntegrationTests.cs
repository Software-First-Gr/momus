using Microsoft.Data.SqlClient;
using Momus.Core;
using Momus.SqlServer;
using Momus.SqlServer.Checks;

namespace Momus.Tests;

/// <summary>
/// Live SQL Server tests, opt-in like <see cref="PostgresIntegrationTests"/>: set MOMUS_TEST_MSSQL to
/// a connection string for a login that may create databases, for example
///   docker run -d -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='…' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
///   MOMUS_TEST_MSSQL="Server=localhost;User Id=sa;Password=…;TrustServerCertificate=True" dotnet test
/// Every test creates the databases it needs and drops them. Unset, every test passes trivially.
///
/// Most of these recreate one situation: two databases on one instance running the same
/// application — a staging database beside a demo one. The statement and session views are
/// instance-wide, so without a filter each database's scan reported the other's work, under the
/// same fingerprints.
/// </summary>
public class SqlServerIntegrationTests
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("MOMUS_TEST_MSSQL");

    [Fact]
    public async Task Full_scan_runs_every_check_against_a_real_server()
    {
        if (Server is null) return;
        await using var db = await TestDatabase.CreateAsync(Server);

        var target = new SqlServerScanTarget(db.ConnectionString, topQueryLimit: 500);
        var report = await new CollectorEngine().ScanAsync(target);

        Assert.Equal("sqlserver", report.Target.Provider);
        Assert.Equal(db.Name, report.Target.DatabaseName);
        Assert.Equal(target.Checks.Count, report.Checks.Count);
        Assert.All(report.Checks, c => Assert.True(c.Succeeded, $"{c.CheckId} failed: {c.Error}"));
    }

    [Fact]
    public async Task Top_queries_count_only_the_scanned_databases_executions()
    {
        if (Server is null) return;
        await using var mine = await TestDatabase.CreateAsync(Server);
        await using var neighbour = await TestDatabase.CreateAsync(Server);

        // A parameterized command is sent as sp_executesql, the shape of everything EF Core runs,
        // and the one for which dm_exec_sql_text reports no database at all.
        var sql = $"SELECT v AS v_{mine.Token} FROM t WHERE id = @p0";
        await mine.RunAsync(sql, times: 3);
        await neighbour.RunAsync(sql, times: 7);

        var findings = await RunAsync(new TopCpuQueriesCheck(500), mine.ConnectionString);

        var statement = Assert.Single(findings, f => f.Subjects.Contains(Subject.ForQuery(SqlFingerprint.Compute(sql))));
        Assert.Equal(3L, statement.Evidence["execution_count"]);
        Assert.Equal(mine.Name, statement.Evidence["database"]);
    }

    [Fact]
    public async Task A_scan_through_master_still_sees_every_database()
    {
        if (Server is null) return;
        await using var one = await TestDatabase.CreateAsync(Server);
        await using var other = await TestDatabase.CreateAsync(Server);

        var sql = $"SELECT v AS v_{one.Token} FROM t WHERE id = @p0";
        await one.RunAsync(sql, times: 3);
        await other.RunAsync(sql, times: 7);

        var master = new SqlConnectionStringBuilder(Server) { InitialCatalog = "master" }.ConnectionString;
        var findings = await RunAsync(new TopCpuQueriesCheck(500), master);

        var both = findings.Where(f => f.Subjects.Contains(Subject.ForQuery(SqlFingerprint.Compute(sql)))).ToList();
        Assert.Equal(
            new[] { (one.Name, 3L), (other.Name, 7L) }.OrderBy(x => x.Item2),
            both.Select(f => ((string)f.Evidence["database"]!, (long)f.Evidence["execution_count"]!)).OrderBy(x => x.Item2));
    }

    [Fact]
    public async Task Blocking_in_a_neighbouring_database_is_that_databases_finding()
    {
        if (Server is null) return;
        await using var mine = await TestDatabase.CreateAsync(Server);
        await using var neighbour = await TestDatabase.CreateAsync(Server);

        await using var holder = new SqlConnection(neighbour.ConnectionString);
        await holder.OpenAsync();
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync();
        await using (var hold = new SqlCommand("UPDATE t SET v = v + 1 WHERE id = 1", holder, transaction))
        {
            await hold.ExecuteNonQueryAsync();
        }

        await using var waiter = new SqlConnection(neighbour.ConnectionString);
        await waiter.OpenAsync();
        long waiterId;
        await using (var spid = new SqlCommand("SELECT @@SPID", waiter))
        {
            waiterId = Convert.ToInt64(await spid.ExecuteScalarAsync());
        }
        await using var wait = new SqlCommand("UPDATE t SET v = v + 1 WHERE id = 1", waiter) { CommandTimeout = 60 };
        var blocked = wait.ExecuteNonQueryAsync();

        try
        {
            await WaitUntilBlockedAsync(Server, waiterId);

            var here = await RunAsync(new BlockingSessionsCheck(), mine.ConnectionString);
            Assert.DoesNotContain(here, f => f.Subjects.Contains(Subject.ForSession(waiterId)));

            var there = await RunAsync(new BlockingSessionsCheck(), neighbour.ConnectionString);
            var finding = Assert.Single(there, f => f.Subjects.Contains(Subject.ForSession(waiterId)));
            Assert.Equal(neighbour.Name, finding.Evidence["database"]);
        }
        finally
        {
            await transaction.RollbackAsync();
            await blocked;
        }
    }

    /// <summary>
    /// The least a scanning login needs, and what each part is for. Measured on SQL Server 2022:
    /// with VIEW SERVER STATE alone the login cannot open the database and no check runs; with a
    /// user in it every check runs — and the missing-index check used to name no table, because
    /// OBJECT_NAME needs metadata visibility that login does not have.
    /// </summary>
    [Fact]
    public async Task The_least_login_that_works_is_view_server_state_and_a_user_in_the_database()
    {
        if (Server is null) return;
        await using var db = await TestDatabase.CreateAsync(Server);

        // A table big enough, and a query shaped enough, for the optimizer to want an index. A
        // trivial plan never gets a suggestion.
        await TestDatabase.ExecuteAsync(db.ConnectionString, """
            SET NOCOUNT ON;
            CREATE TABLE dbo.orders (id int IDENTITY PRIMARY KEY, customer_id int, status int, filler char(200) DEFAULT 'x');
            INSERT dbo.orders (customer_id, status)
                SELECT TOP 50000 ABS(CHECKSUM(NEWID())) % 5000, 1 FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            DECLARE @i int = 0;
            WHILE @i < 300
            BEGIN
                EXEC sp_executesql N'SELECT o.id, o.status FROM dbo.orders o WHERE o.customer_id = @id AND o.status > 0 ORDER BY o.status, o.id',
                    N'@id int', @id = 42;
                SET @i += 1;
            END
            """);

        var login = $"momus_{db.Token}";
        const string password = "Least-Login-2026-ok";
        await TestDatabase.ExecuteAsync(Server, $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}'; GRANT VIEW SERVER STATE TO [{login}];");
        var asLogin = new SqlConnectionStringBuilder(db.ConnectionString)
        {
            UserID = login, Password = password, IntegratedSecurity = false, Pooling = false,
        }.ConnectionString;

        try
        {
            var noUser = await Assert.ThrowsAsync<SqlException>(() => RunAsync(new MissingIndexesCheck(), asLogin));
            Assert.Contains("Cannot open database", noUser.Message);

            await TestDatabase.ExecuteAsync(db.ConnectionString, $"CREATE USER [{login}] FOR LOGIN [{login}];");

            var report = await new CollectorEngine().ScanAsync(new SqlServerScanTarget(asLogin));
            Assert.All(report.Checks, c => Assert.True(c.Succeeded, $"{c.CheckId} failed: {c.Error}"));

            var missing = Assert.Single(report.Checks, c => c.CheckId == "mssql.missing_indexes").Findings;
            var finding = Assert.Single(missing);
            Assert.Contains(Subject.ForTable("dbo.orders"), finding.Subjects);
            Assert.Contains("dbo.orders", finding.Title);
        }
        finally
        {
            await TestDatabase.ExecuteAsync(Server, $"DROP LOGIN [{login}];");
        }
    }

    private static async Task<IReadOnlyList<Finding>> RunAsync(IDiagnosticCheck check, string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await check.RunAsync(connection, CancellationToken.None);
    }

    private static async Task WaitUntilBlockedAsync(string server, long sessionId)
    {
        await using var connection = new SqlConnection(server);
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var probe = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @id AND blocking_session_id <> 0",
                connection);
            probe.Parameters.AddWithValue("@id", sessionId);
            if (Convert.ToInt32(await probe.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Session {sessionId} never showed as blocked.");
    }

    /// <summary>A database of its own, with one small table, dropped when the test ends.</summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _server;

        private TestDatabase(string server, string token)
        {
            _server = server;
            Token = token;
            Name = $"momus_test_{token}";
            ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = Name }.ConnectionString;
        }

        public string Token { get; }
        public string Name { get; }
        public string ConnectionString { get; }

        public static async Task<TestDatabase> CreateAsync(string server)
        {
            var db = new TestDatabase(server, Guid.NewGuid().ToString("N")[..8]);
            await ExecuteAsync(server, $"CREATE DATABASE [{db.Name}]");
            await ExecuteAsync(db.ConnectionString, "CREATE TABLE t (id int PRIMARY KEY, v int); INSERT t VALUES (1, 1), (2, 2);");
            return db;
        }

        public async Task RunAsync(string sql, int times)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            for (var i = 0; i < times; i++)
            {
                await using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@p0", 1);
                await command.ExecuteScalarAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            // A pooled connection keeps the database in use; ROLLBACK IMMEDIATE would break it later.
            SqlConnection.ClearAllPools();
            await ExecuteAsync(_server,
                $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}];");
        }

        public static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
