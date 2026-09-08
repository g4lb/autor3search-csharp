using Autor3Search.Core;
using Autor3Search.Core.Configuration;
using Autor3Search.Core.Processes;
using Autor3Search.Core.SourceControl;

namespace Autor3Search.Cli;

/// <summary>Runs the declared benchmarks under a profiler and reports where the output went.</summary>
internal static class ProfileCommand
{
    /// <summary>Runs `profile`. Returns 0 on success, 2 on failure.</summary>
    public static async Task<int> RunAsync(
        Args args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var start = Path.GetFullPath(args.GetString("C") ?? Directory.GetCurrentDirectory());
        var repo = await Git.RootAsync(start, ct);

        var configPath = Path.Combine(repo, Paths.FromSlash(RunConfig.RelativePath));
        if (!File.Exists(configPath))
        {
            stderr.WriteLine($"no {RunConfig.RelativePath} — run 'autor3search-csharp init' first");
            return 2;
        }

        var config = RunConfig.Load(configPath);
        var outputDir = Path.Combine(repo, ".autor3search", "profiles");
        Directory.CreateDirectory(outputDir);

        var filter = config.Benchmarks.Count == 0 ? "*" : string.Join("|", config.Benchmarks);
        var runner = new Runner(repo, config.TimeoutDuration, stdout);

        // EventPipe (EP), not ETW: EP is the only profiler BenchmarkDotNet offers that
        // works on macOS, Linux and Windows alike, so `profile` behaves identically
        // everywhere rather than being a Windows-only feature with a footnote.
        var result = await runner.RunAsync("dotnet",
        [
            "run", "-c", "Release",
            "--project", Path.Combine(repo, Paths.FromSlash(config.BenchmarkProject)),
            "--",
            "--filter", filter,
            "--job", config.Job,
            "--profiler", "EP",
            "--memory",
            "--artifacts", outputDir,
        ], ct);

        if (result.TimedOut)
        {
            stderr.WriteLine($"profiling timed out after {config.TimeoutDuration}");
            return 2;
        }

        if (!result.OK)
        {
            stderr.WriteLine($"profiling failed (exit {result.ExitCode}):\n{result.Tail(30)}");
            return 2;
        }

        var traces = Directory.GetFiles(outputDir, "*.nettrace", SearchOption.AllDirectories);

        stdout.WriteLine();
        stdout.WriteLine($"profiles written to {Paths.ToSlash(Path.GetRelativePath(repo, outputDir))}");

        foreach (var t in traces)
            stdout.WriteLine($"  {Paths.ToSlash(Path.GetRelativePath(repo, t))}");

        if (traces.Length > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine("open one with speedscope (https://speedscope.app), PerfView, or Visual Studio.");
            stdout.WriteLine("Real profile data beats an agent guessing at hot spots from reading source.");
        }

        return 0;
    }
}
