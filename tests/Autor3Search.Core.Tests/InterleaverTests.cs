using Autor3Search.Core.Bench;
using Autor3Search.Core.Measuring;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Verifies the interleaving discipline: alternation, order swap, attribution, warmup, cancellation and failure naming.</summary>
public class InterleaverTests
{
    private static RoundFunc Recording(string label, List<string> order, double value) =>
        (round, ct) =>
        {
            order.Add($"{label}{round}");
            return Task.FromResult<IReadOnlyList<Observation>>([new Observation("A.B", value, 1)]);
        };

    /// <summary>Each side contributes exactly one observation per measured round.</summary>
    [Fact]
    public async Task EachSideContributesOneValuePerRound()
    {
        var order = new List<string>();
        var (b, c) = await Interleaver.RunAsync(
            4, warmup: false,
            Recording("b", order, 100), Recording("c", order, 50),
            CancellationToken.None);

        Assert.Equal(4, b.Values("A.B", Units.TimeNs).Length);
        Assert.Equal(4, c.Values("A.B", Units.TimeNs).Length);
    }

    // Alternating rounds cancels drift BETWEEN rounds. Running the sides in a fixed
    // order WITHIN each round leaves a systematic offset: the candidate would always
    // be measured one slot later, so any drift monotonic across a round lands on it
    // in the same direction every time. Averaging cannot remove a constant bias.
    /// <summary>The two sides swap which one runs first on alternate rounds.</summary>
    [Fact]
    public async Task TheSidesSwapOrderOnAlternateRounds()
    {
        var order = new List<string>();
        await Interleaver.RunAsync(
            4, warmup: false,
            Recording("b", order, 100), Recording("c", order, 50),
            CancellationToken.None);

        Assert.Equal(["b0", "c0", "c1", "b1", "b2", "c2", "c3", "b3"], order);
    }

    /// <summary>Observations end up attributed to the correct side even though the run order swaps.</summary>
    [Fact]
    public async Task ResultsAreAttributedToTheCorrectSideDespiteSwapping()
    {
        var order = new List<string>();
        var (b, c) = await Interleaver.RunAsync(
            4, warmup: false,
            Recording("b", order, 100), Recording("c", order, 50),
            CancellationToken.None);

        Assert.All(b.Values("A.B", Units.TimeNs), v => Assert.Equal(100.0, v));
        Assert.All(c.Values("A.B", Units.TimeNs), v => Assert.Equal(50.0, v));
    }

    /// <summary>With warmup enabled, one extra round runs first and its observations are discarded.</summary>
    [Fact]
    public async Task TheWarmupRoundIsRunAndDiscarded()
    {
        var order = new List<string>();
        var (b, _) = await Interleaver.RunAsync(
            4, warmup: true,
            Recording("b", order, 100), Recording("c", order, 50),
            CancellationToken.None);

        Assert.Equal(10, order.Count);                        // 5 rounds x 2 sides
        Assert.Equal(4, b.Values("A.B", Units.TimeNs).Length); // 4 kept
    }

    /// <summary>Fewer than two measured rounds is rejected, since a single round cannot swap order.</summary>
    [Fact]
    public async Task FewerThanTwoRoundsIsRejected()
    {
        var order = new List<string>();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Interleaver.RunAsync(1, false,
                Recording("b", order, 1), Recording("c", order, 1), CancellationToken.None));
    }

    /// <summary>A failing side's exception message names both the side and the round it failed on.</summary>
    [Fact]
    public async Task AFailingSideAbortsWithTheRoundAndSideNamed()
    {
        var order = new List<string>();
        RoundFunc failing = (round, ct) => throw new InvalidOperationException("boom");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Interleaver.RunAsync(4, false,
                Recording("b", order, 1), failing, CancellationToken.None));

        Assert.Contains("candidate", ex.Message);
        Assert.Contains("round 0", ex.Message);
    }

    /// <summary>Cancellation is observed between rounds and propagates as an operation-cancelled exception.</summary>
    [Fact]
    public async Task CancellationStopsBetweenRounds()
    {
        using var cts = new CancellationTokenSource();
        var count = 0;

        RoundFunc counting = (round, ct) =>
        {
            if (++count >= 3) cts.Cancel();
            return Task.FromResult<IReadOnlyList<Observation>>([new Observation("A.B", 1, 1)]);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Interleaver.RunAsync(10, false, counting, counting, cts.Token));
    }
}
