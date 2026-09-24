using System.Data.Common;
using System.Globalization;
using Momus.Cli;
using Momus.Core;
using Momus.SqlServer.Checks;
using Momus.Tests.Fakes;

namespace Momus.Tests;

/// <summary>
/// `momus scan` on a machine whose locale writes decimals with a comma. A check builds its sentence
/// while the scan runs and the console renders the duration while it prints, so both take the
/// culture the process is in — and `--json`, which CI scripts parse, carries the check's text as is.
/// </summary>
/// <remarks>
/// Found on a Mac set to Greek: "0,2s · 5 checks" and "Average CPU 260,8 ms per execution, 227.452
/// total logical reads", while the same scan in the (invariant) Docker image printed "0.6s".
/// </remarks>
[Collection(nameof(ProcessCulture))]
public class CliCultureTests
{
    [Fact]
    public async Task Scan_output_reads_as_English_on_a_Greek_machine()
    {
        var (culture, uiCulture) = (CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        var stdout = Console.Out;
        try
        {
            // What a machine set to Greek gives every thread. It goes on the process default, not on
            // this thread, because that is where a real process gets its culture from.
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("el-GR");
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("el-GR");
            Assert.Equal("260,8", 260.8.ToString("N1"));

            // Every command pins the culture before it runs, so the cheapest one will do.
            Console.SetOut(TextWriter.Null);
            Assert.Equal(0, await MomusCli.RunAsync(["--version"], CancellationToken.None));

            var connection = new FakeDataConnection().When("dm_exec_query_stats", Row(
                ("total_cpu_ms", 1_304L), ("execution_count", 5L), ("avg_cpu_ms", 260.8),
                ("total_logical_reads", 227_452L), ("query_hash", "0x5A1D6C1B2F0E9D11"), ("database_name", "shop"),
                ("statement_text", "SELECT * FROM orders WHERE customer_id = @p0"),
                ("query_text", "SELECT * FROM orders WHERE customer_id = @p0")));
            var report = await new CollectorEngine().ScanAsync(new SqlServerOnCannedRows(connection));

            var console = new StringWriter();
            Console.SetOut(console);
            ConsoleReport.Render(report);
            var json = JsonReport.Serialize(report);

            const string detail = "Average CPU 260.8 ms per execution, 227,452 total logical reads.";
            Assert.Contains(detail, console.ToString());
            Assert.Contains(detail, json);
            Assert.Matches(@" · \d+\.\ds · 1 checks", console.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        }
    }

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in cells) row[name] = value;
        return row;
    }

    private sealed class SqlServerOnCannedRows(FakeDataConnection connection) : IScanTarget
    {
        public string Provider => "sqlserver";
        public IReadOnlyList<IDiagnosticCheck> Checks { get; } = [new TopCpuQueriesCheck()];
        public DbConnection CreateConnection() => connection;

        public Task<TargetInfo> GetTargetInfoAsync(DbConnection connection, CancellationToken ct) =>
            Task.FromResult(new TargetInfo { Provider = "sqlserver", DatabaseName = "shop" });
    }
}

/// <summary>
/// The default culture belongs to the whole process, so a test that changes it runs alone: no other
/// test sees the Greek default it sets, and no other test pins it back half-way through.
/// </summary>
[CollectionDefinition(nameof(ProcessCulture), DisableParallelization = true)]
public sealed class ProcessCulture;
