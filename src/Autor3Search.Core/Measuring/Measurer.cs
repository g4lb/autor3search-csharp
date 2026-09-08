using Autor3Search.Core.Bench;
using Autor3Search.Core.Processes;

namespace Autor3Search.Core.Measuring;

/// <summary>Configuration for one full measurement of two worktrees.</summary>
/// <param name="BaseDir">Baseline worktree root.</param>
/// <param name="CandDir">Candidate worktree root — the repository itself.</param>
/// <param name="BenchmarkProject">Repo-relative path to the benchmark project.</param>
/// <param name="Filter">BenchmarkDotNet --filter glob.</param>
/// <param name="Job">BenchmarkDotNet job: dry, short, medium, long or default.</param>
/// <param name="InProcess">Runs benchmarks in the host process, skipping BDN's project generation.</param>
/// <param name="Rounds">Measured rounds per side.</param>
/// <param name="Warmup">Adds one discarded leading round.</param>
/// <param name="Timeout">Bounds each build and each round.</param>
/// <param name="Log">Receives subprocess output. May be null.</param>
public sealed record MeasureOptions(
    string BaseDir,
    string CandDir,
    string BenchmarkProject,
    string Filter,
    string Job,
    bool InProcess,
    int Rounds,
    bool Warmup,
    TimeSpan Timeout,
    TextWriter? Log);

/// <summary>Measures two worktrees against each other with real BenchmarkDotNet runs.</summary>
public static class Measurer
{
    /// <summary>Builds each side once, then interleaves the measured rounds.</summary>
    public static async Task<(BenchSet Base, BenchSet Cand)> RunAsync(
        MeasureOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Filter))
            throw new ArgumentException("empty benchmark filter", nameof(o));

        // Build ONCE per side, not per round. Doing it per round would put MSBuild on
        // the clock and its variance into the measurement.
        var baseDir = await BuildAsync(o.BaseDir, o, ct).ConfigureAwait(false);
        var candDir = await BuildAsync(o.CandDir, o, ct).ConfigureAwait(false);

        try
        {
            var assemblyName = Path.GetFileNameWithoutExtension(o.BenchmarkProject);
            var baseExe = Path.Combine(baseDir, assemblyName + ".dll");
            var candExe = Path.Combine(candDir, assemblyName + ".dll");

            return await Interleaver.RunAsync(
                o.Rounds, o.Warmup,
                Side(o.BaseDir, baseExe, o), Side(o.CandDir, candExe, o),
                ct).ConfigureAwait(false);
        }
        finally
        {
            // Every measurement builds two full Release outputs. This tool is meant to be
            // invoked repeatedly in an unattended optimization loop, so leaving these behind
            // would leak gigabytes of /tmp per night — exactly what `doctor`'s disk-space
            // check exists to catch, and exactly what would trip it.
            DeleteQuietly(baseDir);
            DeleteQuietly(candDir);
        }
    }

    private static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (IOException) { /* a leftover scratch dir is not worth failing a completed run over */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    /// <summary>
    /// Publishes the benchmark project to a fresh scratch directory and returns it. The
    /// caller owns this directory for the lifetime of the measurement and must delete it —
    /// it survives across all rounds, so it cannot be cleaned up here.
    /// </summary>
    private static async Task<string> BuildAsync(string worktree, MeasureOptions o, CancellationToken ct)
    {
        var outputDir = Path.Combine(
            Path.GetTempPath(), $"a3s-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        var project = Path.Combine(worktree, Paths.FromSlash(o.BenchmarkProject));
        var runner = new Runner(worktree, o.Timeout, o.Log);

        var result = await runner.RunAsync("dotnet",
            ["build", project, "-c", "Release", "-o", outputDir, "--nologo"], ct)
            .ConfigureAwait(false);

        if (result.TimedOut)
            throw new InvalidOperationException($"building the benchmark project in {worktree} timed out after {o.Timeout}");

        if (!result.OK)
            throw new InvalidOperationException(
                $"building the benchmark project in {worktree} failed (exit {result.ExitCode}):\n{result.Tail(30)}");

        var assemblyName = Path.GetFileNameWithoutExtension(o.BenchmarkProject);
        var dll = Path.Combine(outputDir, assemblyName + ".dll");

        if (!File.Exists(dll))
            throw new InvalidOperationException($"benchmark build produced no {assemblyName}.dll in {outputDir}");

        return outputDir;
    }

    private static RoundFunc Side(string worktree, string dll, MeasureOptions o) =>
        async (round, ct) =>
        {
            var artifacts = Path.Combine(Path.GetTempPath(), $"a3s-round-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);

            try
            {
                var args = new List<string>
                {
                    dll,
                    "--filter", o.Filter,
                    "--job", o.Job,
                    "--memory",
                    "--exporters", "JSON",
                    "--artifacts", artifacts,
                };

                if (o.InProcess) args.Add("--inProcess");

                // The Runner's WORKING DIRECTORY is load-bearing for correctness, not a
                // convenience for relative paths.
                //
                // BenchmarkDotNet's default toolchain does NOT execute the assembly named on
                // the command line — that Main() only performs reflection-based discovery. To
                // run a job it does a fresh `dotnet build` of the benchmark project it finds by
                // searching UPWARD FROM THE PROCESS'S CURRENT WORKING DIRECTORY, and runs that.
                //
                // So `worktree` below is what decides which source tree is actually measured.
                // Running both sides from a shared directory would make them rebuild the SAME
                // project and measure identical code — every experiment would compare a worktree
                // against itself, report "no significant improvement" forever, and look
                // completely healthy while doing it. Do not "simplify" this.
                var runner = new Runner(worktree, o.Timeout, o.Log);
                var result = await runner.RunAsync("dotnet", args, ct).ConfigureAwait(false);

                if (result.TimedOut)
                    throw new InvalidOperationException(
                        $"benchmark round timed out after {o.Timeout} in {worktree}");

                if (!result.OK)
                    throw new InvalidOperationException(
                        $"benchmark round failed in {worktree} (exit {result.ExitCode}):\n{result.Tail(30)}");

                var observations = BdnReport.ParseDirectory(artifacts);

                if (observations.Count == 0)
                    throw new InvalidOperationException(
                        $"no benchmarks matched \"{o.Filter}\" in {worktree}");

                return observations;
            }
            finally
            {
                try { Directory.Delete(artifacts, true); }
                catch (IOException) { /* a leftover scratch dir is not worth failing a run over */ }
            }
        };
}
