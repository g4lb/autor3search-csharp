using Autor3Search.Core.Reporting;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for the results ledger file format and operations.</summary>
public sealed class ResultsFileTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"a3s-results-{Guid.NewGuid():N}.tsv");

    /// <summary>Clean up the test file after each test.</summary>
    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ResultsRow Row(int n, string status, double score, string reason = "improved") =>
        new(n, DateTimeOffset.Parse("2026-09-08T10:00:00+00:00"), $"abc{n:D4}", status, reason,
            score, $"score {score:F4}", "1.0.0");

    /// <summary>Verifies that Append writes a header once, then rows.</summary>
    [Fact]
    public void AppendWritesAHeaderOnceThenRows()
    {
        ResultsFile.Append(_path, Row(1, "KEEP", 0.5));
        ResultsFile.Append(_path, Row(2, "DISCARD", 1.01));

        var lines = File.ReadAllLines(_path);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("experiment\t", lines[0]);
        Assert.Contains("KEEP", lines[1]);
        Assert.Contains("DISCARD", lines[2]);
    }

    /// <summary>Verifies that rows round-trip correctly through disk.</summary>
    [Fact]
    public void RowsRoundTripThroughDisk()
    {
        ResultsFile.Append(_path, Row(1, "KEEP", 0.4763));
        var loaded = ResultsFile.Load(_path);

        Assert.Single(loaded);
        Assert.Equal(1, loaded[0].Experiment);
        Assert.Equal("KEEP", loaded[0].Status);
        Assert.Equal(0.4763, loaded[0].Score, 6);
        Assert.Equal("abc0001", loaded[0].Commit);
    }

    /// <summary>Verifies that tabs and newlines in messages are flattened to prevent column corruption.</summary>
    [Fact]
    public void TabsAndNewlinesInAMessageDoNotCorruptColumns()
    {
        var row = Row(1, "FAIL", 0) with { Message = "line one\twith tab\nand newline" };
        ResultsFile.Append(_path, row);

        Assert.Equal(2, File.ReadAllLines(_path).Length);

        var loaded = ResultsFile.Load(_path).Single();
        Assert.Equal("FAIL", loaded.Status);
        Assert.DoesNotContain('\t', loaded.Message);
    }

    /// <summary>Verifies that loading a missing file returns an empty list, not an error.</summary>
    [Fact]
    public void LoadOfAMissingFileIsEmptyNotAnError()
    {
        Assert.Empty(ResultsFile.Load(Path.Combine(Path.GetTempPath(), "a3s-nope.tsv")));
    }

    /// <summary>Verifies that the next experiment number starts at one for an empty ledger.</summary>
    [Fact]
    public void NextExperimentNumberStartsAtOne()
    {
        Assert.Equal(1, ResultsFile.NextExperimentNumber(_path));
    }

    /// <summary>Verifies that the next experiment number follows the highest recorded.</summary>
    [Fact]
    public void NextExperimentNumberFollowsTheHighestRecorded()
    {
        ResultsFile.Append(_path, Row(1, "KEEP", 0.5));
        ResultsFile.Append(_path, Row(2, "DISCARD", 1.0));
        Assert.Equal(3, ResultsFile.NextExperimentNumber(_path));
    }

    /// <summary>Verifies that Summarize counts each status correctly.</summary>
    [Fact]
    public void SummarizeCountsEachStatus()
    {
        var rows = new[]
        {
            Row(1, "KEEP", 0.9), Row(2, "DISCARD", 1.0),
            Row(3, "FAIL", 0), Row(4, "CRASH", 0), Row(5, "KEEP", 0.8),
        };
        var s = ResultsFile.Summarize(rows);

        Assert.Equal(5, s.Total);
        Assert.Equal(2, s.Keeps);
        Assert.Equal(1, s.Discards);
        Assert.Equal(1, s.Fails);
        Assert.Equal(1, s.Crashes);
    }

    /// <summary>Verifies that cumulative speedup is the product of kept scores, not the latest one.</summary>
    [Fact]
    public void CumulativeSpeedupIsTheProductOfKeptScores()
    {
        var rows = new[] { Row(1, "KEEP", 0.5), Row(2, "KEEP", 0.5), Row(3, "DISCARD", 1.2) };
        Assert.Equal(0.25, ResultsFile.Summarize(rows).CumulativeSpeedup, 9);
    }

    /// <summary>Verifies that cumulative speedup with no keeps is one.</summary>
    [Fact]
    public void CumulativeSpeedupOfNoKeepsIsOne()
    {
        Assert.Equal(1.0, ResultsFile.Summarize([Row(1, "DISCARD", 1.1)]).CumulativeSpeedup, 9);
    }

    /// <summary>Verifies that biggest wins are kept rows ordered by score ascending (best first).</summary>
    [Fact]
    public void BiggestWinsAreKeptRowsOrderedByScoreAscending()
    {
        var rows = new[]
        {
            Row(1, "KEEP", 0.9), Row(2, "KEEP", 0.4),
            Row(3, "DISCARD", 0.1), Row(4, "KEEP", 0.7),
        };
        var wins = ResultsFile.Summarize(rows).BiggestWins;

        Assert.Equal([2, 4, 1], wins.Select(w => w.Experiment));   // DISCARD excluded
    }
}
