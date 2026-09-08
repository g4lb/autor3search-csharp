using Autor3Search.Core.Bench;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for the distribution-free median summary.</summary>
public class MedianEstimateTests
{
    /// <summary>The median of an odd-count sample is its middle value.</summary>
    [Fact]
    public void MedianOfOddCountIsTheMiddleValue()
    {
        var s = MedianEstimate.Summarize([3, 1, 2, 5, 4, 7, 6]);
        Assert.Equal(4.0, s.Center);
    }

    /// <summary>The median of an even-count sample averages the two middle values.</summary>
    [Fact]
    public void MedianOfEvenCountAveragesTheTwoMiddleValues()
    {
        var s = MedianEstimate.Summarize([1, 2, 3, 4, 5, 6]);
        Assert.Equal(3.5, s.Center);
    }

    // At 95% confidence a distribution-free interval needs 6 observations: the
    // tightest available bound is P(X = 0) = 1/2^n, which must be <= 0.025.
    // This is benchmath's own warning, surfaced rather than swallowed.
    /// <summary>Fewer than six observations cannot support a bounded 95% confidence interval.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void FewerThanSixObservationsGivesAnUnboundedInterval(int n)
    {
        var values = Enumerable.Range(1, n).Select(i => (double)i).ToArray();
        var s = MedianEstimate.Summarize(values);
        Assert.False(s.Bounded);
        Assert.Contains(s.Warnings, w => w.Contains("6", StringComparison.Ordinal));
    }

    /// <summary>Six observations is exactly enough for a bounded 95% confidence interval.</summary>
    [Fact]
    public void SixObservationsGivesABoundedInterval()
    {
        var s = MedianEstimate.Summarize([1, 2, 3, 4, 5, 6]);
        Assert.True(s.Bounded);
        Assert.Empty(s.Warnings);
        Assert.Equal(1.0, s.Lo);
        Assert.Equal(6.0, s.Hi);
    }

    /// <summary>The confidence interval always brackets the reported center.</summary>
    [Fact]
    public void TheIntervalBracketsTheCenter()
    {
        var s = MedianEstimate.Summarize([10, 12, 14, 16, 18, 20, 22, 24, 26, 30]);
        Assert.True(s.Bounded);
        Assert.True(s.Lo <= s.Center);
        Assert.True(s.Center <= s.Hi);
    }

    // Both samples span the SAME fixed interval [0, 1], sampled at increasing density.
    // Using 1..n instead would make the sample's own spread grow with n, so the
    // interval's absolute width would grow (7 at n=8, 13 at n=40) even though the
    // estimate is genuinely getting sharper — measuring the data's range rather than
    // the estimator's precision.
    /// <summary>More observations produce a narrower confidence interval, holding the sample's range fixed.</summary>
    [Fact]
    public void TheIntervalNarrowsAsObservationsAccumulate()
    {
        var small = MedianEstimate.Summarize(
            Enumerable.Range(0, 8).Select(i => i / 7.0).ToArray());
        var large = MedianEstimate.Summarize(
            Enumerable.Range(0, 40).Select(i => i / 39.0).ToArray());

        // n=8  -> k=1,  CI = [x(1), x(8)]   = [0.0000, 1.0000], width 1.0000
        // n=40 -> k=14, CI = [x(14), x(27)] = [0.3333, 0.6667], width 0.3333
        Assert.True(large.Bounded && small.Bounded);
        Assert.True(large.Hi - large.Lo < small.Hi - small.Lo,
            $"expected the interval to narrow: n=8 width {small.Hi - small.Lo:F4}, " +
            $"n=40 width {large.Hi - large.Lo:F4}");
    }

    /// <summary>An empty sample is rejected.</summary>
    [Fact]
    public void EmptyInputIsRejected()
    {
        Assert.Throws<ArgumentException>(() => MedianEstimate.Summarize([]));
    }
}
