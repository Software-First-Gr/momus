namespace Momus.Client;

/// <summary>
/// Everything the client can be told, bound from the <c>Momus:</c> configuration section. Every
/// default is chosen so that the answer to "what do I have to configure" is nothing.
/// </summary>
public sealed class MomusOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string Section = "Momus";

    /// <summary>
    /// Null means "Development only", which is the default. Setting it to true in Production is a
    /// deliberate act, and the overhead budget in the README is what makes that a reasonable one.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>Where the Momus server is. The same port convention as Seq or Jaeger: one to remember.</summary>
    public string Endpoint { get; set; } = "http://localhost:4848";

    /// <summary>
    /// Whether to tell the server how to reach the database. Null means "only when the endpoint is
    /// loopback", so a developer configures the database once, on the app side, and a server
    /// somewhere else is never handed credentials by accident.
    /// </summary>
    public bool? ShareConnectionStrings { get; set; }

    /// <summary>How often a window is closed and sent. Frequent enough to feel live, rare enough to be nothing.</summary>
    public double FlushSeconds { get; set; } = 5;

    /// <summary>Defaults to the entry assembly's name.</summary>
    public string? AppName { get; set; }

    /// <summary>Defaults to the host environment's name.</summary>
    public string? Environment { get; set; }

    /// <summary>
    /// Ceiling on distinct (statement × operation × call site) keys held in one window. Past it,
    /// executions are counted in one overflow number the UI shows rather than being forgotten.
    /// </summary>
    public int MaxKeysPerWindow { get; set; } = 2_000;

    /// <summary>Ceiling on distinct statements tracked within a single operation.</summary>
    public int MaxKeysPerOperation { get; set; } = 200;

    /// <summary>Completed operations that may queue up for the exporter before it starts dropping them.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    internal TimeSpan FlushInterval => TimeSpan.FromSeconds(Math.Clamp(FlushSeconds, 1, 300));

    /// <summary>True when the endpoint points at this machine, which is what makes sharing safe by default.</summary>
    internal bool EndpointIsLoopback =>
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase));

    internal bool SharesConnectionStrings => ShareConnectionStrings ?? EndpointIsLoopback;
}
