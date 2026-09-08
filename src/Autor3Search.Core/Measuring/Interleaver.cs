using Autor3Search.Core.Bench;

namespace Autor3Search.Core.Measuring;

/// <summary>Produces one round of observations for one side.</summary>
/// <param name="round">Zero-based round index, including any warmup round.</param>
/// <param name="ct">Cancellation for the round.</param>
public delegate Task<IReadOnlyList<Observation>> RoundFunc(int round, CancellationToken ct);

/// <summary>
/// Runs two sides alternately and accumulates their observations.
///
/// Interleaving is the core measurement discipline of this tool. Comparing a candidate
/// measured now against a baseline measured minutes ago attributes CPU thermal drift,
/// frequency scaling and background load to the code change. Alternating the two sides
/// within one session cancels that, because both experience the same conditions.
/// </summary>
public static class Interleaver
{
    /// <summary>
    /// Runs <paramref name="rounds"/> measured rounds per side, optionally preceded by
    /// one discarded warmup round that absorbs first-touch effects such as cold caches
    /// and tiered JIT compilation.
    ///
    /// The two sides SWAP ORDER on every round — base,cand then cand,base. Alternating
    /// rounds alone cancels drift BETWEEN rounds, but a fixed within-round order leaves
    /// a systematic offset: the candidate would always occupy the later slot, so drift
    /// monotonic across a round biases it in one direction every single time. That is a
    /// constant, not noise, and averaging does not remove it. Swapping makes each side
    /// take the first slot half the time, cancelling the term to first order.
    ///
    /// An odd round count cannot split evenly and leaves one round's offset behind; an
    /// even count — the default is 10 — cancels it exactly.
    /// </summary>
    public static async Task<(BenchSet Base, BenchSet Cand)> RunAsync(
        int rounds, bool warmup, RoundFunc baseSide, RoundFunc candSide, CancellationToken ct)
    {
        if (rounds < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rounds), rounds, "need at least 2 measured rounds");
        }

        var total = warmup ? rounds + 1 : rounds;
        var baseSet = new BenchSet();
        var candSet = new BenchSet();

        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (b, c) = await RunRoundAsync(i, baseSide, candSide, ct).ConfigureAwait(false);

            if (warmup && i == 0) continue;

            baseSet.Add(b);
            candSet.Add(c);
        }

        return (baseSet, candSet);
    }

    private static async Task<(IReadOnlyList<Observation> Base, IReadOnlyList<Observation> Cand)>
        RunRoundAsync(int i, RoundFunc baseSide, RoundFunc candSide, CancellationToken ct)
    {
        var candidateFirst = i % 2 == 1;

        var first = candidateFirst ? candSide : baseSide;
        var second = candidateFirst ? baseSide : candSide;
        var firstLabel = candidateFirst ? "candidate" : "baseline";
        var secondLabel = candidateFirst ? "baseline" : "candidate";

        var f = await Invoke(first, firstLabel, i, ct).ConfigureAwait(false);
        var s = await Invoke(second, secondLabel, i, ct).ConfigureAwait(false);

        return candidateFirst ? (s, f) : (f, s);
    }

    private static async Task<IReadOnlyList<Observation>> Invoke(
        RoundFunc side, string label, int round, CancellationToken ct)
    {
        try
        {
            return await side(round, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{label} round {round}: {ex.Message}", ex);
        }
    }
}
