using Autor3Search.Core.Bench;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for <see cref="Stats"/>: deltas, comparison, and the geomean score.</summary>
public class StatsTests
{
    private static BenchSet SetOf(string name, double[] ns)
    {
        var set = new BenchSet();
        foreach (var v in ns) set.Add([new Observation(name, v, 1024)]);
        return set;
    }

    private static BenchSet SetOfMany(params (string Name, double[] Ns)[] series)
    {
        var set = new BenchSet();
        var rounds = series[0].Ns.Length;
        for (var round = 0; round < rounds; round++)
        {
            set.Add(series.Select(s => new Observation(s.Name, s.Ns[round], 1024)).ToArray());
        }
        return set;
    }

    /// <summary>Compare computes the ratio and percent change from the medians.</summary>
    [Fact]
    public void CompareComputesRatioAndPercentChange()
    {
        var b = SetOf("A.B", [1000, 1000, 1000, 1000, 1000, 1000]);
        var c = SetOf("A.B", [500, 500, 500, 500, 500, 500]);

        var d = Stats.Compare(b, c, "A.B", Units.TimeNs);
        Assert.Equal(0.5, d.Ratio, 9);
        Assert.Equal(-50.0, d.PctChange, 9);
        Assert.Equal(1000.0, d.BaseCenter);
        Assert.Equal(500.0, d.CandCenter);
        Assert.Equal(6, d.NBase);
        Assert.Equal(6, d.NCand);
    }

    /// <summary>A large, consistent improvement is marked significant.</summary>
    [Fact]
    public void CompareMarksASolidImprovementSignificant()
    {
        var b = SetOf("A.B", [1000, 1010, 1020, 1005, 1015, 1030, 1002, 1008, 1012, 1025]);
        var c = SetOf("A.B", [600, 610, 620, 605, 615, 630, 602, 608, 612, 625]);

        var d = Stats.Compare(b, c, "A.B", Units.TimeNs);
        Assert.True(d.Significant);
        Assert.True(d.P < 0.05);
    }

    /// <summary>Overlapping noisy samples are not marked significant.</summary>
    [Fact]
    public void CompareDoesNotMarkNoiseSignificant()
    {
        var b = SetOf("A.B", [1000, 1010, 990, 1005, 995, 1015, 985, 1020, 1000, 1002]);
        var c = SetOf("A.B", [1001, 1009, 991, 1004, 996, 1014, 986, 1019, 999, 1003]);

        var d = Stats.Compare(b, c, "A.B", Units.TimeNs);
        Assert.False(d.Significant);
    }

    /// <summary>Underpowered-sample warnings from the summaries reach the caller.</summary>
    [Fact]
    public void CompareCarriesUnderpoweredWarningsOutward()
    {
        // 4 rounds per side: legal for a run, but below the 6 a median confidence
        // interval needs. The warning must reach the caller rather than be dropped.
        var b = SetOf("A.B", [1000, 1010, 1020, 1005]);
        var c = SetOf("A.B", [600, 610, 620, 605]);

        var d = Stats.Compare(b, c, "A.B", Units.TimeNs);
        Assert.NotEmpty(d.Warnings);
    }

    /// <summary>Identical warnings from both sides are deduplicated.</summary>
    [Fact]
    public void CompareDeduplicatesIdenticalWarnings()
    {
        // Both sides warn about the same sample size in the same words.
        var b = SetOf("A.B", [1000, 1010, 1020, 1005]);
        var c = SetOf("A.B", [600, 610, 620, 605]);

        var d = Stats.Compare(b, c, "A.B", Units.TimeNs);
        Assert.Equal(d.Warnings.Distinct().Count(), d.Warnings.Count);
    }

    /// <summary>Comparing a benchmark absent from either set throws.</summary>
    [Fact]
    public void CompareRejectsAMissingBenchmark()
    {
        var b = SetOf("A.B", [1, 2, 3, 4]);
        var c = SetOf("Other.X", [1, 2, 3, 4]);
        Assert.Throws<InvalidOperationException>(() => Stats.Compare(b, c, "A.B", Units.TimeNs));
    }

