using System.Globalization;
using Momus.Core;
using Xunit;

namespace Momus.Tests;

public class ProseTests
{
    [Theory]
    [InlineData(0, "0 calls")]
    [InlineData(1, "1 call")]
    [InlineData(2910, "2,910 calls")]
    public void A_count_agrees_with_its_noun(long n, string expected) =>
        Assert.Equal(expected, Prose.Count(n, "call"));

    [Fact]
    public void An_irregular_plural_can_be_given() =>
        Assert.Equal("2 queries", Prose.Count(2, "query", "queries"));

    [Theory]
    [InlineData(0.017, "0.02 ms")]
    [InlineData(0.4, "0.4 ms")]
    [InlineData(7.5, "7.5 ms")]
    [InlineData(140, "140 ms")]
    [InlineData(4_700, "4.7 s")]
    [InlineData(192_000, "3.2 min")]
    [InlineData(5_400_000, "1.5 h")]
    public void A_total_is_given_in_the_unit_a_person_would_say(double ms, string expected) =>
        Assert.Equal(expected, Prose.Millis(ms));

    [Theory]
    [InlineData(42, "42 s")]
    [InlineData(304, "5 min 4 s")]
    [InlineData(4_320, "1 h 12 min")]
    [InlineData(86_400, "1 day 0 h")]
    [InlineData(273_600, "3 days 4 h")]
    [InlineData(-5, "0 s")]
    public void A_duration_reads_as_words_not_a_clock(int seconds, string expected) =>
        Assert.Equal(expected, Prose.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Numbers_read_as_English_wherever_the_server_runs()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("el-GR");
            Assert.Equal("2,910 calls", Prose.Count(2910, "call"));
            Assert.Equal("4.7 s", Prose.Millis(4_700));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
