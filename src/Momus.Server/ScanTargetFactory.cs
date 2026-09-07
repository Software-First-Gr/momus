using Momus.Core;
using Momus.Postgres;
using Momus.Server.Store;
using Momus.SqlServer;

namespace Momus.Server;

/// <summary>Turns a stored target into something the collector engine can scan.</summary>
public interface IScanTargetFactory
{
    IScanTarget Create(string provider, string connectionString);

    /// <summary>Provider keys this server understands, for the Settings page and error messages.</summary>
    IReadOnlyList<string> Providers { get; }
}

public sealed class ScanTargetFactory : IScanTargetFactory
{
    /// <summary>
    /// The console shows five expensive queries; the store keeps fifty. A query that is #23 for
    /// the database can still be the one an endpoint runs forty times per request, and that join
    /// is only possible if the row was kept.
    /// </summary>
    public const int TopQueryLimit = 50;

    public IReadOnlyList<string> Providers { get; } = ["postgres", "sqlserver"];

    public IScanTarget Create(string provider, string connectionString) => provider.ToLowerInvariant() switch
    {
        "postgres" or "postgresql" or "pg" => new PostgresScanTarget(connectionString, TopQueryLimit),
        "sqlserver" or "mssql" => new SqlServerScanTarget(connectionString, TopQueryLimit),
        _ => throw new ArgumentException($"Unknown provider '{provider}'. Use postgres or sqlserver."),
    };
}

/// <summary>
/// The most common first-run failure: Momus runs in a container, the connection string says
/// localhost, and localhost inside a container is the container. Docker Desktop publishes the host
/// as <c>host.docker.internal</c>, so the scheduler retries once with that and remembers which one
/// worked, instead of showing a connection error the user has to decode.
/// </summary>
public static class DockerHostFallback
{
    public const string DockerHost = "host.docker.internal";

    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "[::1]"];

    public static bool InContainer =>
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    /// <summary>The same connection string pointed at the Docker host, or null when it would not help.</summary>
    public static string? Alternative(string connectionString)
    {
        var replaced = false;
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            var equals = parts[i].IndexOf('=');
            if (equals <= 0) continue;

            var key = parts[i][..equals].Trim();
            var value = parts[i][(equals + 1)..].Trim();
            if (!IsHostKey(key)) continue;

            // "localhost,1433" and "localhost:5432" keep whatever follows the host.
            var separator = value.IndexOfAny([',', ':']);
            var host = separator > 0 ? value[..separator] : value;
            var suffix = separator > 0 ? value[separator..] : "";
            if (!LocalHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) continue;

            parts[i] = $"{key}={DockerHost}{suffix}";
            replaced = true;
        }

        return replaced ? string.Join(';', parts) : null;
    }

    private static bool IsHostKey(string key) =>
        key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Address", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Addr", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Network Address", StringComparison.OrdinalIgnoreCase);
}
