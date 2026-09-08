using System.Text;
using Autor3Search.Core;
using Autor3Search.Core.Configuration;
using Autor3Search.Core.Discovery;
using Autor3Search.Core.Reporting;
using Autor3Search.Core.Templates;

namespace Autor3Search.Cli;

/// <summary>Scans a repository and writes the run configuration and agent instructions.</summary>
internal static class InitCommand
{
    /// <summary>Runs `init`. Returns 0 on success, 2 on any refusal.</summary>
    public static async Task<int> RunAsync(
        Args args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        await Task.CompletedTask;

        var repo = Path.GetFullPath(args.GetString("C") ?? Directory.GetCurrentDirectory());
        var force = args.GetFlag("force");

        var benchmarks = Discoverer.Benchmarks(repo);
        if (benchmarks.Count == 0)
        {
            stderr.WriteLine(
                "no benchmarks found. This tool optimizes what it can measure, and it has no " +
                "other notion of \"faster\" — every verdict is a function of the declared " +
                "benchmark timings.\n\n" +
                "To use it here, add a BenchmarkDotNet project with at least one [Benchmark]\n" +
                "method covering the code you want made faster, then run init again:\n\n" +
                "  dotnet new console -o benchmarks\n" +
                "  dotnet add benchmarks package BenchmarkDotNet\n\n" +
                "  [MemoryDiagnoser]\n" +
                "  public class MyBench\n" +
                "  {\n" +
                "      [Benchmark] public void Thing() => Subject.Thing();\n" +
                "  }\n\n" +
                "Benchmark the path that actually dominates your workload. A benchmark of a\n" +
                "cold path produces numbers that are entirely real and entirely useless.");
            return 2;
        }

        var projects = Discoverer.Projects(repo);

        var benchProjects = projects.Where(p => p.IsBenchmarkProject).ToList();
        if (benchProjects.Count == 0)
        {
            stderr.WriteLine(
                "found [Benchmark] methods but no project referencing BenchmarkDotNet. " +
                "The benchmarks must live in a project that references the BenchmarkDotNet " +
                "package and whose Main calls BenchmarkSwitcher.");
            return 2;
        }

        if (benchProjects.Count > 1)
        {
            // Deliberately does NOT suggest editing config.yaml or re-running with -force:
            // this check fires before any config exists, init never reads an existing one,
            // and -force would hit this identical check and refuse again.
            stderr.WriteLine(
                "found more than one BenchmarkDotNet project:\n  " +
                string.Join("\n  ", benchProjects.Select(p => p.ProjectPath)) +
                "\n\nThis tool measures one benchmark project per run and cannot guess which " +
                "one you mean. Either point it at a subtree containing only the project you " +
                "want measured:\n\n  autor3search-csharp init -C <path to that subtree>\n\n" +
                "or remove the BenchmarkDotNet package reference from the projects you do not " +
                "want measured.");
            return 2;
        }

        var benchProject = benchProjects[0].ProjectPath;
        var testProjects = projects.Where(p => p.IsTestProject).Select(p => p.ProjectPath).ToList();

        var excluded = new HashSet<string>(testProjects, StringComparer.Ordinal) { benchProject };
        var sourceProjects = projects.Where(p => !excluded.Contains(p.ProjectPath)).ToList();

        if (sourceProjects.Count == 0)
        {
            stderr.WriteLine(
                "the repository contains no source project outside the test and benchmark " +
                "projects, so there is nothing for an agent to change. Every discovered " +
                "project is either the benchmark project (" + benchProject + ")" +
                (testProjects.Count > 0
                    ? " or a test project (" + string.Join(", ", testProjects) + ")"
                    : "") +
                ". Add a project holding the code you want optimized, and run init again.");
            return 2;
        }

        var configPath = Path.Combine(repo, Paths.FromSlash(RunConfig.RelativePath));
        if (File.Exists(configPath) && !force)
        {
            stderr.WriteLine(
                $"{RunConfig.RelativePath} already exists. It is yours, not the tool's, so init " +
                "will not overwrite it. Pass -force if you really want it regenerated.");
            return 2;
        }

        var (sourceScope, scopeWarning) = InferScope(sourceProjects);

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, RenderConfig(benchmarks.Select(b => b.FullName).ToList(),
            benchProject, testProjects, sourceScope));

