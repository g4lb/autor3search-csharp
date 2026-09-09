using Autor3Search.Cli;
using Autor3Search.Core;
using Autor3Search.Core.Reporting;
using Autor3Search.Core.RunState;
using Xunit;

namespace Autor3Search.Cli.Tests;

/// <summary>Tests for the <c>status</c> and <c>stop</c> commands.</summary>
[Collection("StateHomeEnv")]
public sealed class StatusStopCommandTests : IDisposable
{
    private readonly string _repo;
    private readonly string _stateHome;

    /// <summary>Creates a fresh temporary git repository and state home, and records a baseline for it.</summary>
    public StatusStopCommandTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"a3s-ss-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(Path.GetTempPath(), $"a3s-ssstate-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, _stateHome);

        TestRepo.CreateDemoGitRepo(_repo);
        Run(BaselineCommand.RunAsync, "baseline", "-tag", "sep8");
    }

    /// <summary>Restores the environment and removes the temporary repository and state home.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, null);
        TestRepo.DeleteTree(_repo);
        TestRepo.DeleteTree(_stateHome);
    }

    /// <summary>Shape shared by the command entry points under test.</summary>
    private delegate Task<int> CommandFunc(Args a, TextWriter o, TextWriter e, CancellationToken ct);

    private (int Code, string Out, string Err) Run(CommandFunc f, params string[] argv)
    {
        var all = new List<string> { argv[0], "-C", _repo };
        all.AddRange(argv.Skip(1));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = f(Args.Parse(all.ToArray()), stdout, stderr, CancellationToken.None)
            .GetAwaiter().GetResult();

        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>status finds the run from the current branch and reports it as checked out.</summary>
    [Fact]
    public void StatusReportsTheRunItFindsFromTheBranch()
    {
        var (code, output, _) = Run(StatusCommand.RunAsync, "status");

        Assert.Equal(0, code);
        Assert.Contains("sep8", output);
        Assert.Contains("autor3search-csharp/sep8", output);
        Assert.Contains("checked out", output);
    }

    /// <summary>status shows both the frozen baseline commit and the commit being measured against.</summary>
    [Fact]
    public void StatusShowsBothTheFrozenAndMeasurementCommits()
    {
        var baseline = new StateStore(_repo, "sep8").LoadBaseline();
        var (_, output, _) = Run(StatusCommand.RunAsync, "status");

        Assert.Contains(baseline.Commit[..7], output);
        Assert.Contains("measuring vs", output);
    }

    /// <summary>status tallies recorded experiments by verdict and reports the next experiment number.</summary>
    [Fact]
    public void StatusCountsExperimentsByVerdict()
    {
        var results = Path.Combine(_repo, ResultsFile.RelativePath);
        ResultsFile.Append(results, new ResultsRow(1, DateTimeOffset.UtcNow, "aaa", "KEEP", "improved", 0.5, "m", "1.0.0"));
        ResultsFile.Append(results, new ResultsRow(2, DateTimeOffset.UtcNow, "bbb", "DISCARD", "no_significant_improvement", 1.0, "m", "1.0.0"));

        var (_, output, _) = Run(StatusCommand.RunAsync, "status");

        Assert.Contains("2 run", output);
        Assert.Contains("1 keep", output);
        Assert.Contains("1 discard", output);
        Assert.Contains("next is #3", output);
    }

    /// <summary>status works from a different branch when given an explicit tag.</summary>
    [Fact]
    public void StatusWorksFromAnotherBranchWithAnExplicitTag()
    {
        RunGit("checkout", "-q", "main");
        var (code, output, _) = Run(StatusCommand.RunAsync, "status", "-tag", "sep8");

        Assert.Equal(0, code);
        Assert.Contains("sep8", output);
        Assert.DoesNotContain("checked out", output);
    }

    // Checking on a run must never change it. A status command with a side effect
    // would make the act of looking part of the experiment.
    /// <summary>status is read-only: the state directory is byte-identical before and after.</summary>
    [Fact]
    public void StatusWritesNothing()
    {
        var store = new StateStore(_repo, "sep8");
        var before = Snapshot(store.Root);

        Run(StatusCommand.RunAsync, "status");

        Assert.Equal(before, Snapshot(store.Root));
    }

    /// <summary>status reports that no stop has been requested when none has.</summary>
    [Fact]
    public void StatusReportsNoPendingStop()
    {
        var (_, output, _) = Run(StatusCommand.RunAsync, "status");
        Assert.Contains("not requested", output);
    }

    /// <summary>stop with no flags writes a graceful stop request, not a forced one.</summary>
    [Fact]
    public void StopWritesAGracefulRequest()
    {
        var (code, output, _) = Run(StopCommand.RunAsync, "stop");

        Assert.Equal(0, code);
        Assert.True(new StateStore(_repo, "sep8").StopRequested);
        Assert.False(new StateStore(_repo, "sep8").ForceStopRequested);
        Assert.Contains("after the current experiment", output);
    }

    /// <summary>status reflects a pending stop request written by stop.</summary>
    [Fact]
    public void StatusReflectsAPendingStop()
    {
        Run(StopCommand.RunAsync, "stop");
        var (_, output, _) = Run(StatusCommand.RunAsync, "status");

        Assert.Contains("requested", output);
        Assert.DoesNotContain("not requested", output);
    }

    /// <summary>stop -clear cancels a previously requested stop.</summary>
    [Fact]
    public void StopClearCancelsAPendingRequest()
    {
        Run(StopCommand.RunAsync, "stop");
        var (code, _, _) = Run(StopCommand.RunAsync, "stop", "-clear");

        Assert.Equal(0, code);
        Assert.False(new StateStore(_repo, "sep8").StopRequested);
    }

    /// <summary>stop -force writes both the graceful and forced stop markers.</summary>
    [Fact]
    public void StopForceWritesBothMarkers()
    {
        var (code, output, _) = Run(StopCommand.RunAsync, "stop", "-force");

        var store = new StateStore(_repo, "sep8");
        Assert.Equal(0, code);
        Assert.True(store.StopRequested);
        Assert.True(store.ForceStopRequested);
        Assert.Contains("abandon", output, StringComparison.OrdinalIgnoreCase);
    }

    // A signal-based stop would have to behave differently on Windows, which has no
    // process-to-process SIGTERM: a forced stop there would end eval outright, before it
    // could record what it abandoned. Polling a marker file instead makes the message the
    // same everywhere, so there is no platform caveat to explain.
    /// <summary>stop -force says the same thing on every platform, with no Windows caveat.</summary>
    [Fact]
    public void StopForceSaysTheSameThingOnEveryPlatform()
    {
        var (_, output, _) = Run(StopCommand.RunAsync, "stop", "-force");

        Assert.DoesNotContain("Windows", output);
        Assert.DoesNotContain("immediate rather than", output);
    }

    /// <summary>stop reports failure and the missing tag when no run is recorded for it.</summary>
    [Fact]
    public void StopReportsWhenNoRunExists()
    {
        RunGit("checkout", "-q", "main");
        var (code, _, err) = Run(StopCommand.RunAsync, "stop", "-tag", "nosuchtag");

        Assert.Equal(2, code);
        Assert.Contains("nosuchtag", err);
    }

    private void RunGit(params string[] args)
    {
        var runner = new Core.Processes.Runner(_repo, TimeSpan.FromMinutes(1), null);
        runner.RunAsync("git", args, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string Snapshot(string dir) =>
        string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("baseline-worktree"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{f}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f):O}"));
}
