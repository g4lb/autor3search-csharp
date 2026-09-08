using Autor3Search.Core.Bench;
using Autor3Search.Core.Verdicts;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for <see cref="Verdict"/>: the KEEP/DISCARD decision table.</summary>
public class VerdictTests
{
    private static Delta D(string name, double pctChange, double p, bool significant,
                           int n = 10, double alpha = 0.05) =>
        new(name, Units.TimeNs, 1000, 1000 * (1 + pctChange / 100), 1 + pctChange / 100,
            pctChange, p, alpha, significant, n, n, []);

    private static VerdictInput In(params Delta[] deltas) =>
        new(deltas, Stats.GeoMean(deltas), MaxRegressPct: 5.0, MinEffectPct: 1.0);

    /// <summary>The exit code for each status is fixed by contract: 0/1/2/3.</summary>
    [Fact]
    public void ExitCodesAreFixedByContract()
    {
        Assert.Equal(0, VerdictStatus.Keep.ExitCode());
        Assert.Equal(1, VerdictStatus.Discard.ExitCode());
        Assert.Equal(2, VerdictStatus.Fail.ExitCode());
        Assert.Equal(3, VerdictStatus.Crash.ExitCode());
    }

    /// <summary>A large, significant improvement is kept.</summary>
    [Fact]
    public void AClearWinIsKept()
    {
        var r = Verdict.Decide(In(D("A.One", -40, 0.0001, true)));
        Assert.Equal(VerdictStatus.Keep, r.Status);
        Assert.Equal(Reasons.Improved, r.Reason);
    }

    /// <summary>An insignificant, near-zero change is discarded as no improvement.</summary>
    [Fact]
    public void NoiseIsDiscardedAsNoImprovement()
    {
        var r = Verdict.Decide(In(D("A.One", -0.2, 0.6, false)));
        Assert.Equal(VerdictStatus.Discard, r.Status);
        Assert.Equal(Reasons.NoImprovement, r.Reason);
    }

    // A real but tiny win discards with its OWN reason. Reporting it as
    // "no improvement" would tell an agent its change did nothing when it
    // measurably did — the idea was directionally right, just too small to bank.
    /// <summary>A significant but sub-threshold improvement discards as BelowMinEffect, not NoImprovement.</summary>
    [Fact]
    public void ARealButTinyWinDiscardsAsBelowMinEffect()
    {
        var r = Verdict.Decide(In(D("A.One", -0.5, 0.0001, true)));
        Assert.Equal(VerdictStatus.Discard, r.Status);
        Assert.Equal(Reasons.BelowMinEffect, r.Reason);
        Assert.Contains("below the 1.0% minimum effect size", r.Message);
    }

    /// <summary>A significant regression beyond the limit discards even with a strong overall score.</summary>
    [Fact]
    public void ASignificantRegressionBeyondTheLimitIsDiscardedEvenWithAGoodScore()
    {
        // Overall geomean is a strong win, but one benchmark regressed 20%.
        var r = Verdict.Decide(In(
            D("A.Fast", -60, 0.0001, true),
            D("B.Slow", +20, 0.001, true)));

        Assert.Equal(VerdictStatus.Discard, r.Status);
        Assert.Equal(Reasons.GuardRegression, r.Reason);
        Assert.Single(r.Regressions);
        Assert.Equal("B.Slow", r.Regressions[0].Name);
        Assert.Contains("B.Slow", r.Message);
    }

    /// <summary>A regression within MaxRegressPct does not trip the guard.</summary>
    [Fact]
    public void ARegressionWithinTheLimitDoesNotTripTheGuard()
    {
        var r = Verdict.Decide(In(
            D("A.Fast", -60, 0.0001, true),
            D("B.Slow", +3, 0.001, true)));

        Assert.Equal(VerdictStatus.Keep, r.Status);
        Assert.Empty(r.Regressions);
    }

    /// <summary>A regression that is not statistically significant does not trip the guard.</summary>
    [Fact]
    public void AnInsignificantRegressionDoesNotTripTheGuard()
    {
        var r = Verdict.Decide(In(
            D("A.Fast", -60, 0.0001, true),
            D("B.Slow", +30, 0.4, significant: false)));

        Assert.Equal(VerdictStatus.Keep, r.Status);
    }

