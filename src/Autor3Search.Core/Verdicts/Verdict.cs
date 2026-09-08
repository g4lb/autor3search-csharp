using System.Globalization;
using System.Text;
using Autor3Search.Core.Bench;

namespace Autor3Search.Core.Verdicts;

/// <summary>The terminal outcome of one experiment.</summary>
public enum VerdictStatus
{
    /// <summary>The change measurably helped and is kept.</summary>
    Keep,

    /// <summary>The change was measured and rejected.</summary>
    Discard,

    /// <summary>A correctness or integrity gate rejected the change.</summary>
    Fail,

    /// <summary>The candidate did not build, or a phase timed out.</summary>
    Crash,
}

/// <summary>Exit-code mapping for <see cref="VerdictStatus"/>.</summary>
public static class VerdictStatusExtensions
{
    /// <summary>The process exit code for a status. Fixed by contract: 0/1/2/3.</summary>
    public static int ExitCode(this VerdictStatus status) => status switch
    {
        VerdictStatus.Keep => 0,
        VerdictStatus.Discard => 1,
        VerdictStatus.Fail => 2,
        VerdictStatus.Crash => 3,
        _ => 2,
    };

    /// <summary>The wire form written to results.tsv and JSON output.</summary>
    public static string ToWireString(this VerdictStatus status) => status switch
    {
        VerdictStatus.Keep => "KEEP",
        VerdictStatus.Discard => "DISCARD",
        VerdictStatus.Fail => "FAIL",
        VerdictStatus.Crash => "CRASH",
        _ => "FAIL",
    };
}

/// <summary>Machine-readable explanations for a status.</summary>
public static class Reasons
{
    /// <summary>Kept: a real, significant improvement.</summary>
    public const string Improved = "improved";

    /// <summary>Discarded: nothing measurably moved.</summary>
    public const string NoImprovement = "no_significant_improvement";

    /// <summary>Discarded: measurably faster, but by less than min_effect_pct.</summary>
    public const string BelowMinEffect = "improvement_below_min_effect";

    /// <summary>Discarded: a significant regression beyond max_regress_pct.</summary>
    public const string GuardRegression = "guard_regression";

    /// <summary>Failed: an edit outside the allowed scope.</summary>
    public const string Scope = "scope_violation";

    /// <summary>Failed: config.yaml changed since baseline.</summary>
    public const string ConfigChanged = "config_changed";

    /// <summary>Failed: a frozen file appeared that was not present at baseline.</summary>
    public const string NewFrozenFile = "new_frozen_file";

    /// <summary>Failed: a frozen file is no longer discoverable in the working tree.</summary>
    public const string MissingFrozenFile = "missing_frozen_file";

    /// <summary>Failed: a frozen file was replaced by a symlink.</summary>
    public const string SymlinkSwap = "symlink_swap";

    /// <summary>Failed: the frozen store itself was modified.</summary>
    public const string FrozenTampered = "frozen_store_tampered";

    /// <summary>Failed: the pinned baseline worktree moved off its recorded commit.</summary>
    public const string BaselineTampered = "baseline_tampered";

    /// <summary>Failed: a dependency file was modified.</summary>
    public const string DependencyChanged = "dependency_changed";

    /// <summary>Crashed: the candidate did not build.</summary>
    public const string Build = "build_failed";

    /// <summary>Failed: the frozen tests did not pass.</summary>
    public const string Tests = "tests_failed";

    /// <summary>Crashed: a subprocess phase exceeded its timeout.</summary>
    public const string Timeout = "timeout";
}

/// <summary>Everything <see cref="Verdict.Decide"/> needs once all gates have passed.</summary>
/// <param name="Deltas">Per-benchmark time comparisons. The family for the Bonferroni correction.</param>
/// <param name="Score">Geomean of the deltas' ratios.</param>
/// <param name="MaxRegressPct">Largest tolerated significant regression.</param>
/// <param name="MinEffectPct">Smallest geomean improvement a KEEP will accept.</param>
public sealed record VerdictInput(
    IReadOnlyList<Delta> Deltas,
    double Score,
    double MaxRegressPct,
    double MinEffectPct);