    /// <summary>A zero baseline median cannot form a ratio and throws.</summary>
    [Fact]
    public void CompareRejectsAZeroBaselineMedian()
    {
        var b = SetOf("A.B", [0, 0, 0, 0]);
        var c = SetOf("A.B", [1, 2, 3, 4]);
        var ex = Assert.Throws<InvalidOperationException>(() => Stats.Compare(b, c, "A.B", Units.TimeNs));
        Assert.Contains("ratio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>CompareAll returns one delta per benchmark, sorted by name.</summary>
    [Fact]
    public void CompareAllReturnsOneDeltaPerBenchmarkSortedByName()
    {
        var b = SetOfMany(("Z.Two", [100, 100, 100, 100]), ("A.One", [200, 200, 200, 200]));
        var c = SetOfMany(("Z.Two", [50, 50, 50, 50]), ("A.One", [100, 100, 100, 100]));

        var deltas = Stats.CompareAll(b, c, Units.TimeNs);
        Assert.Equal(2, deltas.Count);
        Assert.Equal("A.One", deltas[0].Name);
        Assert.Equal("Z.Two", deltas[1].Name);
    }

    // A benchmark that vanishes from the candidate cannot be checked for regressions,
    // which is exactly the outcome an agent deleting an inconvenient benchmark wants.
    /// <summary>A benchmark missing from the candidate is rejected, naming the benchmark.</summary>
    [Fact]
    public void CompareAllRejectsABenchmarkMissingFromTheCandidate()
    {
        var b = SetOfMany(("A.One", [100, 100, 100, 100]), ("B.Two", [100, 100, 100, 100]));
        var c = SetOf("A.One", [50, 50, 50, 50]);

        var ex = Assert.Throws<InvalidOperationException>(() => Stats.CompareAll(b, c, Units.TimeNs));
        Assert.Contains("B.Two", ex.Message);
        Assert.Contains("disappears", ex.Message);
    }

    /// <summary>An empty intersection between baseline and candidate is rejected.</summary>
    [Fact]
    public void CompareAllRejectsAnEmptyIntersection()
    {
        var b = SetOf("A.One", [1, 2, 3, 4]);
        var c = new BenchSet();
        Assert.Throws<InvalidOperationException>(() => Stats.CompareAll(b, c, Units.TimeNs));
    }

    /// <summary>The geomean of equal ratios equals that ratio.</summary>
    [Fact]
    public void GeoMeanOfEqualRatiosIsThatRatio()
    {
        var b = SetOfMany(("A.One", [100, 100, 100, 100]), ("B.Two", [200, 200, 200, 200]));
        var c = SetOfMany(("A.One", [50, 50, 50, 50]), ("B.Two", [100, 100, 100, 100]));

        Assert.Equal(0.5, Stats.GeoMean(Stats.CompareAll(b, c, Units.TimeNs)), 9);
    }

    /// <summary>GeoMean is the geometric mean of ratios, not the arithmetic mean.</summary>
    [Fact]
    public void GeoMeanIsTheGeometricNotArithmeticMean()
    {
        // Ratios 0.25 and 1.0: geometric mean 0.5, arithmetic mean 0.625.
        var b = SetOfMany(("A.One", [100, 100, 100, 100]), ("B.Two", [100, 100, 100, 100]));
        var c = SetOfMany(("A.One", [25, 25, 25, 25]), ("B.Two", [100, 100, 100, 100]));

        Assert.Equal(0.5, Stats.GeoMean(Stats.CompareAll(b, c, Units.TimeNs)), 9);
    }

    /// <summary>GeoMean of an empty delta set is rejected.</summary>
    [Fact]
    public void GeoMeanOfAnEmptySetIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Stats.GeoMean([]));
    }
}
