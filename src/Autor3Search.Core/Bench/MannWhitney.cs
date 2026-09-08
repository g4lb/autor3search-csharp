namespace Autor3Search.Core.Bench;

/// <summary>The outcome of one two-sided Mann-Whitney U test.</summary>
/// <param name="P">Two-sided p-value.</param>
/// <param name="Alpha">The rejection threshold this test was run against.</param>
/// <param name="N1">Observations in the first sample.</param>
/// <param name="N2">Observations in the second sample.</param>
/// <param name="UsedExact">True when the exact distribution was used, false for the normal approximation.</param>
/// <param name="Warnings">Conditions that make the result unsafe to read at face value.</param>
public sealed record UTestResult(
    double P,
    double Alpha,
    int N1,
    int N2,
    bool UsedExact,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Two-sided Mann-Whitney U test (rank-sum), ported from
/// github.com/aclements/go-moremath/stats as used by golang.org/x/perf/benchmath.
/// See NOTICE for attribution.
///
/// Distribution-free on purpose: benchmark timings are skewed and heavy-tailed, and a
/// t-test's normality assumption does not hold for them.
/// </summary>
public static class MannWhitney
{
    /// <summary>Largest per-sample size for which the exact distribution is computed.</summary>
    private const int ExactLimit = 20;

    /// <summary>Runs the test. Both samples need at least two observations.</summary>
    public static UTestResult Test(double[] a, double[] b, double alpha = 0.05)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length < 2 || b.Length < 2)
        {
            throw new ArgumentException(
                $"need at least 2 observations per side, got {a.Length}/{b.Length}");
        }

        int n1 = a.Length, n2 = b.Length;
        var warnings = new List<string>();

        var (rankSumA, hasTies, tieCorrection) = RankSum(a, b);

        // U for sample A, and its mirror. The test is symmetric in the two samples.
        var u1 = rankSumA - n1 * (n1 + 1) / 2.0;
        var u2 = (double)n1 * n2 - u1;
        var uMin = Math.Min(u1, u2);

        double p;
        bool exact;

        if (!hasTies && n1 <= ExactLimit && n2 <= ExactLimit)
        {
            exact = true;
            p = Math.Min(1.0, 2.0 * ExactCumulative(n1, n2, uMin));
        }
        else
        {
            exact = false;
            p = Math.Min(1.0, NormalApproximation(uMin, n1, n2, tieCorrection));
        }

        if (hasTies)
        {
            warnings.Add(
                "samples contain tied values, so the exact rank distribution does not apply; " +
                "the tie-corrected normal approximation was used instead");
        }

        return new UTestResult(p, alpha, n1, n2, exact, warnings);
    }

    /// <summary>
    /// The smallest two-sided p-value attainable for these sample sizes, however far
    /// apart the samples are. With n per side it is 2/C(2n, n).
    ///
    /// The KEEP rule divides alpha by the number of benchmarks compared, so a large
    /// enough benchmark set pushes the corrected threshold below this floor and every
    /// experiment discards no matter what the agent does. `eval` warns when that has
    /// happened; this is the function that detects it.
    /// </summary>
    public static double MinimumAttainableP(int n1, int n2) =>
        Math.Min(1.0, 2.0 / Binomial(n1 + n2, n1));

    /// <summary>
    /// Midrank sum for sample A over the pooled ranking, plus whether ties occurred
    /// and the tie-correction term sum(t^3 - t) used by the normal approximation.
    /// </summary>
    private static (double RankSumA, bool HasTies, double TieCorrection) RankSum(double[] a, double[] b)
    {
        var pooled = new (double Value, bool FromA)[a.Length + b.Length];
        for (var i = 0; i < a.Length; i++) pooled[i] = (a[i], true);
        for (var i = 0; i < b.Length; i++) pooled[a.Length + i] = (b[i], false);

        Array.Sort(pooled, (x, y) => x.Value.CompareTo(y.Value));

        double rankSumA = 0, tieCorrection = 0;
        var hasTies = false;

        var i2 = 0;
        while (i2 < pooled.Length)
        {
            var j = i2;
            while (j + 1 < pooled.Length && pooled[j + 1].Value == pooled[i2].Value) j++;

            var tieCount = j - i2 + 1;
            // Ranks are 1-based; tied entries all take the average of their ranks.
            var midRank = (i2 + 1 + j + 1) / 2.0;

            if (tieCount > 1)
            {
                hasTies = true;
                tieCorrection += Math.Pow(tieCount, 3) - tieCount;
            }

            for (var k = i2; k <= j; k++)
            {
                if (pooled[k].FromA) rankSumA += midRank;
            }

            i2 = j + 1;
        }

        return (rankSumA, hasTies, tieCorrection);
    }

    /// <summary>
    /// P(U &lt;= u) under the exact null distribution.
    ///
    /// Counts arrangements with the Mann-Whitney recurrence
    /// N(m, n, u) = N(m-1, n, u-n) + N(m, n-1, u), then divides by C(m+n, m).
    /// </summary>
    private static double ExactCumulative(int n1, int n2, double u)
    {
        var maxU = n1 * n2;
        var target = (int)Math.Floor(u + 1e-9);
        if (target < 0) return 0.0;
        if (target >= maxU) return 1.0;

        // counts[m, n, k] built iteratively; k indexes U from 0 to maxU.
        var counts = new double[n1 + 1, n2 + 1, maxU + 1];
        for (var m = 0; m <= n1; m++) counts[m, 0, 0] = 1.0;
        for (var n = 0; n <= n2; n++) counts[0, n, 0] = 1.0;

        for (var m = 1; m <= n1; m++)
        {
            for (var n = 1; n <= n2; n++)
            {
                for (var k = 0; k <= maxU; k++)
                {
                    var fromA = k - n >= 0 ? counts[m - 1, n, k - n] : 0.0;
                    counts[m, n, k] = fromA + counts[m, n - 1, k];
                }
            }
        }

        double cumulative = 0;
        for (var k = 0; k <= target; k++) cumulative += counts[n1, n2, k];

        return cumulative / Binomial(n1 + n2, n1);
    }

    /// <summary>
    /// Two-sided p from the normal approximation, with tie correction and a
    /// continuity correction of 0.5.
    /// </summary>
    private static double NormalApproximation(double uMin, int n1, int n2, double tieCorrection)
    {
        double n = n1 + n2;
        var mean = n1 * (double)n2 / 2.0;

        var variance = n1 * (double)n2 / 12.0
                       * (n + 1.0 - tieCorrection / (n * (n - 1.0)));

        if (variance <= 0) return 1.0;

        // uMin is at or below the mean by construction, so the continuity correction
        // moves toward the mean.
        var z = (uMin - mean + 0.5) / Math.Sqrt(variance);
        return 2.0 * Normal.SurvivalFunction(Math.Abs(z));
    }

    /// <summary>C(n, k) as a double. Values stay far inside double range for realistic counts.</summary>
    private static double Binomial(int n, int k)
    {
        if (k < 0 || k > n) return 0.0;
        k = Math.Min(k, n - k);

        var result = 1.0;
        for (var i = 1; i <= k; i++) result = result * (n - k + i) / i;
        return result;
    }
}
