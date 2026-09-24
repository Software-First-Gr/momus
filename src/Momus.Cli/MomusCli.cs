using System.Globalization;

namespace Momus.Cli;

/// <summary>
/// One binary, several commands. `scan` is the original tool and keeps its flags, its output and
/// its exit codes exactly; `serve` is the same collector with a schedule, a store and a page.
/// </summary>
public static class MomusCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        // Pinned before any command runs, as MomusServer does for the server. A check writes its
        // sentence while the scan runs, so on a Mac set to Greek `scan` printed "0,2s" and "260,8 ms
        // per execution, 227.452 total logical reads" — into the console and into --json, which CI
        // scripts parse — while the same scan in the invariant Docker image printed "0.6s".
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        var command = args.Length == 0 ? "help" : args[0];
        var rest = args.Length <= 1 ? [] : args[1..];

        return command switch
        {
            "scan" => await ScanCommand.RunAsync(rest, ct),
            "serve" => await ServeCommand.RunAsync(rest, ct),
            "-h" or "--help" or "help" => Usage.Print(),
            "--version" or "-v" or "version" => Usage.PrintVersion(),
            _ => Usage.Unknown(command),
        };
    }
}
