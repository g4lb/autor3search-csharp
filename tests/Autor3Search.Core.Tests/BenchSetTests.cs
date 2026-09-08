using Autor3Search.Core.Bench;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for accumulating per-round observations into a BenchSet.</summary>
public class BenchSetTests
{
    private static Observation Ob(string name, double ns, long bytes) => new(name, ns, bytes);

    /// <summary>Verifies that each Add call contributes one value per round.</summary>
    [Fact]
    public void AddAccumulatesOneValuePerRound()
    {
        var set = new BenchSet();
        set.Add([Ob("A.B", 100, 8)]);
        set.Add([Ob("A.B", 110, 8)]);
        set.Add([Ob("A.B", 120, 8)]);

        Assert.Equal([100.0, 110.0, 120.0], set.Values("A.B", Units.TimeNs));
    }

    /// <summary>Verifies that Values preserves the order rounds were added in.</summary>
    [Fact]
    public void ValuesPreserveRoundOrder()
    {
        var set = new BenchSet();
        set.Add([Ob("A.B", 300, 1)]);
        set.Add([Ob("A.B", 100, 1)]);
        Assert.Equal([300.0, 100.0], set.Values("A.B", Units.TimeNs));
    }

    /// <summary>Verifies that Names returns benchmark names sorted for deterministic output.</summary>
    [Fact]
    public void NamesAreSortedForDeterministicOutput()
    {
        var set = new BenchSet();
        set.Add([Ob("Z.Last", 1, 1), Ob("A.First", 1, 1)]);
        Assert.Equal(["A.First", "Z.Last"], set.Names());
    }

    /// <summary>Verifies that bytes-per-op values are tracked as a series separate from time.</summary>
    [Fact]
    public void BytesPerOpIsTrackedSeparately()
    {
        var set = new BenchSet();
        set.Add([Ob("A.B", 100, 4096)]);
        set.Add([Ob("A.B", 100, 2048)]);
        Assert.Equal([4096.0, 2048.0], set.Values("A.B", Units.BytesPerOp));
    }

    /// <summary>Verifies that Has reports presence by benchmark name and unit.</summary>
    [Fact]
    public void HasReportsPresenceByNameAndUnit()
    {
        var set = new BenchSet();
        set.Add([Ob("A.B", 100, 8)]);
        Assert.True(set.Has("A.B", Units.TimeNs));
        Assert.True(set.Has("A.B", Units.BytesPerOp));
        Assert.False(set.Has("Missing.One", Units.TimeNs));
    }

    /// <summary>Verifies that Values returns an empty array for an unknown benchmark.</summary>
    [Fact]
    public void ValuesForAnUnknownBenchmarkIsEmpty()
    {
        var set = new BenchSet();
        Assert.Empty(set.Values("Nope", Units.TimeNs));
    }
}
