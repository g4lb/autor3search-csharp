using Autor3Search.Core;
using Autor3Search.Core.RunState;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>
/// Serialises test classes that mutate the process-wide state-home environment
/// variable. xUnit parallelises across classes by default, so without this two such
/// classes would intermittently corrupt each other's environment.
/// </summary>
[CollectionDefinition("StateHomeEnv", DisableParallelization = true)]
public sealed class StateHomeEnvCollection;

/// <summary>Tests for out-of-tree run state: baselines, claims and stop markers.</summary>
[Collection("StateHomeEnv")]
public sealed class StateStoreTests : IDisposable
{
    private readonly string _stateHome;
    private readonly string _repo = "/some/repo";

    /// <summary>Points the state home at a fresh temporary directory for this test.</summary>
    public StateStoreTests()
    {
        _stateHome = Path.Combine(Path.GetTempPath(), $"a3s-state-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, _stateHome);
    }

    /// <summary>Restores the environment and removes the temporary state home.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, null);
        try { if (Directory.Exists(_stateHome)) Directory.Delete(_stateHome, true); }
        catch (IOException) { }
    }

    private static Baseline NewBaseline() => new()
    {
        Tag = "sep8",
        Commit = "a".PadRight(40, '1'),
        MeasureCommit = "a".PadRight(40, '1'),
        ConfigSha256 = "b".PadRight(64, '2'),
        CreatedAt = DateTimeOffset.Parse("2026-09-08T10:00:00+00:00"),
        FrozenProjects = ["tests/Demo.Tests/Demo.Tests.csproj"],
        BenchmarkProject = "bench/Demo.Benchmarks/Demo.Benchmarks.csproj",
    };

    /// <summary>State lives outside the repository it belongs to.</summary>
    [Fact]
    public void StateLivesOutsideTheRepository()
    {
        var store = new StateStore(_repo, "sep8");
        Assert.StartsWith(_stateHome, store.Root);
        Assert.DoesNotContain(_repo, store.Root);
    }

    /// <summary>Two tags for the same repository get separate directories.</summary>
    [Fact]
    public void TwoTagsGetSeparateDirectories()
    {
        Assert.NotEqual(new StateStore(_repo, "sep8").Root, new StateStore(_repo, "sep9").Root);
    }

    /// <summary>Two repositories with the same tag get separate directories.</summary>
    [Fact]
    public void TwoRepositoriesGetSeparateDirectories()
    {
        Assert.NotEqual(new StateStore("/repo/a", "sep8").Root, new StateStore("/repo/b", "sep8").Root);
    }

    /// <summary>A saved baseline round-trips through disk unchanged.</summary>
    [Fact]
    public void BaselineRoundTripsThroughDisk()
    {
        var store = new StateStore(_repo, "sep8");
        store.SaveBaseline(NewBaseline());

        var loaded = store.LoadBaseline();
        Assert.Equal("sep8", loaded.Tag);
        Assert.Equal(NewBaseline().Commit, loaded.Commit);
        Assert.Equal(NewBaseline().ConfigSha256, loaded.ConfigSha256);
        Assert.Equal(["tests/Demo.Tests/Demo.Tests.csproj"], loaded.FrozenProjects);
    }

    /// <summary>Exists reflects whether a baseline has been saved.</summary>
    [Fact]
    public void ExistsReflectsWhetherABaselineWasSaved()
    {
        var store = new StateStore(_repo, "sep8");
        Assert.False(store.Exists);
        store.SaveBaseline(NewBaseline());
        Assert.True(store.Exists);
    }

    // The frozen anchor and the measurement anchor are deliberately separate. The
    // scope gate always compares against Commit, which never moves, so an agent
    // cannot widen what it may edit by banking experiments.
    /// <summary>The measurement commit advances on save while the frozen anchor does not.</summary>
    [Fact]
    public void TheMeasurementCommitAdvancesWhileTheFrozenAnchorDoesNot()
    {
        var store = new StateStore(_repo, "sep8");
        var b = NewBaseline();
        store.SaveBaseline(b);

        var advanced = store.LoadBaseline() with { MeasureCommit = "c".PadRight(40, '3') };
        store.SaveBaseline(advanced);

        var reloaded = store.LoadBaseline();
        Assert.Equal(b.Commit, reloaded.Commit);
        Assert.Equal("c".PadRight(40, '3'), reloaded.MeasureCommit);
        Assert.NotEqual(reloaded.Commit, reloaded.MeasureCommit);
    }

    /// <summary>The run claim records and clears a pid.</summary>
    [Fact]
    public void TheRunClaimRecordsAndClearsAPid()
    {
        var store = new StateStore(_repo, "sep8");
        Assert.Null(store.ReadClaim());

        store.WriteClaim(4242);
        Assert.Equal(4242, store.ReadClaim());

        store.ClearClaim();
        Assert.Null(store.ReadClaim());
    }

    /// <summary>A claim held by this process is alive.</summary>
    [Fact]
    public void AClaimHeldByThisProcessIsAlive()
    {
        var store = new StateStore(_repo, "sep8");
        store.WriteClaim(Environment.ProcessId);
        Assert.True(store.IsClaimAlive());
    }

    // A crashed eval leaves a stale pid file. Treating that as a live run would wedge
    // the repository until a human deleted a file they were never told about.
    /// <summary>A claim naming a process that does not exist is not alive.</summary>
    [Fact]
    public void AClaimForADeadProcessIsNotAlive()
    {
        var store = new StateStore(_repo, "sep8");
        store.WriteClaim(999_999_998);
        Assert.False(store.IsClaimAlive());
    }

    /// <summary>Stop requests are written and cleared.</summary>
    [Fact]
    public void StopRequestsAreWrittenAndCleared()
    {
        var store = new StateStore(_repo, "sep8");
        Assert.False(store.StopRequested);

        store.RequestStop();
        Assert.True(store.StopRequested);
        Assert.False(store.ForceStopRequested);

        store.ClearStop();
        Assert.False(store.StopRequested);
    }

    /// <summary>A force stop also requests a graceful stop.</summary>
    [Fact]
    public void AForceStopAlsoRequestsAGracefulStop()
    {
        var store = new StateStore(_repo, "sep8");
        store.RequestForceStop();

        Assert.True(store.ForceStopRequested);
        Assert.True(store.StopRequested);
    }

    /// <summary>Clearing a stop removes both markers.</summary>
    [Fact]
    public void ClearStopRemovesBothMarkers()
    {
        var store = new StateStore(_repo, "sep8");
        store.RequestForceStop();
        store.ClearStop();

        Assert.False(store.StopRequested);
        Assert.False(store.ForceStopRequested);
    }

    /// <summary>Branch names round-trip with the tags they carry.</summary>
    [Fact]
    public void BranchNamesRoundTripWithTags()
    {
        Assert.Equal("autor3search-csharp/sep8", StateStore.BranchForTag("sep8"));
        Assert.Equal("sep8", StateStore.TagFromBranch("autor3search-csharp/sep8"));
        Assert.Null(StateStore.TagFromBranch("main"));
    }
}
