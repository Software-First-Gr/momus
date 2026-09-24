using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Momus.Server;

/// <summary>
/// Decides whether a window may come in, and remembers the ones it turned away. An application
/// whose key is wrong looks, from every other page, exactly like an application that is not
/// running; the diagnostics page reads the refusals from here so that it can say which.
/// </summary>
public sealed class IngestGate(ServerOptions options, ILogger<IngestGate> logger)
{
    private readonly byte[]? _expected = options.IngestKey is { Length: > 0 } key ? Hash(key) : null;
    private readonly Lock _gate = new();
    private long _refused;
    private DateTimeOffset? _lastAt;
    private string? _lastFrom;

    public bool RequiresKey => _expected is not null;

    /// <summary>
    /// True when no key is configured, or when <paramref name="presented"/> is it. Compared as
    /// hashes, in constant time, so neither the content nor the length leaks through timing.
    /// </summary>
    public bool Admits(string? presented) =>
        _expected is null ||
        (presented is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Hash(presented), _expected));

    public void Refuse(string? from)
    {
        bool first;
        lock (_gate)
        {
            first = _refused == 0;
            _refused++;
            _lastAt = DateTimeOffset.UtcNow;
            _lastFrom = from;
        }

        // Once. A misconfigured application posts every few seconds, and the count is on the
        // diagnostics page.
        if (first)
        {
            logger.LogWarning(
                "Refused a window from {From}: missing or wrong {Header}. Its Momus:IngestKey must equal MOMUS_INGEST_KEY.",
                from ?? "an unknown address", Core.Ingest.IngestJson.KeyHeader);
        }
    }

    public IngestRefusals Refusals
    {
        get
        {
            lock (_gate) return new IngestRefusals(_refused, _lastAt, _lastFrom);
        }
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

/// <summary>Windows turned away since the server started, and the last one.</summary>
public sealed record IngestRefusals(long Count, DateTimeOffset? LastAt, string? LastFrom);
