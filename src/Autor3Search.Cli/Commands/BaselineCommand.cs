using System.Text.RegularExpressions;
using Autor3Search.Core;
using Autor3Search.Core.Configuration;
using Autor3Search.Core.Discovery;
using Autor3Search.Core.Freezing;
using Autor3Search.Core.RunState;
using Autor3Search.Core.SourceControl;

namespace Autor3Search.Cli;

/// <summary>Creates the run branch, freezes the test and benchmark files, pins the baseline.</summary>
internal static partial class BaselineCommand
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex ValidTag();

    /// <summary>Runs `baseline -tag &lt;tag&gt;`. Returns 0 on success, 2 on any refusal.</summary>
    public static async Task<int> RunAsync(
        Args args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var tag = args.GetString("tag");
        if (string.IsNullOrWhiteSpace(tag))
        {
            stderr.WriteLine("baseline needs a run tag: autor3search-csharp baseline -tag <tag>");
            return 2;
        }

        if (!ValidTag().IsMatch(tag))
        {
            stderr.WriteLine(
                $"tag \"{tag}\" cannot be part of a git branch name. Use letters, digits, " +
                "dots, dashes and underscores.");
            return 2;
        }

        var start = Path.GetFullPath(args.GetString("C") ?? Directory.GetCurrentDirectory());
        var repo = await Git.RootAsync(start, ct);

        var configPath = Path.Combine(repo, Paths.FromSlash(RunConfig.RelativePath));
        if (!File.Exists(configPath))
        {
            stderr.WriteLine(
                $"no {RunConfig.RelativePath} — run 'autor3search-csharp init' first, then commit it");
            return 2;
        }

        var config = RunConfig.Load(configPath);

        var store = new StateStore(repo, tag);
        if (store.Exists)
        {
            stderr.WriteLine(
                $"a baseline for tag \"{tag}\" already exists at {store.Root}. " +
                "Tags are not reusable: reusing one would score a new run against another " +
                "run's frozen tests. Pick a different tag.");
            return 2;
        }

        var branch = StateStore.BranchForTag(tag);
        if (await Git.BranchExistsAsync(repo, branch, ct))
        {
            stderr.WriteLine($"branch {branch} already exists. Pick a different tag.");
            return 2;
        }

        // A baseline pinned against what is on disk rather than what is in git would
        // not be reproducible, so nothing downstream could be trusted.
        if (!await Git.IsCleanAsync(repo, ct))
        {
            stderr.WriteLine(
                "the working tree has uncommitted changes. baseline pins a commit, so it " +
                "refuses a dirty tree — a baseline that depended on unversioned edits could " +
                "never be reproduced.\n\n  git add -A && git commit -m \"autor3search-csharp init\"");
            return 2;
        }

        var frozenProjects = new List<string>(config.TestProjects) { config.BenchmarkProject };
        var frozenFiles = Discoverer.FrozenFiles(repo, frozenProjects, config.Unfreeze);

        if (frozenFiles.Count == 0)
        {
            stderr.WriteLine(
                "nothing to freeze: no files were found in the configured test and benchmark " +
                "projects. Check test_projects and benchmark_project in " + RunConfig.RelativePath);
            return 2;
        }

        var commit = await Git.HeadCommitAsync(repo, ct);

        Directory.CreateDirectory(store.Root);
        await Git.CreateBranchAsync(repo, branch, ct);

        Manifest manifest;
        try
        {
            manifest = Freezer.Snapshot(repo, store.FrozenStorePath, frozenFiles);
        }
        catch (SymlinkRefusedException ex)
        {
            stderr.WriteLine(ex.Message);
            return 2;
        }

        manifest.Save(store.ManifestPath);

        await Git.AddWorktreeAsync(repo, store.WorktreePath, commit, ct);

        store.SaveBaseline(new Baseline
        {
            Tag = tag,
            Commit = commit,
            MeasureCommit = commit,
            ConfigSha256 = Freezer.HashFile(configPath),
            CreatedAt = DateTimeOffset.UtcNow,
            FrozenProjects = frozenProjects,
            BenchmarkProject = config.BenchmarkProject,
        });

        stdout.WriteLine($"run tag        {tag}");
        stdout.WriteLine($"branch         {branch}  (checked out)");
        stdout.WriteLine($"baseline       {commit[..7]}  (run starts here)");
        stdout.WriteLine($"frozen         {frozenFiles.Count} file(s) across {frozenProjects.Count} project(s)");
        stdout.WriteLine($"worktree       {store.WorktreePath}");
        stdout.WriteLine($"state          {store.Root}");
        stdout.WriteLine();
        stdout.WriteLine("next: point your agent at program.md and start the loop");

        return 0;
    }
}
