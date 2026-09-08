using Autor3Search.Core.Pipelines;
using Autor3Search.Core.Verdicts;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>
/// Every gate that rejects a candidate BEFORE measurement, so the suite stays fast.
/// Each test sets up a real git repository with a real baseline, introduces exactly
/// one violation, and asserts the verdict.
/// </summary>
[Trait("Category", "Gates")]
[Collection("StateHomeEnv")]
public sealed class PipelineGateTests : IDisposable
{
    private readonly GateHarness _h = new();

    /// <summary>Disposes the harness, restoring the state-home environment variable.</summary>
    public void Dispose() => _h.Dispose();

    /// <summary>An edit outside the configured scope fails with Reasons.Scope.</summary>
    [Fact]
    public async Task AnEditOutsideScopeFails()
    {
        _h.WriteAndCommit("unrelated/Thing.cs", "// not in scope");

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Fail, outcome.Verdict.Status);
        Assert.Equal(Reasons.Scope, outcome.Verdict.Reason);
        Assert.Contains("unrelated/Thing.cs", outcome.Verdict.Message);
    }

    /// <summary>A dependency file change fails even though the path is otherwise in scope.</summary>
    [Fact]
    public async Task ADependencyChangeFailsEvenWhenInScope()
    {
        _h.WriteAndCommit("Directory.Packages.props", "<Project></Project>");

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Fail, outcome.Verdict.Status);
        Assert.Equal(Reasons.DependencyChanged, outcome.Verdict.Reason);
    }

    /// <summary>A changed config.yaml fails the run with Reasons.ConfigChanged.</summary>
    [Fact]
    public async Task AChangedConfigFailsTheRun()
    {
        _h.WriteAndCommit(".autor3search/config.yaml",
            File.ReadAllText(_h.ConfigPath) + "\nmax_regress_pct: 90.0\n");

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Fail, outcome.Verdict.Status);
        Assert.Equal(Reasons.ConfigChanged, outcome.Verdict.Reason);
    }

    /// <summary>A weakened frozen test is silently restored rather than argued with.</summary>
    [Fact]
    public async Task AWeakenedTestIsSilentlyRestoredRatherThanArguedWith()
    {
        var testFile = "tests/Demo.Tests/WordCountTests.cs";
        var original = _h.Read(testFile);

        _h.WriteAndCommit(testFile, "namespace Demo.Tests; public class WordCountTests { }");

        await _h.EvalAsync();

        Assert.Equal(original, _h.Read(testFile));
    }

    /// <summary>A brand new frozen test file fails with Reasons.NewFrozenFile.</summary>
    [Fact]
    public async Task ANewTestFileFails()
    {
        _h.WriteAndCommit("tests/Demo.Tests/EasierTests.cs",
            "namespace Demo.Tests; public class Easier { }");

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Fail, outcome.Verdict.Status);
        Assert.Equal(Reasons.NewFrozenFile, outcome.Verdict.Reason);
        Assert.Contains("EasierTests.cs", outcome.Verdict.Message);
    }

    /// <summary>A brand new benchmark file fails with Reasons.NewFrozenFile.</summary>
    [Fact]
    public async Task ANewBenchmarkFileFails()
    {
        _h.WriteAndCommit("bench/Demo.Benchmarks/EasyBench.cs", """
            using BenchmarkDotNet.Attributes;
            namespace Demo.Benchmarks;
            public class EasyBench { [Benchmark] public int Nothing() => 1; }
            """);

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Fail, outcome.Verdict.Status);
        Assert.Equal(Reasons.NewFrozenFile, outcome.Verdict.Reason);
    }

    // Restore rewrites the frozen file, so a manifest entry the walker can no longer
    // see means the tree changed shape around it — the file is nominally restored but
    // would not actually run.
    /// <summary>A frozen file hidden behind a dot-prefixed directory fails with Reasons.MissingFrozenFile.</summary>
    [Fact(Skip = "Under investigation — see 'Reachability of Reasons.MissingFrozenFile' in " +
        "task-18-report.md. Moving the whole project directory relocates its .csproj, which " +
        "trips the dependency gate (Reasons.DependencyChanged) before the manifest-agreement " +
        "check this test targets is ever reached. Skipped rather than weakened so the original " +
        "intent is visible pending a ruling on whether MissingFrozenFile's 'missing' direction " +
        "is reachable at all given Freezer.Restore's self-healing recreate.")]
    public async Task AFrozenFileHiddenFromDiscoveryFails()
    {
        _h.MoveDirectory("tests/Demo.Tests", "tests/.hidden.Demo.Tests");

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Fail, outcome.Verdict.Status);
        Assert.Equal(Reasons.MissingFrozenFile, outcome.Verdict.Reason);
    }

    /// <summary>A code change that breaks the build crashes the run.</summary>
    [Fact]
    public async Task ACodeChangeThatBreaksTheBuildCrashes()
    {
        _h.WriteAndCommit("src/Demo/WordCount.cs", "this is not valid C#");

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Crash, outcome.Verdict.Status);
        Assert.Equal(Reasons.Build, outcome.Verdict.Reason);
    }

    // The whole point: an agent cannot buy speed with correctness.
    /// <summary>A code change that breaks the frozen tests fails with Reasons.Tests.</summary>
    [Fact]
    public async Task ACodeChangeThatBreaksTheFrozenTestsFails()
    {
        _h.WriteAndCommit("src/Demo/WordCount.cs", """
            namespace Demo;
            public static class WordCount
            {
                public static Dictionary<string, int> CountWords(string s) => new();
            }
            """);

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Fail, outcome.Verdict.Status);
        Assert.Equal(Reasons.Tests, outcome.Verdict.Reason);
    }

    /// <summary>A baseline worktree that moved off its recorded commit fails with Reasons.BaselineTampered.</summary>
    [Fact]
    public async Task AMovedBaselineWorktreeFails()
    {
        await _h.MoveBaselineWorktreeOffItsCommitAsync();

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Fail, outcome.Verdict.Status);
        Assert.Equal(Reasons.BaselineTampered, outcome.Verdict.Reason);
    }

    /// <summary>A tampered frozen store fails with Reasons.FrozenTampered.</summary>
    [Fact]
    public async Task ATamperedFrozenStoreFails()
    {
        _h.TamperFrozenStore();

        var outcome = await _h.EvalAsync();

        Assert.Equal(VerdictStatus.Fail, outcome.Verdict.Status);
        Assert.Equal(Reasons.FrozenTampered, outcome.Verdict.Reason);
    }
}