/// <summary>The harness's answer for one experiment.</summary>
/// <param name="Status">KEEP, DISCARD, FAIL or CRASH.</param>
/// <param name="Reason">A constant from <see cref="Reasons"/>.</param>
/// <param name="Score">The geomean score, or 0 for a gate failure.</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="Regressions">Benchmarks that tripped the regression guard.</param>
/// <param name="Warnings">Conditions that qualify how far the numbers can be trusted. Never change the decision.</param>
public sealed record VerdictResult(
    VerdictStatus Status,
    string Reason,
    double Score,
    string Message,
    IReadOnlyList<Delta> Regressions,
    IReadOnlyList<string> Warnings);

/// <summary>Turns gate outcomes and measurements into a single decision.</summary>
public static class Verdict
{
    /// <summary>Builds a result for a failed correctness stage, before measurement.</summary>
    public static VerdictResult Gate(VerdictStatus status, string reason, string message) =>
        new(status, reason, 0.0, message, [], []);

    /// <summary>
    /// Applies the scoring rules.
    ///
    /// 1. Any regression significant at the RAW, uncorrected alpha and larger than
    ///    MaxRegressPct rejects the change however good the overall score. This
    ///    deliberately does not apply rule 2's Bonferroni correction: the correction
    ///    only ever makes significance harder to reach, so using it here would make
    ///    real regressions easier to miss. The asymmetry is the point — conservative
    ///    about accepting a win, liberal about catching harm.
    ///
    /// 2. Otherwise keep only when BOTH:
    ///    a. the score is below 1 - MinEffectPct/100, not merely below 1; and
    ///    b. at least one benchmark improved at alpha/k, where k is the number of
    ///       benchmarks compared. Testing k benchmarks against one uncorrected alpha
    ///       inflates the family-wise false-positive rate — with k=4 there is roughly
    ///       an 18% chance one looks significant when nothing changed.
    ///
    /// A change clearing 2b but missing 2a discards as BelowMinEffect, not
    /// NoImprovement: it worked, it was just too small to bank.
    /// </summary>
    public static VerdictResult Decide(VerdictInput input)
    {
        var k = Math.Max(1, input.Deltas.Count);
        var warnings = MeasurementWarnings(input.Deltas, k);

        var regressions = input.Deltas
            .Where(d => d.Significant && d.PctChange > input.MaxRegressPct)
            .ToList();

        if (regressions.Count > 0)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < regressions.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Inv($"{regressions[i].Name} {regressions[i].PctChange:+0.0;-0.0}%"));
            }

            return new VerdictResult(
                VerdictStatus.Discard, Reasons.GuardRegression, input.Score,
                Inv($"regression guard tripped (limit {input.MaxRegressPct:+0.0;-0.0}%): {sb}"),
                regressions, warnings);
        }

        var improved = input.Deltas.Any(d => d.PctChange < 0 && d.P < d.Alpha / k);
        var minEffectThreshold = 1 - input.MinEffectPct / 100;
        var pct = (input.Score - 1) * 100;

        if (input.Score < minEffectThreshold && improved)
        {
            return new VerdictResult(
                VerdictStatus.Keep, Reasons.Improved, input.Score,
                Inv($"score {input.Score:F4} ({pct:+0.00;-0.00}%)"), [], warnings);
        }

        if (improved && input.Score < 1)
        {
            return new VerdictResult(
                VerdictStatus.Discard, Reasons.BelowMinEffect, input.Score,
                Inv($"score {input.Score:F4} ({pct:+0.00;-0.00}%), a real improvement but below ") +
                Inv($"the {input.MinEffectPct:F1}% minimum effect size"),
                [], warnings);
        }

        // "no overall improvement", not "no significant improvement": a benchmark may well
        // have improved significantly here (improved can be true and score still >= 1) —
        // the geomean simply did not clear 1. This message must stay true either way; the
        // machine-readable Reasons.NoImprovement constant is unchanged (Tasks 18-20 and
        // results.tsv bind to its wire value).
        return new VerdictResult(
            VerdictStatus.Discard, Reasons.NoImprovement, input.Score,
            Inv($"score {input.Score:F4} ({pct:+0.00;-0.00}%), no overall improvement"),
            [], warnings);
    }

    /// <summary>
    /// Formats invariantly, regardless of ambient culture.
    ///
    /// These strings are read by an agent and written into results.tsv, so their shape
    /// is a contract. Relying on the repository's InvariantGlobalization setting would
    /// put that contract in a different file, where removing it for an unrelated reason
    /// would silently change this one's output.
    /// </summary>
    private static string Inv(FormattableString s) =>
        s.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> MeasurementWarnings(IReadOnlyList<Delta> deltas, int k)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();

        foreach (var d in deltas)
        {
            foreach (var w in d.Warnings)
            {
                if (seen.Add(w)) output.Add(w);
            }
        }

        var unreachable = UnreachableAlphaWarning(deltas, k);
        if (unreachable is not null) output.Add(unreachable);

        return output;
    }

    /// <summary>
    /// Detects a run that cannot produce a KEEP no matter what the agent does.
    ///
    /// The U test has a floor on the p-value it can produce for given sample sizes:
    /// 2/C(n1+n2, n1), because that is the fraction of orderings at least as extreme
    /// as the observed one. If alpha/k falls below that floor for EVERY benchmark,
    /// none can clear it. RunConfig.Validate enforces a count floor for k=1; it cannot
    /// catch this, because it does not know how many benchmarks a run will compare.
    ///
    /// KEEP needs only one benchmark to clear the bar, so this warns only when none can.
    /// </summary>
    private static string? UnreachableAlphaWarning(IReadOnlyList<Delta> deltas, int k)
    {
        if (deltas.Count == 0) return null;

        // The LARGEST min(NBase, NCand) among the deltas that can't clear the bar. A larger
        // n gives a smaller p-value floor, so the benchmark with the largest n is the
        // hardest one to make this claim about — reporting its floor is the strongest
        // statement that still holds for every benchmark in the set. Do not swap this for
        // Math.Min: that would pick the smallest n, overstate the floor, and make the
        // message wrong.
        var largestN = 0;
        var alpha = 0.0;

        foreach (var d in deltas)
        {
            var corrected = d.Alpha / k;
            if (MannWhitney.MinimumAttainableP(d.NBase, d.NCand) < corrected)
                return null;  // this one can clear it, which is enough for a KEEP

            var n = Math.Min(d.NBase, d.NCand);
            if (n > largestN) { largestN = n; alpha = d.Alpha; }
        }

        var corrected2 = alpha / k;

        // Assumes NBase == NCand for the reported delta (true today: both sides always run
        // the same configured round count). MinimumAttainableP(largestN, largestN) is only
        // the true floor for that delta when its two sides are equal; if base and candidate
        // counts could ever differ, this would need to report each delta's own
        // MinimumAttainableP(NBase, NCand) instead of re-deriving it from a single n.
        var floor = MannWhitney.MinimumAttainableP(largestN, largestN);
        var need = CountForAlpha(corrected2);

        var msg =
            Inv($"no KEEP was reachable: comparing {k} benchmark(s) corrects the significance ") +
            Inv($"threshold to {corrected2:F5}, but with {largestN} rounds per side the test cannot ") +
            Inv($"produce a p-value below {floor:F5} however large the improvement is");

        return need > 0
            ? msg + Inv($" — raise count to at least {need}")
            : msg + " — raise count, or measure fewer benchmarks";
    }

    /// <summary>Smallest rounds-per-side whose p-value floor clears the given threshold.</summary>
    private static int CountForAlpha(double alpha)
    {
        for (var n = 2; n <= 200; n++)
        {
            if (MannWhitney.MinimumAttainableP(n, n) < alpha) return n;
        }
        return 0;
    }
}
