using Autor3Search.Core.Bench;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for the two-sided Mann-Whitney U test.</summary>
public class MannWhitneyTests
{
    /// <summary>Two identical samples must not be flagged as significantly different.</summary>
    [Fact]
    public void IdenticalSamplesAreNotSignificant()
    {
        double[] a = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        double[] b = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var r = MannWhitney.Test(a, b);
        Assert.True(r.P > 0.05);
    }

    /// <summary>Two completely separated samples hit the combinatorial floor for the given sample sizes.</summary>
    [Fact]
    public void CompletelySeparatedSamplesGiveTheMinimumAttainableP()
    {
        double[] a = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        double[] b = [101, 102, 103, 104, 105, 106, 107, 108, 109, 110];
        var r = MannWhitney.Test(a, b);

        // With 10 per side the floor is 2/C(20,10) = 2/184756.
        Assert.True(r.UsedExact);
        Assert.Equal(2.0 / 184756.0, r.P, 12);
        Assert.Equal(2.0 / 184756.0, MannWhitney.MinimumAttainableP(10, 10), 12);
    }

    // These four floors are exactly why RunConfig refuses count < 4: at 3 per side
    // the best achievable p is 0.1, so no experiment could ever clear alpha = 0.05.
    /// <summary>The minimum attainable p matches the exact combinatorial floor 2/C(2n, n).</summary>
    [Theory]
    [InlineData(2, 2, 2.0 / 6.0)]
    [InlineData(3, 3, 2.0 / 20.0)]
    [InlineData(4, 4, 2.0 / 70.0)]
    [InlineData(5, 5, 2.0 / 252.0)]
    public void MinimumAttainablePMatchesTheCombinatorialFloor(int n1, int n2, double expected)
    {
        Assert.Equal(expected, MannWhitney.MinimumAttainableP(n1, n2), 12);
    }

    /// <summary>Swapping the two samples must not change the resulting p-value.</summary>
    [Fact]
    public void TheTestIsSymmetricInItsArguments()
    {
        double[] a = [10, 12, 14, 16, 18, 20];
        double[] b = [11, 13, 15, 17, 19, 40];
        Assert.Equal(MannWhitney.Test(a, b).P, MannWhitney.Test(b, a).P, 12);
    }

    /// <summary>The p-value must never exceed 1.0.</summary>
    [Fact]
    public void PIsNeverAboveOne()
    {
        double[] a = [1, 2, 3, 4, 5, 6];
        double[] b = [1, 2, 3, 4, 5, 6];
        Assert.True(MannWhitney.Test(a, b).P <= 1.0);
    }

    // Ties break the exact distribution's assumption of a unique ranking, so the
    // implementation must fall back to the tie-corrected normal approximation
    // rather than silently reporting an exact p that is wrong.
    /// <summary>Tied values force the tie-corrected normal approximation rather than the exact distribution.</summary>
    [Fact]
    public void TiesForceTheNormalApproximation()
    {
        double[] a = [1, 1, 1, 2, 2, 2, 3, 3, 3, 3];
        double[] b = [1, 1, 2, 2, 3, 3, 4, 4, 5, 5];
        var r = MannWhitney.Test(a, b);
        Assert.False(r.UsedExact);
        Assert.InRange(r.P, 0.0, 1.0);

        // The tie warning is the only warning MannWhitney ever emits, and it is
        // surfaced to the agent and to results.tsv — it must actually be there, not
        // just implied by UsedExact being false.
        Assert.Contains(r.Warnings, w => w.Contains("tied", StringComparison.Ordinal));
    }

    /// <summary>Samples above the exact-distribution size cap fall back to the normal approximation.</summary>
    [Fact]
    public void LargeSamplesUseTheNormalApproximation()
    {
        var a = Enumerable.Range(0, 40).Select(i => (double)i).ToArray();
        var b = Enumerable.Range(0, 40).Select(i => i + 0.5).ToArray();
        var r = MannWhitney.Test(a, b);
        Assert.False(r.UsedExact);
        Assert.Equal(40, r.N1);
        Assert.Equal(40, r.N2);
    }

    /// <summary>A consistent, overlap-free improvement at ten observations per side is significant.</summary>
    [Fact]
    public void ASolidImprovementIsSignificantAtTenPerSide()
    {
        // Shape of a real KEEP: candidate consistently faster, with overlap-free spread.
        double[] baseNs = [1000, 1010, 1020, 1005, 1015, 1030, 1002, 1008, 1012, 1025];
        double[] candNs = [600, 610, 620, 605, 615, 630, 602, 608, 612, 625];
        var r = MannWhitney.Test(baseNs, candNs);
        Assert.True(r.P < 0.05);
    }

    /// <summary>Overlapping, noisy samples with no real difference are not significant.</summary>
    [Fact]
    public void NoiseIsNotSignificant()
    {
        double[] baseNs = [1000, 1010, 990, 1005, 995, 1015, 985, 1020, 1000, 1002];
        double[] candNs = [1001, 1009, 991, 1004, 996, 1014, 986, 1019, 999, 1003];
        var r = MannWhitney.Test(baseNs, candNs);
        Assert.True(r.P > 0.05);
    }

    /// <summary>Fewer than two observations on either side is rejected.</summary>
    [Fact]
    public void FewerThanTwoObservationsIsRejected()
    {
        Assert.Throws<ArgumentException>(() => MannWhitney.Test([1.0], [1.0, 2.0]));
    }

    /// <summary>An unbalanced pair of completely separated samples hits its own combinatorial floor.</summary>
    [Fact]
    public void UnbalancedCompletelySeparatedSamplesGiveTheMinimumAttainableP()
    {
        double[] a = [1, 2];
        double[] b = [101, 102, 103, 104, 105, 106, 107, 108, 109, 110];
        var r = MannWhitney.Test(a, b);

        // With n1=2, n2=10 the floor is 2/C(12,2) = 2/66.
        Assert.True(r.UsedExact);
        Assert.Equal(2.0 / 66.0, r.P, 12);
        Assert.Equal(2.0 / 66.0, MannWhitney.MinimumAttainableP(2, 10), 12);
    }

    /// <summary>Two samples with zero spread produce p = 1.0 via the variance-guard, not NaN or a crash.</summary>
    [Fact]
    public void AllIdenticalValuesGiveExactlyOne()
    {
        double[] a = [5, 5, 5, 5];
        double[] b = [5, 5, 5, 5];
        var r = MannWhitney.Test(a, b);
        Assert.False(r.UsedExact);
        Assert.Equal(1.0, r.P);
    }

    // A NaN reaching the ranking step sorts unpredictably and never compares equal to
    // itself, so it would silently avoid registering as a tie and let the exact path
    // return a confidently wrong p with no warning. It must be rejected up front.
    /// <summary>A NaN in the first sample is rejected rather than silently mis-scored.</summary>
    [Fact]
    public void NonFiniteValueInFirstSampleIsRejected()
    {
        double[] a = [1, 2, double.NaN, 4];
        double[] b = [1, 2, 3, 4];
        Assert.Throws<ArgumentException>(() => MannWhitney.Test(a, b));
    }

    /// <summary>A NaN in the second sample is rejected rather than silently mis-scored.</summary>
    [Fact]
    public void NonFiniteValueInSecondSampleIsRejected()
    {
        double[] a = [1, 2, 3, 4];
        double[] b = [1, 2, double.NaN, 4];
        Assert.Throws<ArgumentException>(() => MannWhitney.Test(a, b));
    }
}
