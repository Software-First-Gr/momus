using Momus.Core.Ingest;

namespace Momus.Tests;

/// <summary>
/// A percentile rebuilt from twelve log2 buckets. It has to be approximate; it must not invent a
/// number — the demo once said a 700 ms transaction was open for 1,024 ms.
/// </summary>
public class TimingTests
{
    [Fact]
    public void Everything_in_one_bucket_reads_as_what_was_measured_not_as_the_bucket_s_edge()
    {
        var timing = Many(700, count: 29, max: 760);

        Assert.Equal(760, timing.Percentile(0.95), 1);
    }

    [Fact]
    public void A_percentile_is_interpolated_within_its_bucket()
    {
        // 90 fast samples and 10 in the 512–1024 bucket: the 95th is halfway through that bucket.
        var hist = new long[Timing.Buckets];
        hist[Timing.BucketFor(1.5)] = 90;
        hist[Timing.BucketFor(700)] = 10;

        Assert.Equal(768, new Timing(0, 900, hist).Percentile(0.95), 1);
    }

    [Fact]
    public void The_last_bucket_reaches_the_largest_value_seen()
    {
        var timing = Many(3000, count: 100, max: 3000);

        Assert.Equal(1024 + (3000 - 1024) * 0.95, timing.Percentile(0.95), 1);
    }

    [Fact]
    public void No_samples_is_zero() =>
        Assert.Equal(0, new Timing(0, 0, new long[Timing.Buckets]).Percentile(0.95));

    private static Timing Many(double ms, long count, double max)
    {
        var hist = new long[Timing.Buckets];
        hist[Timing.BucketFor(ms)] = count;
        return new Timing(ms * count, max, hist);
    }
}