        File.WriteAllText(
            Path.Combine(repo, "program.md"),
            ProgramTemplate.Render(benchmarks.Select(b => b.FullName).ToList(), benchProject));

        UpdateGitignore(repo);

        stdout.WriteLine($"benchmark project   {benchProject}");
        stdout.WriteLine($"test projects       {(testProjects.Count == 0 ? "(none)" : string.Join(", ", testProjects))}");
        stdout.WriteLine($"scope               {string.Join(", ", sourceScope)}");
        stdout.WriteLine();
        stdout.WriteLine($"discovered {benchmarks.Count} benchmark(s):");
        foreach (var b in benchmarks) stdout.WriteLine($"  {b.FullName}");
        stdout.WriteLine();
        stdout.WriteLine("wrote .autor3search/config.yaml and program.md");
        stdout.WriteLine("next: git add -A && git commit -m \"autor3search-csharp init\"");
        stdout.WriteLine("      autor3search-csharp doctor");
        stdout.WriteLine("      autor3search-csharp baseline -tag <today>");

        if (scopeWarning is not null)
        {
            stdout.WriteLine();
            stdout.WriteLine("WARNING: " + scopeWarning);
        }

        if (testProjects.Count == 0)
        {
            stdout.WriteLine();
            stdout.WriteLine(
                "WARNING: no test project was found. Correctness cannot be gated, so an agent " +
                "is free to make this code faster by making it wrong. Add tests before running " +
                "an unattended loop.");
        }

