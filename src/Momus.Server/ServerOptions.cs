using System.Globalization;

namespace Momus.Server;

/// <summary>A database the server should scan, before it has ever been contacted.</summary>
/// <param name="Name">Display name; also the source of the stable id.</param>
/// <param name="Provider">postgres or sqlserver.</param>
/// <param name="ConnectionString">Stored as given. A read-only user is enough.</param>
/// <param name="Source">env, cli or ui — shown in Settings so it is obvious what can be removed there.</param>
public sealed record TargetSpec(string Name, string Provider, string ConnectionString, string Source)
{
    /// <summary>Stable id derived from the name, so restarting does not duplicate targets.</summary>
    public string Id => Slug(Name);

    public static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "target" : slug;
    }
}

/// <summary>Everything `momus serve` needs. Defaults are chosen so that no flag is required.</summary>
public sealed record ServerOptions
{
    /// <summary>Where the SQLite file lives. In the container this is the mounted volume.</summary>
    public string DataDirectory { get; init; } =
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            ? "/data"
            : Path.Combine(Environment.CurrentDirectory, ".momus");

    public int Port { get; init; } = 4848;

    /// <summary>Statistics views are cheap to read; a minute keeps the history useful.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(60);

    public IReadOnlyList<TargetSpec> Targets { get; init; } = [];

    public string DatabasePath => Path.Combine(DataDirectory, "momus.db");

    /// <summary>
    /// Reads the environment: <c>MOMUS_DATA</c>, <c>MOMUS_PORT</c>, <c>MOMUS_SCAN_INTERVAL</c> and
    /// <c>MOMUS_TARGETS__0__NAME</c> / <c>__PROVIDER</c> / <c>__CONNECTIONSTRING</c>. The indexed
    /// form is the same shape ASP.NET Core configuration uses, so a compose file reads naturally.
    /// </summary>
    public static ServerOptions FromEnvironment()
    {
        var options = new ServerOptions();

        var data = Environment.GetEnvironmentVariable("MOMUS_DATA");
        var port = Environment.GetEnvironmentVariable("MOMUS_PORT");
        var interval = Environment.GetEnvironmentVariable("MOMUS_SCAN_INTERVAL");

        return options with
        {
            DataDirectory = string.IsNullOrWhiteSpace(data) ? options.DataDirectory : data,
            Port = int.TryParse(port, out var p) ? p : options.Port,
            ScanInterval = ParseInterval(interval) ?? options.ScanInterval,
            Targets = TargetsFromEnvironment().ToList(),
        };
    }

    private static IEnumerable<TargetSpec> TargetsFromEnvironment()
    {
        for (var i = 0; ; i++)
        {
            var connectionString = Env(i, "CONNECTIONSTRING") ?? Env(i, "CONNECTION");
            if (string.IsNullOrWhiteSpace(connectionString)) yield break;

            var provider = Env(i, "PROVIDER") ?? "postgres";
            yield return new TargetSpec(Env(i, "NAME") ?? $"target-{i + 1}", provider, connectionString, "env");
        }

        static string? Env(int index, string key) =>
            Environment.GetEnvironmentVariable($"MOMUS_TARGETS__{index}__{key}");
    }

    /// <summary>Accepts <c>90s</c>, <c>5m</c>, <c>1h</c> or a bare number of seconds.</summary>
    public static TimeSpan? ParseInterval(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant();

        var (value, unit) = text[^1] switch
        {
            's' => (text[..^1], TimeSpan.FromSeconds(1)),
            'm' => (text[..^1], TimeSpan.FromMinutes(1)),
            'h' => (text[..^1], TimeSpan.FromHours(1)),
            _ => (text, TimeSpan.FromSeconds(1)),
        };

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0
            ? unit * n
            : null;
    }

    /// <summary>Parses <c>--target postgres:Host=localhost;Database=shop</c>, optionally <c>name=provider:...</c>.</summary>
    public static TargetSpec? ParseTargetFlag(string value, int index)
    {
        var name = $"target-{index + 1}";

        var equals = value.IndexOf('=');
        var colon = value.IndexOf(':');
        if (equals > 0 && (colon < 0 || equals < colon))
        {
            name = value[..equals];
            value = value[(equals + 1)..];
            colon = value.IndexOf(':');
        }

        if (colon <= 0) return null;
        var provider = value[..colon].Trim();
        var connectionString = value[(colon + 1)..].Trim();

        return connectionString.Length == 0
            ? null
            : new TargetSpec(name, provider, connectionString, "cli");
    }
}
