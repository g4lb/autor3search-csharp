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

    // Several gates below sit behind a full `dotnet build` and `dotnet test` of the
    // fixture, so an earlier stage failing — a build race under parallel test
    // execution, a timeout — surfaces here as the wrong verdict rather than as the
    // gate under test. Two bare Assert.Equal calls report only "expected
    // baseline_tampered, got build" and discard the verdict message explaining why,
    // which is precisely the message needed to tell a real regression apart from
    // infrastructure noise. Assert both fields together and carry the message into
    // the failure, so an intermittent failure stays diagnosable from a CI log alone.
    private static void AssertGate(EvalOutcome outcome, VerdictStatus status, string reason)
    {
        var v = outcome.Verdict;
        Assert.True(v.Status == status && v.Reason == reason,
            $"expected {status}/{reason} but got {v.Status}/{v.Reason}: {v.Message}");
    }

    /// <summary>An edit outside the configured scope fails with Reasons.Scope.</summary>
    [Fact]
    public async Task AnEditOutsideScopeFails()
    {
        _h.WriteAndCommit("unrelated/Thing.cs", "// not in scope");

        var outcome = await _h.EvalAsync();

        AssertGate(outcome, VerdictStatus.Fail, Reasons.Scope);
        Assert.Contains("unrelated/Thing.cs", outcome.Verdict.Message);
    }

    /// <summary>A dependency file change fails even though the path is otherwise in scope.</summary>
    [Fact]
    public async Task ADependencyChangeFailsEvenWhenInScope()
    {
        _h.WriteAndCommit("Directory.Packages.props", "<Project></Project>");

        var outcome = await _h.EvalAsync();

        AssertGate(outcome, VerdictStatus.Fail, Reasons.DependencyChanged);
    }

    /// <summary>A changed config.yaml fails the run with Reasons.ConfigChanged.</summary>
    [Fact]
    public async Task AChangedConfigFailsTheRun()
    {
        _h.WriteAndCommit(".autor3search/config.yaml",
            File.ReadAllText(_h.ConfigPath) + "\nmax_regress_pct: 90.0\n");

        var outcome = await _h.EvalAsync();

        AssertGate(outcome, VerdictStatus.Fail, Reasons.ConfigChanged);
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

        AssertGate(outcome, VerdictStatus.Fail, Reasons.NewFrozenFile);
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

        AssertGate(outcome, VerdictStatus.Fail, Reasons.NewFrozenFile);
    }

    // The frozen project's .csproj moves along with its directory, and a .csproj at a
    // path the manifest does not recognise is a genuine dependency-surface change
    // regardless of how it got there — this is the correct gate for this scenario, not
    // Reasons.MissingFrozenFile. See Pipeline.EvalAsync's comment above the
    // MissingFrozenFile check, and DiscovererTests.FrozenFilesEnumerateFromTheGivenProjectPathRegardlessOfHiddenAncestors,
    // for why that direction is unreachable by construction: Discoverer.FrozenFiles
    // enumerates from the fixed, baseline-recorded project path Freezer.Restore
    // recreates, so a frozen file can never go missing from the walk that finds it.
    /// <summary>Moving a frozen test project to a hidden directory fails with Reasons.DependencyChanged, not MissingFrozenFile.</summary>
    [Fact]
    public async Task AFrozenProjectMovedToAHiddenDirectoryFailsAsADependencyChange()
    {
        _h.MoveDirectory("tests/Demo.Tests", "tests/.hidden.Demo.Tests");

        var outcome = await _h.EvalAsync();

        AssertGate(outcome, VerdictStatus.Fail, Reasons.DependencyChanged);
    }

    /// <summary>A code change that breaks the build crashes the run.</summary>
    [Fact]
    public async Task ACodeChangeThatBreaksTheBuildCrashes()
    {
        _h.WriteAndCommit("src/Demo/WordCount.cs", "this is not valid C#");

        var outcome = await _h.EvalAsync();

        AssertGate(outcome, VerdictStatus.Crash, Reasons.Build);
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

        AssertGate(outcome, VerdictStatus.Fail, Reasons.Tests);
    }

    /// <summary>A baseline worktree that moved off its recorded commit fails with Reasons.BaselineTampered.</summary>
    [Fact]
    public async Task AMovedBaselineWorktreeFails()
    {
        await _h.MoveBaselineWorktreeOffItsCommitAsync();

        var outcome = await _h.EvalAsync();

        AssertGate(outcome, VerdictStatus.Fail, Reasons.BaselineTampered);
    }

    /// <summary>A tampered frozen store fails with Reasons.FrozenTampered.</summary>
    [Fact]
    public async Task ATamperedFrozenStoreFails()
    {
        _h.TamperFrozenStore();

        var outcome = await _h.EvalAsync();

        AssertGate(outcome, VerdictStatus.Fail, Reasons.FrozenTampered);
    }
}