        return 0;
    }

    /// <summary>
    /// The scope glob patterns for the given non-excluded (non-test, non-benchmark)
    /// source projects, plus a warning to surface when the inferred scope is wider
    /// than a directory-scoped default — everything else is frozen, so scoping to
    /// source directories is both correct and the least surprising default.
    ///
    /// A project sitting at the repository root (<see cref="DiscoveredProject.Directory"/>
    /// is empty) cannot be excluded by any directory pattern — its own files live directly
    /// under the repo root alongside <c>.autor3search/</c>, <c>program.md</c> and every
    /// frozen project. The only correct scope for that layout is <c>**</c>, but that also
    /// matches the frozen projects' own directories, so this is called out loudly rather
    /// than written silently: a scope gate that fails open, with <c>program.md</c> telling
    /// the agent it is constrained, is exactly the failure this tool exists to prevent.
    /// </summary>
    private static (List<string> Scope, string? Warning) InferScope(
        IReadOnlyList<DiscoveredProject> sourceProjects)
    {
        if (sourceProjects.Any(p => p.Directory.Length == 0))
        {
            return (["**"],
                "the inferred scope is unrestricted (\"**\") because a source project sits at " +
                "the repository root, so nothing can be excluded by directory. The agent may " +
                "edit anything not frozen (the test and benchmark projects). If you want the " +
                "scope narrower, edit scope in .autor3search/config.yaml BY HAND before running " +
                "baseline — the config is hashed at baseline, and any change made after that " +
                "fails the run.");
        }

        var dirs = sourceProjects
            .Select(p => p.Directory)
            .Select(d => d.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(d => $"{d}/**")
            .ToList();

        return (dirs, null);
    }

    private static string RenderConfig(
        List<string> benchmarks, string benchProject, List<string> testProjects, List<string> scope)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# autor3search-c# run configuration.");
        sb.AppendLine("# Humans own this file. It is hashed when you run `baseline`, and any change");
        sb.AppendLine("# mid-run fails the run: the scoring rules are fixed for the whole run.");
        sb.AppendLine();
        sb.AppendLine("# The declared benchmark set, by BenchmarkDotNet FullName.");
        sb.AppendLine("benchmarks:");
        foreach (var b in benchmarks) sb.AppendLine($"  - {b}");
        sb.AppendLine();
        sb.AppendLine("# The project that declares the benchmarks.");
        sb.AppendLine($"benchmark_project: {benchProject}");
        sb.AppendLine();
        sb.AppendLine("# Test projects. Every file in these is frozen and restored before each eval.");
        sb.AppendLine("test_projects:");
        foreach (var t in testProjects) sb.AppendLine($"  - {t}");
        sb.AppendLine();
        sb.AppendLine("# What the agent may modify.");
        sb.AppendLine("scope:");
        foreach (var s in scope) sb.AppendLine($"  - \"{s}\"");
        sb.AppendLine();
        sb.AppendLine("# Measured rounds per side. Below 4 the significance test cannot reach p < 0.05");
        sb.AppendLine("# at all, so every experiment would be discarded on a technicality.");
        sb.AppendLine("count: 10");
        sb.AppendLine();
        sb.AppendLine("# BenchmarkDotNet job: dry, short, medium, long or default.");
        sb.AppendLine("job: short");
        sb.AppendLine();
        sb.AppendLine("# One leading round, measured and discarded, absorbing cold caches and tiered JIT.");
        sb.AppendLine("warmup: true");
        sb.AppendLine();
        sb.AppendLine("# Run benchmarks in the host process. Faster, less isolated. Leave false unless");
        sb.AppendLine("# you have measured the difference on your own machine and accepted it.");
        sb.AppendLine("in_process: false");
        sb.AppendLine();
        sb.AppendLine("# Largest tolerated significant regression on any single benchmark, percent.");
        sb.AppendLine("max_regress_pct: 5.0");
        sb.AppendLine();
        sb.AppendLine("# Smallest geomean improvement a KEEP will accept, percent. Raise this on a");
        sb.AppendLine("# noisy machine: it costs you only wins smaller than the noise floor.");
        sb.AppendLine("min_effect_pct: 1.0");
        sb.AppendLine();
        sb.AppendLine("# Bounds each subprocess phase.");
        sb.AppendLine("timeout: 15m");
        sb.AppendLine();
        sb.AppendLine("# Build the repository with warnings as errors. Off by default: most repositories");
        sb.AppendLine("# carry pre-existing warnings unrelated to anything the agent did.");
        sb.AppendLine("warnings_as_errors: false");
        sb.AppendLine();
        sb.AppendLine("# Files inside a frozen project that should NOT be frozen.");
        sb.AppendLine("unfreeze: []");

        return sb.ToString();
    }

    /// <summary>
    /// Adds the harness's own outputs to .gitignore, idempotently.
    ///
    /// config.yaml is deliberately un-ignored: it is the one file under .autor3search/
    /// that humans own and that belongs in version control.
    /// </summary>
    private static void UpdateGitignore(string repo)
    {
        var path = Path.Combine(repo, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];

        string[] wanted =
        [
            // ".autor3search/*", NOT ".autor3search/". Git cannot re-include a file whose
            // PARENT DIRECTORY is excluded, so the directory form silently defeats the
            // negation below and makes config.yaml — the one file here that humans own and
            // are told to commit — permanently untrackable. The trailing /* excludes the
            // directory's CONTENTS instead, which leaves the negation able to work.
            ".autor3search/*",
            "!.autor3search/config.yaml",
            ResultsFile.RelativePath,
            ResultsFile.RunLogName,
        ];

        var missing = wanted.Where(w => !existing.Any(l => l.Trim() == w)).ToList();
        if (missing.Count == 0) return;

        var sb = new StringBuilder();
        if (existing.Count > 0)
        {
            sb.AppendJoin('\n', existing);
            sb.AppendLine();
            sb.AppendLine();
        }

        sb.AppendLine("# autor3search-c# harness output");
        foreach (var m in missing) sb.AppendLine(m);

        File.WriteAllText(path, sb.ToString());
    }
}
