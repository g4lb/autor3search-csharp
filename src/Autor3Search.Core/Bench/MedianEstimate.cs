namespace Autor3Search.Core.Bench;

/// <summary>A median and its distribution-free confidence interval.</summary>
/// <param name="Center">The sample median.</param>
/// <param name="Lo">Lower bound, or negative infinity when unbounded.</param>
/// <param name="Hi">Upper bound, or positive infinity when unbounded.</param>
/// <param name="Bounded">False when too few observations exist for a finite interval.</param>
/// <param name="Warnings">Conditions that make the summary unsafe to read at face value.</param>
public sealed record MedianSummary(
    double Center,
    double Lo,
    double Hi,
    bool Bounded,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Median summary with a distribution-free confidence interval from binomial order
/// statistics. Ported from a third-party BSD 3-Clause implementation of the same
/// distribution-free summary; see NOTICE for the attribution.
/// </summary>
public static class MedianEstimate
{
    /// <summary>Summarizes observations at the given confidence level.</summary>
    public static MedianSummary Summarize(double[] values, double confidence = 0.95)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
            throw new ArgumentException("cannot summarize an empty sample", nameof(values));

        for (var i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
            {
                throw new ArgumentException(
                    $"sample '{nameof(values)}' contains a non-finite value ({values[i]}) at index {i}",
                    nameof(values));
            }
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var n = sorted.Length;
        var center = n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;

        var alpha = 1.0 - confidence;

        // Largest k >= 1 with P(Binomial(n, 0.5) <= k-1) <= alpha/2. The interval is
        // then [x_(k), x_(n+1-k)] in 1-based order statistics.
        var k = 0;
        double cumulative = 0;
        var scale = Math.Pow(0.5, n);
        for (var i = 0; i < n; i++)
        {
            cumulative += Combinatorics.Binomial(n, i) * scale;
            if (cumulative > alpha / 2.0) break;
            k = i + 1;
        }

        if (k < 1)
        {
            // The tightest bound available is P(X = 0) = 1/2^n, so a finite interval
            // at 95% confidence needs 2^n >= 40, i.e. n >= 6.
            var needed = MinimumObservations(confidence);
            return new MedianSummary(
                center,
                double.NegativeInfinity,
                double.PositiveInfinity,
                Bounded: false,
                Warnings: [$"need >= {needed} observations for a confidence interval at level {confidence}, have {n}"]);
        }

        return new MedianSummary(center, sorted[k - 1], sorted[n - k], Bounded: true, Warnings: []);
    }

    /// <summary>Smallest observation count admitting a finite interval at this confidence.</summary>
    public static int MinimumObservations(double confidence)
    {
        var half = (1.0 - confidence) / 2.0;
        for (var n = 1; n <= 1000; n++)
        {
            if (Math.Pow(0.5, n) <= half) return n;
        }
        return 1000;
    }
}
