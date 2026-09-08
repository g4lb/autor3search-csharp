using Autor3Search.Cli;
using Autor3Search.Core.Reporting;
using Xunit;

namespace Autor3Search.Cli.Tests;

/// <summary>Tests for <see cref="ReportCommand"/>.</summary>
public sealed class ReportCommandTests : IDisposable
{
    private readonly string _repo;

    /// <summary>Creates a fresh, empty repository directory for each test.</summary>
    public ReportCommandTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"a3s-rep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
    }

    /// <summary>Removes the repository directory.</summary>
    public void Dispose() => TestRepo.DeleteTree(_repo);

    private void AddRow(int n, string status, double score) =>
        ResultsFile.Append(Path.Combine(_repo, ResultsFile.RelativePath),
            new ResultsRow(n, DateTimeOffset.UtcNow, $"c{n:D7}", status,
                status == "KEEP" ? "improved" : "no_significant_improvement",
                score, $"score {score:F4}", "1.0.0"));

    private (int Code, string Out, string Err) Run()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = ReportCommand.RunAsync(
            Args.Parse(["report", "-C", _repo]), stdout, stderr, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>The report counts each status.</summary>
    [Fact]
    public void ReportCountsEachStatus()
    {
        AddRow(1, "KEEP", 0.5);
        AddRow(2, "DISCARD", 1.0);
        AddRow(3, "FAIL", 0.0);

        var (code, output, _) = Run();

        Assert.Equal(0, code);
        Assert.Contains("3", output);
        Assert.Contains("KEEP", output);
        Assert.Contains("DISCARD", output);
        Assert.Contains("FAIL", output);
    }

    /// <summary>The cumulative figure is the product of every kept score, not the latest.</summary>
    [Fact]
    public void ReportShowsTheCompoundedCumulativeSpeedup()
    {
        AddRow(1, "KEEP", 0.5);
        AddRow(2, "KEEP", 0.5);

        var (_, output, _) = Run();

        // 0.5 * 0.5 = 0.25, i.e. 4.00x faster overall.
        Assert.Contains("0.2500", output);
        Assert.Contains("4.00", output);
    }

    /// <summary>The biggest wins are listed best (lowest score) first.</summary>
    [Fact]
    public void ReportListsTheBiggestWinsBestFirst()
    {
        AddRow(1, "KEEP", 0.9);
        AddRow(2, "KEEP", 0.3);

        var (_, output, _) = Run();

        var best = output.IndexOf("0.3000", StringComparison.Ordinal);
        var worst = output.IndexOf("0.9000", StringComparison.Ordinal);
        Assert.True(best >= 0 && worst >= 0 && best < worst);
    }

    /// <summary>An empty ledger says so rather than printing zeros.</summary>
    [Fact]
    public void ReportOnAnEmptyLedgerSaysSoRatherThanPrintingZeros()
    {
        var (code, output, _) = Run();
        Assert.Equal(0, code);
        Assert.Contains("no experiments", output, StringComparison.OrdinalIgnoreCase);
    }
}
