using Momus.Cli;

// One binary, several commands. `scan` is the original tool and keeps its flags, its output and
// its exit codes exactly; `serve` is the same collector with a schedule, a store and a page.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var command = args.Length == 0 ? "help" : args[0];
var rest = args.Length <= 1 ? [] : args[1..];

return command switch
{
    "scan" => await ScanCommand.RunAsync(rest, cts.Token),
    "serve" => await ServeCommand.RunAsync(rest, cts.Token),
    "-h" or "--help" or "help" => Usage.Print(),
    "--version" or "-v" or "version" => Usage.PrintVersion(),
    _ => Usage.Unknown(command),
};
