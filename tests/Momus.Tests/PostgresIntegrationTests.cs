using Momus.Core;
using Momus.Postgres;
using Momus.Postgres.Checks;
using Npgsql;
using Xunit;

namespace Momus.Tests;

/// <summary>
/// Live-database test, opt-in: set MOMUS_TEST_PG to a Postgres connection string to enable, e.g.
///   MOMUS_TEST_PG="Host=localhost;Username=postgres;Database=postgres" dotnet test
/// When the variable is not set the test passes trivially (no extra skip-package dependency).
/// </summary>
public class PostgresIntegrationTests
{
    [Fact]
    public async Task Full_scan_runs_every_check_against_a_real_server()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOMUS_TEST_PG");
        if (connectionString is null) return; // not configured — nothing to assert

        var target = new PostgresScanTarget(connectionString);
        var report = await new CollectorEngine().ScanAsync(target);

        Assert.Equal("postgres", report.Target.Provider);
        Assert.NotNull(report.Target.ServerVersion);
        Assert.Equal(target.Checks.Count, report.Checks.Count);
        Assert.All(report.Checks, c => Assert.True(c.Succeeded, $"{c.CheckId} failed: {c.Error}"));
    }

    /// <summary>
    /// pg_stat_activity lists the whole cluster's sessions. A lock wait in a neighbouring database
    /// running the same application used to be reported here too — and, since D22, joined to this
    /// application's statements by fingerprint as if it were its own.
    /// </summary>
    [Fact]
    public async Task A_lock_wait_in_a_neighbouring_database_is_that_databases_finding()
    {
        var server = Environment.GetEnvironmentVariable("MOMUS_TEST_PG");
        if (server is null) return;

        var token = Guid.NewGuid().ToString("N")[..8];
        var mine = $"momus_test_{token}_a";
        var neighbour = $"momus_test_{token}_b";
        string In(string database) => new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString;

        await ExecuteAsync(server, $"CREATE DATABASE {mine}");
        await ExecuteAsync(server, $"CREATE DATABASE {neighbour}");
        try
        {
            await ExecuteAsync(In(neighbour), "CREATE TABLE t (id int PRIMARY KEY, v int); INSERT INTO t VALUES (1, 1);");

            await using var holder = new NpgsqlConnection(In(neighbour));
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("UPDATE t SET v = v + 1 WHERE id = 1", holder, transaction))
            {
                await hold.ExecuteNonQueryAsync();
            }

            await using var waiter = new NpgsqlConnection(In(neighbour));
            await waiter.OpenAsync();
            long waiterPid;
            await using (var pid = new NpgsqlCommand("SELECT pg_backend_pid()", waiter))
            {
                waiterPid = Convert.ToInt64(await pid.ExecuteScalarAsync());
            }
            await using var wait = new NpgsqlCommand("UPDATE t SET v = v + 1 WHERE id = 1", waiter);
            var blocked = wait.ExecuteNonQueryAsync();

            try
            {
                // The check reports waits from LockWaitMinSeconds on.
                await Task.Delay(TimeSpan.FromSeconds(PostgresThresholds.LockWaitMinSeconds + 1.5));

                var here = await RunAsync(new LockWaitsCheck(), In(mine));
                Assert.DoesNotContain(here, f => f.Subjects.Contains(Subject.ForSession(waiterPid)));

                var there = await RunAsync(new LockWaitsCheck(), In(neighbour));
                Assert.Contains(there, f => f.Subjects.Contains(Subject.ForSession(waiterPid)));
            }
            finally
            {
                await transaction.RollbackAsync();
                await blocked;
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(server, $"DROP DATABASE IF EXISTS {mine} WITH (FORCE)");
            await ExecuteAsync(server, $"DROP DATABASE IF EXISTS {neighbour} WITH (FORCE)");
        }
    }

    private static async Task<IReadOnlyList<Finding>> RunAsync(IDiagnosticCheck check, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await check.RunAsync(connection, CancellationToken.None);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
