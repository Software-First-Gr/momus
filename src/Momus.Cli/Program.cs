using Momus.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

return await MomusCli.RunAsync(args, cts.Token);
