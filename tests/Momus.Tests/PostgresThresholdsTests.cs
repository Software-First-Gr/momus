using Momus.Core;
using Momus.Postgres;
using Xunit;

namespace Momus.Tests;

public class PostgresThresholdsTests
{
    [Theory]
    [InlineData(0.999, null)]
    [InlineData(0.98, Severity.Low)]
    [InlineData(0.94, Severity.Medium)]
    [InlineData(0.85, Severity.High)]
    public void Cache_hit_ratio_maps_to_expected_severity(double ratio, Severity? expected)
    {
        Assert.Equal(expected, PostgresThresholds.CacheHitSeverity(ratio, totalBlocks: 1_000_000));
    }

    [Fact]
    public void Cache_hit_ratio_is_ignored_with_too_few_samples()
    {
        // A terrible ratio over a handful of reads is noise, not a finding.
        Assert.Null(PostgresThresholds.CacheHitSeverity(0.50, totalBlocks: 100));
    }

    [Theory]
    [InlineData(50, 100, null)]
    [InlineData(75, 100, Severity.Medium)]
    [InlineData(95, 100, Severity.High)]
    public void Connection_saturation_maps_to_expected_severity(long used, long max, Severity? expected)
    {
        Assert.Equal(expected, PostgresThresholds.ConnectionSaturationSeverity(used, max));
    }

    [Fact]
    public void Connection_saturation_handles_zero_max_without_dividing()
    {
        Assert.Null(PostgresThresholds.ConnectionSaturationSeverity(10, 0));
    }

    [Theory]
    [InlineData(20_000, 50_000, Severity.Medium)]     // many dead but fewer than live
    [InlineData(200_000, 50_000, Severity.High)]      // dead outnumber live at scale
    public void Dead_tuples_map_to_expected_severity(long dead, long live, Severity expected)
    {
        Assert.Equal(expected, PostgresThresholds.DeadTupleSeverity(dead, live));
    }
}
