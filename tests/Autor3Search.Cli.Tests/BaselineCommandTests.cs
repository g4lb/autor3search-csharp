using Autor3Search.Cli;
using Autor3Search.Core;
using Autor3Search.Core.Freezing;
using Autor3Search.Core.Processes;
using Autor3Search.Core.RunState;
using Xunit;

namespace Autor3Search.Cli.Tests;

/// <summary>
/// Serialises test classes that mutate the process-wide state-home environment
/// variable. xUnit parallelises across classes by default, so without this two such
/// classes would intermittently corrupt each other's environment.
/// </summary>
[CollectionDefinition("StateHomeEnv", DisableParallelization = true)]
public sealed class StateHomeEnvCollection;

/// <summary>Tests for the <c>baseline</c> command: freezing, pinning, and refusals.</summary>
[Collection("StateHomeEnv")]
public sealed class BaselineCommandTests : IDisposable
{
    private readonly string _repo;
    private readonly string _stateHome;

    /// <summary>Creates a fresh temporary git repository and state home for each test.</summary>
    public BaselineCommandTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"a3s-bl-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(Path.GetTempPath(), $"a3s-blstate-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, _stateHome);

        TestRepo.CreateDemoGitRepo(_repo);
    }

    /// <summary>Restores the environment and removes the temporary repository and state home.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, null);
        TestRepo.DeleteTree(_repo);
        TestRepo.DeleteTree(_stateHome);
    }

    private async Task<(int Code, string Out, string Err)> RunBaseline(params string[] extra)
    {
        var argv = new List<string> { "baseline", "-C", _repo };
        argv.AddRange(extra);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await BaselineCommand.RunAsync(
            Args.Parse(argv.ToArray()), stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>baseline creates and checks out the run branch.</summary>
    [Fact]
    public async Task BaselineCreatesTheRunBranch()
    {
        var (code, _, err) = await RunBaseline("-tag", "sep8");
        Assert.Equal(0, code);
        Assert.Equal("", err);

        Assert.Equal("autor3search-csharp/sep8",
            await Core.SourceControl.Git.CurrentBranchAsync(_repo, CancellationToken.None));
    }

    /// <summary>baseline freezes every test and benchmark file, but no source file.</summary>
    [Fact]
    public async Task BaselineFreezesTestAndBenchmarkFiles()
    {
        await RunBaseline("-tag", "sep8");

        var store = new StateStore(_repo, "sep8");
        var manifest = Manifest.Load(store.ManifestPath);

        Assert.Contains("tests/Demo.Tests/WordCountTests.cs", manifest.Files.Keys);
        Assert.Contains("tests/Demo.Tests/Demo.Tests.csproj", manifest.Files.Keys);
        Assert.Contains("bench/Demo.Benchmarks/WordCountBench.cs", manifest.Files.Keys);
        Assert.DoesNotContain("src/Demo/WordCount.cs", manifest.Files.Keys);
    }

    /// <summary>baseline pins a detached worktree at the baseline commit.</summary>
    [Fact]
    public async Task BaselinePinsAWorktreeAtTheBaselineCommit()
    {
        await RunBaseline("-tag", "sep8");

        var store = new StateStore(_repo, "sep8");
        var baseline = store.LoadBaseline();

        Assert.True(Directory.Exists(store.WorktreePath));
        Assert.Equal(baseline.Commit,
            await Core.SourceControl.Git.HeadCommitAsync(store.WorktreePath, CancellationToken.None));
    }

    /// <summary>The measurement commit starts equal to the frozen anchor commit.</summary>
    [Fact]
    public async Task TheMeasurementCommitStartsEqualToTheFrozenAnchor()
    {
        await RunBaseline("-tag", "sep8");

        var baseline = new StateStore(_repo, "sep8").LoadBaseline();
        Assert.Equal(baseline.Commit, baseline.MeasureCommit);
    }

    /// <summary>baseline records the SHA-256 of config.yaml at the time it ran.</summary>
    [Fact]
    public async Task BaselineRecordsTheConfigHash()
    {
        await RunBaseline("-tag", "sep8");

        var baseline = new StateStore(_repo, "sep8").LoadBaseline();
        var expected = Freezer.HashFile(
            Path.Combine(_repo, ".autor3search", "config.yaml"));

        Assert.Equal(expected, baseline.ConfigSha256);
    }

    // A baseline pinned against what is on disk rather than what is in git would not
    // be reproducible, and nothing downstream could be trusted.
    /// <summary>baseline refuses a working tree with uncommitted changes, including untracked files.</summary>
    [Fact]
    public async Task BaselineRefusesADirtyTree()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "Demo", "WordCount.cs"), "// uncommitted");

        var (code, _, err) = await RunBaseline("-tag", "sep8");

        Assert.Equal(2, code);
        Assert.Contains("uncommitted", err, StringComparison.OrdinalIgnoreCase);
        Assert.False(new StateStore(_repo, "sep8").Exists);
    }

    /// <summary>baseline refuses a tag that was already used for a baseline in this repository.</summary>
    [Fact]
    public async Task BaselineRefusesAReusedTag()
    {
        await RunBaseline("-tag", "sep8");

        var (code, _, err) = await RunBaseline("-tag", "sep8");

        Assert.Equal(2, code);
        Assert.Contains("sep8", err);
        Assert.Contains("already", err, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>baseline refuses to run without a -tag flag.</summary>
    [Fact]
    public async Task BaselineRequiresATag()
    {
        var (code, _, err) = await RunBaseline();
        Assert.Equal(2, code);
        Assert.Contains("-tag", err);
    }

    /// <summary>baseline refuses a tag that cannot be part of a git branch name.</summary>
    [Fact]
    public async Task BaselineRefusesATagThatCannotBeABranchName()
    {
        var (code, _, err) = await RunBaseline("-tag", "has spaces");
        Assert.Equal(2, code);
        Assert.Contains("tag", err, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>baseline refuses to run when .autor3search/config.yaml is missing.</summary>
    [Fact]
    public async Task BaselineRefusesWhenNoConfigExists()
    {
        File.Delete(Path.Combine(_repo, ".autor3search", "config.yaml"));

        var (code, _, err) = await RunBaseline("-tag", "sep8");

        Assert.Equal(2, code);
        Assert.Contains("init", err);
    }

    /// <summary>baseline reports the run branch and the frozen file count on stdout.</summary>
    [Fact]
    public async Task BaselineReportsWhatItPinned()
    {
        var (_, output, _) = await RunBaseline("-tag", "sep8");

        Assert.Contains("autor3search-csharp/sep8", output);
        Assert.Contains("frozen", output, StringComparison.OrdinalIgnoreCase);
    }

    // ".." passes the character-class regex but is a path segment: StateDir combines
    // it onto the state root, so it resolves ABOVE the per-repository hash directory
    // into the state home shared by every repository on the machine.
    /// <summary>baseline refuses ".." as a tag and creates no state directory for it.</summary>
    [Fact]
    public async Task BaselineRefusesADotDotTag()
    {
        var (code, _, err) = await RunBaseline("-tag", "..");

        Assert.Equal(2, code);
        Assert.Contains("path segment", err, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_stateHome));
    }

    // "." passes the same regex and would collapse every tag in a repository into one
    // directory.
    /// <summary>baseline refuses "." as a tag and creates no state directory for it.</summary>
    [Fact]
    public async Task BaselineRefusesADotTag()
    {
        var (code, _, err) = await RunBaseline("-tag", ".");

        Assert.Equal(2, code);
        Assert.Contains("path segment", err, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_stateHome));
    }

    // Git.CreateBranchAsync (`git checkout -b`) both creates and checks out the run
    // branch. A failure later in the same command — here, freezing a file that turns
    // out to be a symlink — must not strand the repository on that branch: baseline
    // must check the original branch back out and delete the run branch, so a
    // same-tag retry after fixing the problem is not blocked by "branch already
    // exists" telling the user the wrong thing.
    /// <summary>
    /// A failure while freezing checks the original branch back out and deletes the
    /// stray run branch, rather than leaving the repository stranded on it.
    /// </summary>
    [Fact]
    public async Task BaselineCleansUpTheRunBranchWhenFreezingFails()
    {
        if (!SymlinkSupported()) return;

        var target = Path.Combine(_repo, "outside.cs");
        File.WriteAllText(target, "elsewhere");

        var link = Path.Combine(_repo, "tests", "Demo.Tests", "WordCountTests.cs");
        File.Delete(link);
        File.CreateSymbolicLink(link, target);

        // Commit the swap so the tree is clean again — otherwise baseline would
        // refuse at the dirty-tree guard rather than reach freezing.
        Git("add", "-A");
        Git("commit", "-q", "-m", "swap a frozen file for a symlink");

        var (code, _, err) = await RunBaseline("-tag", "sep8");

        Assert.Equal(2, code);
        Assert.Contains("symbolic link", err, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("main",
            await Core.SourceControl.Git.CurrentBranchAsync(_repo, CancellationToken.None));
        Assert.False(await Core.SourceControl.Git.BranchExistsAsync(
            _repo, "autor3search-csharp/sep8", CancellationToken.None));
    }

    private void Git(params string[] args)
    {
        var runner = new Runner(_repo, TimeSpan.FromMinutes(2), null);
        var r = runner.RunAsync("git", args, CancellationToken.None).GetAwaiter().GetResult();
        if (!r.OK) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Tail(10)}");
    }

    // Matches the probe used elsewhere in this repository (e.g. FreezerTests):
    // symlink creation can fail for permission reasons on some machines (notably
    // Windows without developer mode), and a test that depends on it must skip rather
    // than fail in that environment.
    private static bool SymlinkSupported()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"a3s-baseline-symlink-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "target.txt");
            File.WriteAllBytes(target, "x"u8.ToArray());
            var link = Path.Combine(dir, "link.txt");
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
