using Autor3Search.Core.Reporting;
using Autor3Search.Core.RunState;
using Autor3Search.Core.SourceControl;

namespace Autor3Search.Cli;

/// <summary>Reports where a run is. Read-only: checking on a run cannot change it.</summary>
internal static class StatusCommand
{
    /// <summary>Runs `status`. Returns 0, or 2 when there is no such run.</summary>
    public static async Task<int> RunAsync(
        Args args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var start = Path.GetFullPath(args.GetString("C") ?? Directory.GetCurrentDirectory());
        var repo = await Git.RootAsync(start, ct);
        var tag = await RunTag.ResolveAsync(args, repo, ct);

        var store = new StateStore(repo, tag);
        if (!store.Exists)
        {
            stderr.WriteLine($"no run found for tag \"{tag}\" — nothing recorded at {store.Root}");
            return 2;
        }

        var baseline = store.LoadBaseline();
        var branch = StateStore.BranchForTag(tag);
        var current = await Git.CurrentBranchAsync(repo, ct);
        var rows = ResultsFile.Load(Path.Combine(repo, ResultsFile.RelativePath));
        var summary = ResultsFile.Summarize(rows);

        var claim = store.ReadClaim();
        var alive = store.IsClaimAlive();

        stdout.WriteLine($"run tag        {tag}");
        stdout.WriteLine($"branch         {branch}{(current == branch ? "  (checked out)" : "")}");
        stdout.WriteLine($"baseline       {baseline.Commit[..7]}  (run started here)");

        stdout.WriteLine(baseline.MeasureCommit == baseline.Commit
            ? $"measuring vs   {baseline.MeasureCommit[..7]}  (still at the baseline)"
            : $"measuring vs   {baseline.MeasureCommit[..7]}  (advanced past the baseline by earlier KEEPs)");

        stdout.WriteLine(Directory.Exists(store.WorktreePath)
            ? $"worktree       {store.WorktreePath}"
            : $"worktree       {store.WorktreePath}  (missing — the run cannot measure until this is restored)");
        stdout.WriteLine(
            $"experiments    {summary.Total} run  ({summary.Keeps} keep, {summary.Discards} discard, " +
            $"{summary.Fails} fail, {summary.Crashes} crash)  — next is #{ResultsFile.NextExperimentNumber(Path.Combine(repo, ResultsFile.RelativePath))}");

        stdout.WriteLine(alive
            ? $"eval           running (pid {claim}) — a process matching the recorded pid is running"
            : claim is not null
                ? $"eval           not running (a stale claim for pid {claim} was left by an eval that did not exit cleanly)"
                : "eval           not running");

        stdout.WriteLine(store.ForceStopRequested
            ? "stop           requested, and the running experiment has been asked to abandon"
            : store.StopRequested
                ? "stop           requested — the run ends after the current experiment"
                : "stop           not requested");

        if (!store.StopRequested)
        {
            stdout.WriteLine();
            stdout.WriteLine("to stop after the current experiment:  autor3search-csharp stop");
            stdout.WriteLine("to stop now, abandoning it:            autor3search-csharp stop -force");
        }

        return 0;
    }
}
