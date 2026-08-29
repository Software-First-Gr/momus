using Momus.Core;
using Momus.Postgres;
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
}
