using Autor3Search.Core.Bench;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for parsing BenchmarkDotNet JSON reports into observations.</summary>
public class BdnReportTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "testdata", "sample-report-full-compressed.json");

    /// <summary>Verifies that Parse returns one observation per benchmark in the report.</summary>
    [Fact]
    public void ParseReadsEveryBenchmark()
    {
        var obs = BdnReport.Parse(File.ReadAllText(FixturePath));
        Assert.Equal(2, obs.Count);
    }

    /// <summary>Verifies that Parse uses FullName, including parameters, as the benchmark identity.</summary>
    [Fact]
    public void ParseUsesFullNameAsIdentity()
    {
        var obs = BdnReport.Parse(File.ReadAllText(FixturePath));
        Assert.Contains(obs, o => o.FullName == "WordCountBench.CountWords");
        // Parameterized benchmarks keep their parameters in the identity: two
        // parameterizations of one method are two distinct benchmarks to compare.
        Assert.Contains(obs, o => o.FullName == "Demo.SplitBench.Split(Size=1024)");
    }

    /// <summary>Verifies that Parse reads Statistics.Mean as nanoseconds without conversion.</summary>
    [Fact]
    public void ParseReadsMeanInNanoseconds()
    {
        var obs = BdnReport.Parse(File.ReadAllText(FixturePath));
        var wc = obs.Single(o => o.FullName == "WordCountBench.CountWords");
        Assert.Equal(57340.2858581543, wc.MeanNs, 6);
    }

    /// <summary>Verifies that Parse reads Memory.BytesAllocatedPerOperation.</summary>
    [Fact]
    public void ParseReadsAllocatedBytes()
    {
        var obs = BdnReport.Parse(File.ReadAllText(FixturePath));
        var wc = obs.Single(o => o.FullName == "WordCountBench.CountWords");
        Assert.Equal(290568, wc.BytesPerOp);
    }

    /// <summary>Verifies that Parse rejects a report whose Benchmarks array is empty.</summary>
    [Fact]
    public void ParseRejectsAReportWithNoBenchmarks()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BdnReport.Parse("""{"Benchmarks":[]}"""));
        Assert.Contains("no benchmarks", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies that Parse rejects malformed JSON rather than swallowing it.</summary>
    [Fact]
    public void ParseRejectsMalformedJson()
    {
        Assert.ThrowsAny<Exception>(() => BdnReport.Parse("{ not json"));
    }

    // A benchmark whose Mean is missing cannot be scored. Silently treating it as
    // zero would make it look infinitely fast and poison the geomean.
    /// <summary>Verifies that Parse rejects a benchmark with no Statistics.Mean and names it.</summary>
    [Fact]
    public void ParseRejectsABenchmarkWithNoMean()
    {
        var json = """
            {"Benchmarks":[{"FullName":"X.Y","Statistics":{"N":3},"Memory":{}}]}
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => BdnReport.Parse(json));
        Assert.Contains("X.Y", ex.Message);
    }

    /// <summary>Verifies that ParseDirectory finds a report file nested under results/.</summary>
    [Fact]
    public void ParseDirectoryFindsTheReportUnderResults()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"a3s-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "results"));
        File.Copy(FixturePath, Path.Combine(dir, "results", "Some-report-full-compressed.json"));
        try
        {
            Assert.Equal(2, BdnReport.ParseDirectory(dir).Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Verifies that ParseDirectory merges every matching report file, not just the first.</summary>
    [Fact]
    public void ParseDirectoryMergesMultipleReportFiles()
    {
        // BenchmarkDotNet writes one report per benchmark TYPE. A run whose filter
        // matches benchmarks in two classes produces two files, and dropping either
        // would silently shrink the benchmark set mid-run.
        var dir = Path.Combine(Path.GetTempPath(), $"a3s-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "results"));
        File.WriteAllText(Path.Combine(dir, "results", "A-report-full-compressed.json"),
            """{"Benchmarks":[{"FullName":"A.One","Statistics":{"Mean":10.0},"Memory":{}}]}""");
        File.WriteAllText(Path.Combine(dir, "results", "B-report-full-compressed.json"),
            """{"Benchmarks":[{"FullName":"B.Two","Statistics":{"Mean":20.0},"Memory":{}}]}""");
        try
        {
            var obs = BdnReport.ParseDirectory(dir);
            Assert.Equal(2, obs.Count);
            Assert.Contains(obs, o => o.FullName == "A.One");
            Assert.Contains(obs, o => o.FullName == "B.Two");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Verifies that ParseDirectory throws a clear error when no report file was written.</summary>
    [Fact]
    public void ParseDirectoryFailsWhenNoReportWasWritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"a3s-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => BdnReport.ParseDirectory(dir));
            Assert.Contains("no BenchmarkDotNet JSON report", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }
}
