using Autor3Search.Core.Bench;
using Autor3Search.Core.Configuration;
using Autor3Search.Core.Discovery;
using Autor3Search.Core.Freezing;
using Autor3Search.Core.Measuring;
using Autor3Search.Core.Processes;
using Autor3Search.Core.RunState;
using Autor3Search.Core.Scoping;
using Autor3Search.Core.Verdicts;
using Autor3Search.Core.SourceControl;

namespace Autor3Search.Core.Pipelines;

/// <summary>Everything one evaluation needs. The caller resolves all of it first.</summary>
/// <param name="Root">The repository being evaluated.</param>
/// <param name="Store">Out-of-tree run state for this tag.</param>
/// <param name="Config">The run configuration, already validated.</param>
/// <param name="Baseline">The reference point. MeasureCommit may be advanced as a side effect of KEEP.</param>
/// <param name="Log">Receives subprocess output. May be null.</param>
public sealed record PipelineOptions(
    string Root,
    StateStore Store,
    RunConfig Config,
    Baseline Baseline,
    TextWriter? Log);

/// <summary>
/// Every unit compared for one experiment.
///
/// Time is the scored metric and the only one the verdict ever sees. Bytes is a hint:
/// program.md points an agent at allocation as the most common lead, and a human
/// reading results.tsv uses it to see WHY something got faster. Keep that boundary —
/// scoring Bytes would let allocation-only changes with no latency win pass as KEEP.
/// </summary>
/// <param name="Time">Per-benchmark ns/op deltas. Scored.</param>
/// <param name="Bytes">Per-benchmark B/op deltas. Informational, and null when unavailable.</param>
public sealed record Measurements(IReadOnlyList<Delta> Time, IReadOnlyList<Delta>? Bytes);

/// <summary>The result of one evaluation.</summary>
/// <param name="Verdict">The decision.</param>
/// <param name="Measurements">Present only when measurement ran.</param>
/// <param name="Baseline">The baseline as it stands after the evaluation, advanced on KEEP.</param>
public sealed record EvalOutcome(VerdictResult Verdict, Measurements? Measurements, Baseline Baseline);