    // The guard uses the RAW alpha, never the Bonferroni-corrected one. Bonferroni
    // only makes significance harder to reach, which would make real regressions
    // easier to miss — backwards from what a guard is for.
    /// <summary>The regression guard checks against the raw alpha, not the Bonferroni-corrected one.</summary>
    [Fact]
    public void TheRegressionGuardUsesRawAlphaNotTheCorrectedOne()
    {
        // p = 0.04 clears raw alpha 0.05 but not the corrected 0.05/8 = 0.00625.
        var deltas = new[]
        {
            D("A.Fast", -60, 0.0001, true),
            D("B.Slow", +40, 0.04, significant: true),
            D("C.1", -1, 0.5, false), D("D.1", -1, 0.5, false),
            D("E.1", -1, 0.5, false), D("F.1", -1, 0.5, false),
            D("G.1", -1, 0.5, false), D("H.1", -1, 0.5, false),
        };
        var r = Verdict.Decide(new VerdictInput(deltas, Stats.GeoMean(deltas), 5.0, 1.0));

        Assert.Equal(VerdictStatus.Discard, r.Status);
        Assert.Equal(Reasons.GuardRegression, r.Reason);
    }

    // KEEP requires alpha/k, not alpha. With 4 benchmarks a p of 0.03 clears the raw
    // threshold but not 0.05/4 = 0.0125.
    /// <summary>KEEP requires the Bonferroni-corrected threshold, not the raw alpha.</summary>
    [Fact]
    public void KeepRequiresTheBonferroniCorrectedThreshold()
    {
        var deltas = new[]
        {
            D("A.One", -30, 0.03, significant: true),
            D("B.Two", -0.1, 0.9, false),
            D("C.Three", -0.1, 0.9, false),
            D("D.Four", -0.1, 0.9, false),
        };
        var r = Verdict.Decide(new VerdictInput(deltas, Stats.GeoMean(deltas), 5.0, 1.0));

        Assert.Equal(VerdictStatus.Discard, r.Status);
        Assert.Equal(Reasons.NoImprovement, r.Reason);
    }

    /// <summary>One benchmark clearing the corrected threshold is enough for a KEEP.</summary>
    [Fact]
    public void OneBenchmarkClearingTheCorrectedThresholdIsEnough()
    {
        var deltas = new[]
        {
            D("A.One", -30, 0.001, significant: true),
            D("B.Two", -0.1, 0.9, false),
            D("C.Three", -0.1, 0.9, false),
            D("D.Four", -0.1, 0.9, false),
        };
        var r = Verdict.Decide(new VerdictInput(deltas, Stats.GeoMean(deltas), 5.0, 1.0));
        Assert.Equal(VerdictStatus.Keep, r.Status);
    }

    // With 5 rounds per side the U test floor is 2/C(10,5) = 0.00794. Seven
    // benchmarks correct alpha to 0.05/7 = 0.00714, below that floor — so no
    // experiment can ever be kept and the run must say so.
    /// <summary>When no benchmark's sample size can ever clear the corrected threshold, the run warns.</summary>
    [Fact]
    public void AnUnreachableKeepThresholdWarns()
    {
        var deltas = Enumerable.Range(0, 7)
            .Select(i => D($"B{i}.M", -30, 0.008, significant: true, n: 5))
            .ToArray();
        var r = Verdict.Decide(new VerdictInput(deltas, Stats.GeoMean(deltas), 5.0, 1.0));

        Assert.Contains(r.Warnings, w => w.Contains("no KEEP was reachable"));
        // Hand-verified: alpha/k = 0.05/7 = 0.0071429; the n=5 floor is 2/C(10,5) = 0.0079365
        // (does not clear); the n=6 floor is 2/C(12,6) = 0.0021645 (clears) — so 6 is the
        // smallest n that reaches the corrected threshold.
        Assert.Contains(r.Warnings, w => w.Contains("raise count to at least 6"));
    }

    /// <summary>When sample sizes are large enough to reach the corrected threshold, no warning fires.</summary>
    [Fact]
    public void AReachableThresholdDoesNotWarn()
    {
        var deltas = Enumerable.Range(0, 7)
            .Select(i => D($"B{i}.M", -30, 0.0001, significant: true, n: 10))
            .ToArray();
        var r = Verdict.Decide(new VerdictInput(deltas, Stats.GeoMean(deltas), 5.0, 1.0));

        Assert.DoesNotContain(r.Warnings, w => w.Contains("no KEEP was reachable"));
    }

