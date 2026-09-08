using Autor3Search.Core.Reporting;

namespace Autor3Search.Cli;

/// <summary>Summarizes results.tsv.</summary>
internal static class ReportCommand
{
    /// <summary>Runs `report`. Always returns 0.</summary>
    public static async Task<int> RunAsync(
        Args args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        await Task.CompletedTask;
        _ = stderr;
        _ = ct;

        var repo = Path.GetFullPath(args.GetString("C") ?? Directory.GetCurrentDirectory());
        var rows = ResultsFile.Load(Path.Combine(repo, ResultsFile.RelativePath));

        if (rows.Count == 0)
        {
            stdout.WriteLine("no experiments recorded yet.");
            return 0;
        }

        var s = ResultsFile.Summarize(rows);

        stdout.WriteLine($"experiments    {s.Total}");
        stdout.WriteLine($"  KEEP         {s.Keeps}");
        stdout.WriteLine($"  DISCARD      {s.Discards}");
        stdout.WriteLine($"  FAIL         {s.Fails}");
        stdout.WriteLine($"  CRASH        {s.Crashes}");
        stdout.WriteLine();

        // The product, not the latest score: the measurement baseline advances after
        // every KEEP, so each kept score is its own incremental contribution and
        // successive wins compound.
        var speedup = s.CumulativeSpeedup > 0 ? 1.0 / s.CumulativeSpeedup : 0.0;
        stdout.WriteLine($"cumulative     {s.CumulativeSpeedup:F4}  ({speedup:F2}x faster overall)");

        if (s.BiggestWins.Count > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine("biggest wins:");
            foreach (var w in s.BiggestWins.Take(10))
            {
                stdout.WriteLine(
                    $"  #{w.Experiment,-4} {w.Score:F4}  ({(w.Score - 1) * 100:+0.0;-0.0}%)  {w.Commit[..Math.Min(7, w.Commit.Length)]}");
            }
        }

        return 0;
    }
}
