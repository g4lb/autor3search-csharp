namespace Autor3Search.Core.Bench;

/// <summary>One benchmark's unit compared between baseline and candidate.</summary>
/// <param name="Name">Benchmark FullName.</param>
/// <param name="Unit">Metric unit, from <see cref="Units"/>.</param>
/// <param name="BaseCenter">Baseline median.</param>
/// <param name="CandCenter">Candidate median.</param>
/// <param name="Ratio">CandCenter / BaseCenter. Below 1 is better for lower-is-better units.</param>
/// <param name="PctChange">(Ratio - 1) * 100.</param>
/// <param name="P">Two-sided Mann-Whitney p-value.</param>
/// <param name="Alpha">The raw rejection threshold. Never the Bonferroni-corrected one.</param>
/// <param name="Significant">P &lt; Alpha, uncorrected. The honest statistic a reader should see.</param>
/// <param name="NBase">Baseline observations.</param>
/// <param name="NCand">Candidate observations.</param>
/// <param name="Warnings">Deduplicated warnings from both summaries and the test.</param>
public sealed record Delta(
    string Name,
    string Unit,
    double BaseCenter,
    double CandCenter,
    double Ratio,
    double PctChange,
    double P,
    double Alpha,
    bool Significant,
    int NBase,
    int NCand,
    IReadOnlyList<string> Warnings);

/// <summary>Comparison and scoring across measured rounds.</summary>
public static class Stats
{
    /// <summary>Confidence level for the reported median interval.</summary>
    public const double Confidence = 0.95;

    /// <summary>Compares one benchmark's unit across two measurement sets.</summary>
    public static Delta Compare(BenchSet baseSet, BenchSet candSet, string name, string unit)
    {
        var bv = baseSet.Values(name, unit);
        var cv = candSet.Values(name, unit);

        if (bv.Length == 0) throw new InvalidOperationException($"baseline has no {unit} for {name}");
        if (cv.Length == 0) throw new InvalidOperationException($"candidate has no {unit} for {name}");
        if (bv.Length < 2 || cv.Length < 2)
        {
            throw new InvalidOperationException(
                $"{name}: need at least 2 observations per side, got {bv.Length}/{cv.Length}");
        }

        var bSum = MedianEstimate.Summarize(bv, Confidence);
        var cSum = MedianEstimate.Summarize(cv, Confidence);
        var test = MannWhitney.Test(bv, cv);

        if (bSum.Center == 0)
        {
            throw new InvalidOperationException(
                $"{name}: baseline {unit} median is zero, cannot form a ratio");
        }

        var ratio = cSum.Center / bSum.Center;

        return new Delta(
            Name: name,
            Unit: unit,
            BaseCenter: bSum.Center,
            CandCenter: cSum.Center,
            Ratio: ratio,
            PctChange: (ratio - 1.0) * 100.0,
            P: test.P,
            Alpha: test.Alpha,
            // Deliberately the RAW comparison. The Bonferroni correction is a KEEP
            // threshold layered on top in Verdicts, not a redefinition of
            // "significant" — a reader of a report should see the honest statistic.
            Significant: test.P < test.Alpha,
            NBase: test.N1,
            NCand: test.N2,
            Warnings: Dedupe(bSum.Warnings, cSum.Warnings, test.Warnings));
    }

    /// <summary>
    /// Compares every benchmark measured at baseline, sorted by name.
    ///
    /// A benchmark present at baseline but missing from the candidate is an error, not
    /// an omission: it cannot be checked for a regression, which is precisely what an
    /// agent deleting an inconvenient benchmark would be counting on.
    /// </summary>
    public static IReadOnlyList<Delta> CompareAll(BenchSet baseSet, BenchSet candSet, string unit)
    {
        var deltas = new List<Delta>();
        var missing = new List<string>();

        foreach (var name in baseSet.Names())
        {
            if (!candSet.Has(name, unit)) { missing.Add(name); continue; }
            deltas.Add(Compare(baseSet, candSet, name, unit));
        }

        if (missing.Count > 0)
        {
            missing.Sort(StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"benchmark(s) measured at baseline but missing from the candidate: " +
                $"{string.Join(", ", missing)} — a benchmark that disappears cannot be " +
                "checked for regressions");
        }

        if (deltas.Count == 0)
            throw new InvalidOperationException("no benchmark appears in both baseline and candidate");

        return deltas;
    }

    /// <summary>
    /// The geometric mean of the deltas' ratios: the single score the agent optimizes.
    /// Below 1 is an overall speedup. Geometric rather than arithmetic because these
    /// are ratios — halving one benchmark and doubling another must score 1.0, not 1.25.
    /// </summary>
    public static double GeoMean(IReadOnlyList<Delta> deltas)
    {
        if (deltas.Count == 0)
            throw new InvalidOperationException("geomean of an empty delta set");

        double sum = 0;
        foreach (var d in deltas)
        {
            if (d.Ratio <= 0)
                throw new InvalidOperationException($"{d.Name}: non-positive ratio {d.Ratio}");
            sum += Math.Log(d.Ratio);
        }

        return Math.Exp(sum / deltas.Count);
    }

    private static IReadOnlyList<string> Dedupe(params IReadOnlyList<string>[] lists)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var list in lists)
        {
            foreach (var w in list)
            {
                if (seen.Add(w)) output.Add(w);
            }
        }
        return output;
    }
}
