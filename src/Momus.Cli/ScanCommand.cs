using Momus.Core;
using Momus.Postgres;
using Momus.SqlServer;

namespace Momus.Cli;

/// <summary>
/// `momus scan`: one scan, printed and optionally exported. Exit code 3 on High or Critical, so a
/// CI step can fail on it. Flags and exit codes are frozen — scripts depend on them.
/// </summary>
public static class ScanCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? provider = null;
        string? connectionString = null;
        string? jsonPath = null;
        var quiet = false;

        for (var i = 0; i < args.Length; i++)
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
                    Usage.Print();
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

        ScanReport report;
        try
        {
            report = await new CollectorEngine().ScanAsync(target, ct);
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
            await File.WriteAllTextAsync(jsonPath, JsonReport.Serialize(report), ct);
            if (!quiet) Console.WriteLine($"JSON report written to {jsonPath}");
        }

        // Exit code communicates worst severity so scripts/CI can react.
        return report.CountAtLeast(Severity.High) > 0 ? 3 : 0;
    }
}
