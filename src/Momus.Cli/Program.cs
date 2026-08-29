using Momus.Cli;
using Momus.Core;
using Momus.Postgres;
using Momus.SqlServer;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

if (args[0] != "scan")
{
    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
    PrintUsage();
    return 2;
}

string? provider = null;
string? connectionString = null;
string? jsonPath = null;
var quiet = false;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--provider" or "-p" when i + 1 < args.Length:
            provider = args[++i].ToLowerInvariant();
            break;
        case "--connection" or "-c" when i + 1 < args.Length:
            connectionString = args[++i];
            break;
        case "--json" when i + 1 < args.Length:
            jsonPath = args[++i];
            break;
        case "--quiet" or "-q":
            quiet = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete option '{args[i]}'.");
            PrintUsage();
            return 2;
    }
}

connectionString ??= Environment.GetEnvironmentVariable("MOMUS_CONNECTION");
provider ??= Environment.GetEnvironmentVariable("MOMUS_PROVIDER")?.ToLowerInvariant();

if (provider is null || connectionString is null)
{
    Console.Error.WriteLine("Both --provider and --connection are required " +
                            "(or set MOMUS_PROVIDER / MOMUS_CONNECTION).");
    return 2;
}

IScanTarget target;
switch (provider)
{
    case "postgres" or "postgresql" or "pg":
        target = new PostgresScanTarget(connectionString);
        break;
    case "sqlserver" or "mssql":
        target = new SqlServerScanTarget(connectionString);
        break;
    default:
        Console.Error.WriteLine($"Unknown provider '{provider}'. Use 'postgres' or 'sqlserver'.");
        return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ScanReport report;
try
{
    report = await new CollectorEngine().ScanAsync(target, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Scan cancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Scan failed before any checks could run: {ex.Message}");
    return 1;
}

if (!quiet)
{
    ConsoleReport.Render(report);
}

if (jsonPath is not null)
{
    await File.WriteAllTextAsync(jsonPath, JsonReport.Serialize(report), cts.Token);
    if (!quiet) Console.WriteLine($"JSON report written to {jsonPath}");
}

// Exit code communicates worst severity so scripts/CI can react.
return report.CountAtLeast(Severity.High) > 0 ? 3 : 0;

static void PrintUsage()
{
    Console.WriteLine("""
        Momus — the critic your database deserves.

        Usage:
          momus scan --provider <postgres|sqlserver> --connection "<connection string>" [options]

        Options:
          -p, --provider     Database type: postgres | sqlserver
          -c, --connection   ADO.NET connection string (or env MOMUS_CONNECTION)
              --json <file>  Also write the full report as JSON
          -q, --quiet        Suppress console report (useful with --json)

        Exit codes:
          0 = scan ran, nothing High/Critical    3 = High or Critical findings
          1 = scan failed    2 = bad arguments
        """);
}
