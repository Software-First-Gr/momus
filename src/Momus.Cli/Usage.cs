using Momus.Server;

namespace Momus.Cli;

public static class Usage
{
    public static int Print()
    {
        Console.WriteLine($"""
            Momus {MomusServer.Version} — the critic your database deserves.

            Usage:
              momus scan  --provider <postgres|sqlserver> --connection "<connection string>" [options]
              momus serve [--target <provider>:"<connection string>"] [options]

            scan — run every check once and print what is wrong. For a terminal or for CI.
              -p, --provider     Database type: postgres | sqlserver
              -c, --connection   ADO.NET connection string (or env MOMUS_CONNECTION)
                  --json <file>  Also write the full report as JSON
              -q, --quiet        Suppress the console report (useful with --json)

              Exit codes: 0 = nothing High/Critical · 3 = High or Critical findings
                          1 = scan failed · 2 = bad arguments

            serve — scan on a schedule, keep the history, show it on one page.
                  --target <provider>:<connection string>   May be repeated. Also name=provider:...
                  --data <dir>            Where the SQLite file lives     (MOMUS_DATA)
                  --port <n>              HTTP port, default 4848         (MOMUS_PORT)
                  --scan-interval <90s>   Default 60s                     (MOMUS_SCAN_INTERVAL)

              Targets can also come from the environment, which is what the Docker image uses:
                  MOMUS_TARGETS__0__NAME, __PROVIDER, __CONNECTIONSTRING

            Momus only ever reads the engine's own statistics views. A read-only user is enough.
            """);
        return 0;
    }

    public static int PrintVersion()
    {
        Console.WriteLine(MomusServer.Version);
        return 0;
    }

    public static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Print();
        return 2;
    }
}