/// <summary>Runs one full evaluation: gate correctness, measure, score.</summary>
public static class Pipeline
{
    /// <summary>
    /// Gates, measures and scores one experiment.
    ///
    /// Returns a terminal verdict for every gate outcome and every completed
    /// measurement. An exception means the harness itself malfunctioned — I/O, git, a
    /// malformed baseline — not that the candidate was rejected.
    /// </summary>
    public static async Task<EvalOutcome> EvalAsync(PipelineOptions o, CancellationToken ct)
    {
        var timeout = o.Config.TimeoutDuration;

        // Resolved ONCE, here, and used for every gate, build, test and measurement
        // below, and for the KEEP advance at the very end. The agent is autonomous and
        // nothing stops it committing while a multi-minute eval runs; if the advance
        // step re-sampled HEAD after build/test/measure instead of reusing this value,
        // it could point the measurement baseline at a commit that was never scope-
        // gated, never built, never tested and never measured — exactly the unexamined-
        // code-becomes-accepted-baseline-state failure this pipeline exists to prevent.
        var candidate = await Git.HeadCommitAsync(o.Root, ct).ConfigureAwait(false);

        // 1. Scope, before anything is restored or built, so an out-of-scope edit is
        //    reported as itself rather than as a confusing build error.
        //
        //    Diffed against Baseline.Commit — the FROZEN anchor — and never against
        //    MeasureCommit, which moves after every KEEP. Anchoring here means the FULL
        //    accumulated diff is re-validated on every eval. Anchoring to the advancing
        //    commit would give an out-of-scope edit exactly one eval in which to be
        //    caught; past that it becomes "already accepted" state and is never looked
        //    at again.
        var changed = await Git.ChangedSinceAsync(o.Root, o.Baseline.Commit, ct).ConfigureAwait(false);
        var scope = new ScopeMatcher(o.Config.Scope);

        var frozenFiles = new HashSet<string>(
            Manifest.Load(o.Store.ManifestPath).Files.Keys, StringComparer.Ordinal);

        foreach (var rel in changed)
        {
            // Frozen files are handled by restore, not by any gate. This check comes
            // FIRST so that a test or benchmark .csproj — which is frozen — is silently
            // restored like every other frozen file, rather than being reported as a
            // dependency change by the check below.
            if (frozenFiles.Contains(rel)) continue;

            if (IsDependencyFile(rel))
            {
                return Gate(o, VerdictStatus.Fail, Reasons.DependencyChanged,
                    $"{rel} may not be modified: a dependency change is a human supply-chain " +
                    "decision, not an autonomous one, and it changes what is being measured " +
                    "rather than how fast it runs");
            }

            if (rel == Reporting.ResultsFile.RelativePath
                || rel == Reporting.ResultsFile.RunLogName
                || rel == RunConfig.RelativePath)
            {
                continue;   // harness output, plus the config, integrity-checked below
            }

            if (!scope.Match(rel))
            {
                return Gate(o, VerdictStatus.Fail, Reasons.Scope,
                    $"{rel} is outside the allowed scope [{string.Join(", ", o.Config.Scope)}]");
            }
        }

        // 1b. Config integrity. config.yaml lives in the repo because humans own it,
        //     which means the agent can reach it. Raising max_regress_pct or dropping a
        //     benchmark would defeat the guards, so it is hashed at baseline.
        var configPath = Path.Combine(o.Root, Paths.FromSlash(RunConfig.RelativePath));
        if (Freezer.HashFile(configPath) != o.Baseline.ConfigSha256)
        {
            return Gate(o, VerdictStatus.Fail, Reasons.ConfigChanged,
                $"{RunConfig.RelativePath} changed since baseline — the scoring rules are fixed " +
                "for a run. Revert it, or start a new run with 'autor3search-csharp baseline'.");
        }

        // 2. Restore. Agent edits to frozen files are erased, not argued with.
        var manifest = Manifest.Load(o.Store.ManifestPath);
        IReadOnlyList<string> restored;
        try
        {
            restored = Freezer.Restore(o.Root, o.Store.FrozenStorePath, manifest);
        }
        catch (SymlinkRefusedException ex)
        {
            return Gate(o, VerdictStatus.Fail, Reasons.SymlinkSwap,
                $"{ex.Message} — restore them as regular files and rerun");
        }
        catch (StoreTamperedException ex)
        {
            return Gate(o, VerdictStatus.Fail, Reasons.FrozenTampered,
                $"{ex.Message} — the frozen copy this run scores against was modified, so its " +
                "tests can no longer be trusted. Unlike a working-tree change this cannot be " +
                "undone, because the reference itself was lost. Start a fresh run with " +
                "'autor3search-csharp baseline'.");
        }

        if (restored.Count > 0)
            o.Log?.WriteLine($"restored {restored.Count} frozen file(s): {string.Join(", ", restored)}");

        // 2b. The frozen set and what is on disk must agree in BOTH directions.
        //
        //     Restore only rewrites files it froze, and the scope gate skips every
        //     frozen path, so without the first check an agent could ADD a new test or
        //     benchmark file and neither gate would notice.
        //
        //     The reverse direction is easier to miss and matters as much: a file
        //     Restore just rewrote should be visible to the walker again. A manifest
        //     entry MISSING from the walk means the tree changed shape around it — it
        //     is nominally restored but would not actually run, and the run would keep
        //     scoring against a benchmark set that no longer executes.
        //
        //     This direction is UNREACHABLE by construction under Discoverer.FrozenFiles'
        //     current contract: Freezer.Restore unconditionally recreates every manifest
        //     entry's full ancestor chain at its exact original, baseline-recorded path
        //     (or throws, which an earlier gate catches), and FrozenFiles enumerates from
        //     that same fixed project path — its dot/underscore/skip-list rules apply only
        //     to subdirectories reached by recursion, never to the root path it is handed.
        //     There is no way to hide a frozen file from a walk that starts exactly where
        //     the file provably still is. It becomes a live gate again the moment
        //     FrozenFiles' contract changes to a root walk applying those skip rules to the
        //     whole path — which is exactly what the Go tool this ports from does. Kept
        //     deliberately as defence-in-depth against that contract changing underneath
        //     it, not left behind by accident.
        var frozenProjects = o.Baseline.FrozenProjects;
        var present = new HashSet<string>(
            Discoverer.FrozenFiles(o.Root, frozenProjects, o.Config.Unfreeze), StringComparer.Ordinal);

        var added = present.Where(p => !frozenFiles.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (added.Count > 0)
        {
            return Gate(o, VerdictStatus.Fail, Reasons.NewFrozenFile,
                $"files not present at baseline: {string.Join(", ", added)} — the test and " +
                "benchmark set is frozen. Add them before running 'baseline', or list them " +
                "under unfreeze in the config.");
        }

        var missing = frozenFiles.Where(f => !present.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            return Gate(o, VerdictStatus.Fail, Reasons.MissingFrozenFile,
                $"frozen files are no longer discoverable in the working tree: " +
                $"{string.Join(", ", missing)} — they were restored, but the walk that finds " +
                "them cannot reach them, so they would not run. Check for a directory on their " +
                "path that was renamed, moved, or hidden under a dot-prefixed name.");
        }

        var runner = new Runner(o.Root, timeout, o.Log);

        // 3. Build.
        var buildArgs = new List<string> { "build", "-c", "Release", "--nologo" };
        if (o.Config.WarningsAsErrors) buildArgs.Add("-warnaserror");

        var build = await runner.RunAsync("dotnet", buildArgs, ct).ConfigureAwait(false);
        if (build.TimedOut)
            return Gate(o, VerdictStatus.Crash, Reasons.Timeout, $"dotnet build timed out after {timeout}");
        if (!build.OK)
            return Gate(o, VerdictStatus.Crash, Reasons.Build, build.Tail(30));

        // 4. Tests. Correctness is never traded for speed.
        var test = await runner.RunAsync("dotnet",
            ["test", "-c", "Release", "--no-build", "--nologo"], ct).ConfigureAwait(false);
        if (test.TimedOut)
            return Gate(o, VerdictStatus.Crash, Reasons.Timeout, $"dotnet test timed out after {timeout}");
        if (!test.OK)
            return Gate(o, VerdictStatus.Fail, Reasons.Tests, test.Tail(40));

        // 4b. Baseline worktree integrity.
        //
        //     An agent could edit the pinned worktree in place to make the BASELINE
        //     slow, after which every candidate "improves" and every experiment returns
        //     KEEP without optimizing anything.
        //
        //     Compared against MeasureCommit, not Commit: the worktree is legitimately
        //     re-pointed after every KEEP, so comparing against the frozen anchor would
        //     fire spuriously on the very next eval.
        //
        //     This is a DETECTION and only a partial one. The state directory is outside
        //     the repository, but the agent runs as the same OS user, so nothing stops
        //     it editing the worktree; this catches that only if HEAD itself moves.
        //     Treat it as catching accidental clobbering and a careless tamper, not as a
        //     guarantee.
        var worktreeHead = await Git.HeadCommitAsync(o.Store.WorktreePath, ct).ConfigureAwait(false);
        if (worktreeHead != o.Baseline.MeasureCommit)
        {
            return Gate(o, VerdictStatus.Fail, Reasons.BaselineTampered,
                $"the pinned baseline worktree is at {worktreeHead[..7]} but the run records " +
                $"{o.Baseline.MeasureCommit[..7]} as the measurement commit. Something moved it, " +
                "and measurements against it can no longer be trusted.");
        }

        // 5. Measure.
        var filter = BuildFilter(o.Config);
        var (baseSet, candSet) = await Measurer.RunAsync(new MeasureOptions(
            BaseDir: o.Store.WorktreePath,
            CandDir: o.Root,
            BenchmarkProject: o.Config.BenchmarkProject,
            Filter: filter,
            Job: o.Config.Job,
            InProcess: o.Config.InProcess,
            Rounds: o.Config.Count,
            Warmup: o.Config.Warmup,
            Timeout: timeout,
            Log: o.Log), ct).ConfigureAwait(false);

        // 6. Score.
        var timeDeltas = Stats.CompareAll(baseSet, candSet, Units.TimeNs);
        var score = Stats.GeoMean(timeDeltas);

        IReadOnlyList<Delta>? byteDeltas = null;
        try
        {
            byteDeltas = Stats.CompareAll(baseSet, candSet, Units.BytesPerOp);
        }
        catch (InvalidOperationException)
        {
            // A missing hint is never a reason to discard an otherwise-valid experiment.
        }

        var verdict = Verdict.Decide(new VerdictInput(
            timeDeltas, score, o.Config.MaxRegressPct, o.Config.MinEffectPct));

        var baseline = o.Baseline;

        if (verdict.Status == VerdictStatus.Keep)
        {
            // Re-read HEAD rather than trusting `candidate` still describes the working
            // tree: everything above — scope, restore, build, test, measure — ran
            // against whatever was at `candidate` when this eval started, but that is
            // now several minutes in the past. If HEAD moved in the meantime, advancing
            // to either commit would be wrong: advancing to `candidate` would encode a
            // stale git ref the repository has already moved past, and advancing to
            // whatever HEAD is NOW would accept a commit that was never gated, built,
            // tested or measured. Neither is safe, so this fails instead and asks for a
            // clean re-run.
            var headNow = await Git.HeadCommitAsync(o.Root, ct).ConfigureAwait(false);
            if (headNow != candidate)
            {
                // Deliberately Reasons.CandidateMoved, not Reasons.BaselineTampered: the
                // two demand opposite remedies. BaselineTampered means the HARNESS's own
                // pinned state cannot be trusted and the honest fix is a fresh baseline.
                // Here the harness state is fine — the AGENT's own HEAD moved during a
                // multi-minute evaluation — and the fix is simply not to commit while an
                // eval is in flight, then re-run. Reporting this as BaselineTampered would
                // tell the agent to tear down a healthy run and lose every kept commit's
                // measurement lineage over its own mid-flight commit.
                return Gate(o, VerdictStatus.Fail, Reasons.CandidateMoved,
                    $"HEAD changed from {candidate[..7]} to {headNow[..7]} between the start of " +
                    "this evaluation and the point where its result would be banked — the commit " +
                    "that was gated, built, tested and measured is no longer the commit that " +
                    "would be accepted. Nothing was changed. Re-run the experiment, and do not " +
                    "commit while an eval is in flight.");
            }

            baseline = await AdvanceMeasurementBaselineAsync(o, candidate, ct).ConfigureAwait(false);
        }

        return new EvalOutcome(verdict, new Measurements(timeDeltas, byteDeltas), baseline);
    }

    /// <summary>
    /// Re-points the measurement baseline at the newly kept commit and moves the pinned
    /// worktree to match.
    ///
    /// Without this, once one real improvement was kept, every later experiment kept
    /// comparing against the same stale starting point — and a no-op could coast to
    /// KEEP on an earlier win it did not contribute to. The frozen anchor is untouched.
    ///
    /// <paramref name="candidate"/> is the single commit value resolved once at the top
    /// of <see cref="EvalAsync"/> and already confirmed, by the caller, to still match
    /// HEAD — not re-sampled here, so this can never advance to a commit that was not
    /// the one actually gated and measured.
    ///
    /// There is a narrow window between the worktree move below succeeding and
    /// <see cref="StateStore.SaveBaseline(Baseline)"/> completing. If the process dies
    /// in that window, the worktree is already at `candidate` but the saved baseline
    /// still names the old MeasureCommit — so the NEXT eval's own worktree-integrity
    /// check (4b) will see them disagree and report Reasons.BaselineTampered for what
    /// was really an interrupted write, not tampering. Not fixed here — flagged so a
    /// future reader does not mistake that report for an attack.
    /// </summary>
    private static async Task<Baseline> AdvanceMeasurementBaselineAsync(
        PipelineOptions o, string candidate, CancellationToken ct)
    {
        await Git.MoveWorktreeToAsync(o.Store.WorktreePath, candidate, ct).ConfigureAwait(false);

        var advanced = o.Baseline with { MeasureCommit = candidate };
        o.Store.SaveBaseline(advanced);

        o.Log?.WriteLine($"measurement baseline advanced to {candidate[..7]}");
        return advanced;
    }

    /// <summary>
    /// The BenchmarkDotNet --filter for the declared set. An empty declaration means
    /// every discovered benchmark.
    /// </summary>
    private static string BuildFilter(RunConfig config) =>
        config.Benchmarks.Count == 0 ? "*" : string.Join("|", config.Benchmarks);

    private static bool IsDependencyFile(string rel)
    {
        var name = rel.Contains('/') ? rel[(rel.LastIndexOf('/') + 1)..] : rel;

        if (Discoverer.DependencyFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            return true;

        // A .csproj is frozen only when it belongs to a test or benchmark project; any
        // OTHER .csproj carries PackageReference elements too, so it is a dependency
        // surface and is refused on the same grounds.
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static EvalOutcome Gate(
        PipelineOptions o, VerdictStatus status, string reason, string message) =>
        new(Verdict.Gate(status, reason, message), null, o.Baseline);
}
