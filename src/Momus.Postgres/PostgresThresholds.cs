using Momus.Core;

namespace Momus.Postgres;

/// <summary>
/// Pure threshold logic, separated from SQL so it can be unit-tested without a database.
/// Returning null means "healthy, no finding".
/// </summary>
public static class PostgresThresholds
{
    /// <summary>Minimum block reads before the cache hit ratio is statistically meaningful.</summary>
    public const long CacheHitMinSamples = 10_000;

    public static Severity? CacheHitSeverity(double ratio, long totalBlocks)
    {
        if (totalBlocks < CacheHitMinSamples) return null;
        return ratio switch
        {
            < 0.90 => Severity.High,
            < 0.95 => Severity.Medium,
            < 0.99 => Severity.Low,
            _ => null,
        };
    }

    public static Severity? ConnectionSaturationSeverity(long used, long max)
    {
        if (max <= 0) return null;
        var ratio = (double)used / max;
        return ratio switch
        {
            >= 0.90 => Severity.High,
            >= 0.75 => Severity.Medium,
            _ => null,
        };
    }

    public static Severity DeadTupleSeverity(long dead, long live) =>
        dead > 100_000 && dead > live ? Severity.High : Severity.Medium;

    public static Severity MeanQueryTimeSeverity(double meanMs) => meanMs switch
    {
        > 1000 => Severity.High,
        > 250 => Severity.Medium,
        _ => Severity.Info,
    };
}
