using Momus.Server;

namespace Momus.Cli;

/// <summary>
/// `momus serve`: the same collector, running on a schedule with a memory. Flags override the
/// environment, so a compose file can set everything and a developer can override one thing.
/// </summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var options = ServerOptions.FromEnvironment();
        var targets = options.Targets.ToList();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" or "-d" when i + 1 < args.Length:
                    options = options with { DataDirectory = args[++i] };
                    break;

                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var port) || port is < 1 or > 65535)
                    {
                        Console.Error.WriteLine($"'{args[i]}' is not a port number.");
                        return 2;
                    }
                    options = options with { Port = port };
                    break;

                case "--scan-interval" when i + 1 < args.Length:
                    var interval = ServerOptions.ParseInterval(args[++i]);
                    if (interval is null)
                    {
                        Console.Error.WriteLine($"'{args[i]}' is not an interval. Try 30s, 5m or 1h.");
                        return 2;
                    }
                    options = options with { ScanInterval = interval.Value };
                    break;

                case "--target" or "-t" when i + 1 < args.Length:
                    var spec = ServerOptions.ParseTargetFlag(args[++i], targets.Count);
                    if (spec is null)
                    {
                        Console.Error.WriteLine(
                            $"'{args[i]}' is not a target. Use --target postgres:\"Host=…;Database=…\".");
                        return 2;
                    }
                    targets.Add(spec);
                    break;

                default:
                    Console.Error.WriteLine($"Unknown or incomplete option '{args[i]}'.");
                    Usage.Print();
                    return 2;
            }
        }

        try
        {
            return await MomusServer.RunAsync(options with { Targets = targets }, ct);
        }
        catch (OperationCanceledException)
        {
            return 0; // Ctrl-C is how you stop a server.
        }
    }
}