    /// <summary>Warnings inform but never change the underlying decision.</summary>
    [Fact]
    public void WarningsNeverChangeTheDecision()
    {
        var deltas = Enumerable.Range(0, 7)
            .Select(i => D($"B{i}.M", -30, 0.008, significant: true, n: 5))
            .ToArray();
        var r = Verdict.Decide(new VerdictInput(deltas, Stats.GeoMean(deltas), 5.0, 1.0));

        Assert.NotEmpty(r.Warnings);
        Assert.Equal(VerdictStatus.Discard, r.Status);  // discarded on the rules, not the warning
    }

    /// <summary>Identical per-comparison warnings are deduplicated in the result.</summary>
    [Fact]
    public void PerComparisonWarningsAreCarriedThroughDeduplicated()
    {
        var shared = "need >= 6 observations for a confidence interval at level 0.95, have 4";
        var deltas = new[]
        {
            D("A.One", -40, 0.0001, true) with { Warnings = [shared] },
            D("B.Two", -40, 0.0001, true) with { Warnings = [shared] },
        };
        var r = Verdict.Decide(new VerdictInput(deltas, Stats.GeoMean(deltas), 5.0, 1.0));

        Assert.Single(r.Warnings, w => w == shared);
    }

    /// <summary>Gate builds a terminal result with a zero score and no warnings, before any measurement.</summary>
    [Fact]
    public void GateBuildsATerminalResultWithoutMeasurement()
    {
        var r = Verdict.Gate(VerdictStatus.Fail, Reasons.Scope, "src/x.cs is outside scope");
        Assert.Equal(VerdictStatus.Fail, r.Status);
        Assert.Equal(Reasons.Scope, r.Reason);
        Assert.Equal(0.0, r.Score);
        Assert.Empty(r.Warnings);
    }

    /// <summary>An empty delta set does not divide by zero and discards.</summary>
    [Fact]
    public void AnEmptyDeltaSetDoesNotDivideByZero()
    {
        var r = Verdict.Decide(new VerdictInput([], 1.0, 5.0, 1.0));
        Assert.Equal(VerdictStatus.Discard, r.Status);
    }

    // Pins the regression guard's strict `>` comparison: PctChange == MaxRegressPct exactly
    // must NOT trip it. A one-character slip to `>=` would discard this instead of keeping it.
    /// <summary>A regression exactly at MaxRegressPct does not trip the guard.</summary>
    [Fact]
    public void ARegressionExactlyAtTheLimitDoesNotTripTheGuard()
    {
        var r = Verdict.Decide(In(
            D("A.Fast", -60, 0.0001, true),
            D("B.AtLimit", 5.0, 0.001, true)));

        Assert.Equal(VerdictStatus.Keep, r.Status);
        Assert.Empty(r.Regressions);
    }

    // Pins rule 2a's strict `<` comparison: Score == 1 - MinEffectPct/100 exactly must NOT
    // qualify as KEEP. A one-character slip to `<=` would keep a change that is only as good
    // as the minimum effect floor, not strictly better than it.
    /// <summary>A score exactly at the min-effect threshold discards as BelowMinEffect, not Keep.</summary>
    [Fact]
    public void AScoreExactlyAtTheMinEffectThresholdDiscardsAsBelowMinEffect()
    {
        var deltas = new[] { D("A.One", -30, 0.0001, significant: true) };
        var input = new VerdictInput(deltas, Score: 0.99, MaxRegressPct: 5.0, MinEffectPct: 1.0);
        var r = Verdict.Decide(input);

        Assert.Equal(VerdictStatus.Discard, r.Status);
        Assert.Equal(Reasons.BelowMinEffect, r.Reason);
    }

    // Pins rule 2b's strict `<` comparison: P == Alpha/k exactly must NOT count as improved.
    // A one-character slip to `<=` would let a p-value sitting exactly on the corrected
    // threshold pass, defeating the point of having a threshold. Score is set well below the
    // min-effect line so a wrongly-true `improved` would show up as an incorrect KEEP.
    /// <summary>A p-value exactly at the corrected alpha does not count as improved.</summary>
    [Fact]
    public void APValueExactlyAtTheCorrectedThresholdDoesNotCountAsImproved()
    {
        var deltas = new[] { D("A.One", -30, 0.05, significant: true) };
        var input = new VerdictInput(deltas, Score: 0.5, MaxRegressPct: 5.0, MinEffectPct: 1.0);
        var r = Verdict.Decide(input);

        Assert.Equal(VerdictStatus.Discard, r.Status);
        Assert.Equal(Reasons.NoImprovement, r.Reason);
    }
}
