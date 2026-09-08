using System.Runtime.InteropServices;
using System.Text.Json;
using Autor3Search.Core;
using Autor3Search.Core.Configuration;
using Autor3Search.Core.Pipelines;
using Autor3Search.Core.Reporting;
using Autor3Search.Core.RunState;
using Autor3Search.Core.SourceControl;
using Autor3Search.Core.Verdicts;

namespace Autor3Search.Cli;

/// <summary>Runs one experiment through <see cref="Pipeline"/> and reports the verdict.</summary>
internal static partial class EvalCommand
{
    /// <summary>
    /// Runs `eval`. Holds the run claim for its lifetime, clearing it on every exit
    /// path including cancellation. Exit code is <c>verdict.Status.ExitCode()</c>,
    /// except a force-stop abandonment, which exits 2 having recorded nothing.
    /// </summary>
    public static async Task<int> RunAsync(
        Args args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var start = Path.GetFullPath(args.GetString("C") ?? Directory.GetCurrentDirectory());
        var repo = await Git.RootAsync(start, ct).ConfigureAwait(false);
        var tag = await RunTag.ResolveAsync(args, repo, ct).ConfigureAwait(false);

        var store = new StateStore(repo, tag);
        if (!store.Exists)
        {
            stderr.WriteLine($"no run found for tag \"{tag}\" — nothing recorded at {store.Root}. " +
                "Run 'autor3search-csharp baseline -tag <tag>' first.");
            return 2;
        }

        var claimPid = store.ReadClaim();
        if (store.IsClaimAlive())
        {
            stderr.WriteLine($"an eval is already running for tag \"{tag}\" (pid {claimPid}). " +
                "Only one eval may run at a time for a given run.");
            return 2;
        }

        store.WriteClaim(Environment.ProcessId);
        try
        {
            return await RunClaimedAsync(args, repo, tag, store, stdout, stderr, ct).ConfigureAwait(false);
        }
        finally
        {
            // A crash between WriteClaim and here must not wedge the run for every
            // later eval, so this always runs, success or failure.
            store.ClearClaim();
        }
    }

    private static async Task<int> RunClaimedAsync(
        Args args, string repo, string tag, StateStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var configPath = Path.Combine(repo, Paths.FromSlash(RunConfig.RelativePath));
        var config = RunConfig.Load(configPath);
        var baseline = store.LoadBaseline();

        var experiment = ResultsFile.NextExperimentNumber(Path.Combine(repo, ResultsFile.RelativePath));
        var commit = await Git.HeadCommitAsync(repo, ct).ConfigureAwait(false);

        var logPath = Path.Combine(repo, ResultsFile.RunLogName);
        await using var logStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var log = new StreamWriter(logStream) { AutoFlush = true };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watchStop = new CancellationTokenSource();
        var watcher = WatchForForceStopAsync(store, cts, watchStop.Token);
        using var shutdown = RegisterShutdown(cts);

        EvalOutcome outcome;
        try
        {
            var options = new PipelineOptions(repo, store, config, baseline, log);
            outcome = await Pipeline.EvalAsync(options, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The experiment was abandoned mid-flight — by a force stop, or by SIGINT/
            // SIGTERM/Ctrl+C. Either way nothing was measured, so there is nothing to
            // record: writing a results.tsv row here would claim a verdict that was
            // never reached.
            stderr.WriteLine($"experiment {experiment} abandoned before completion — nothing was " +
                "measured, so nothing was recorded in results.tsv.");
            return 2;
        }
        finally
        {
            await watchStop.CancelAsync().ConfigureAwait(false);
            try { await watcher.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on the watcher's own shutdown */ }
        }

        var stopRequested = store.StopRequested;

        ResultsFile.Append(Path.Combine(repo, ResultsFile.RelativePath), new ResultsRow(
            Experiment: experiment,
            Timestamp: DateTimeOffset.UtcNow,
            Commit: commit,
            Status: outcome.Verdict.Status.ToWireString(),
            Reason: outcome.Verdict.Reason,
            Score: outcome.Verdict.Score,
            Message: outcome.Verdict.Message,
            Version: ThisAssembly.InformationalVersion));

        if (args.GetFlag("json"))
        {
            WriteJson(stdout, outcome, experiment, commit, stopRequested);
        }
        else
        {
            WriteHuman(stdout, outcome, experiment, commit, stopRequested);
        }

        return outcome.Verdict.Status.ExitCode();
    }

    private static void WriteHuman(
        TextWriter stdout, EvalOutcome outcome, int experiment, string commit, bool stopRequested)
    {
        stdout.WriteLine($"experiment     {experiment}");
        stdout.WriteLine($"commit         {commit[..7]}");
        stdout.WriteLine($"status         {outcome.Verdict.Status.ToWireString()}");
        stdout.WriteLine($"reason         {outcome.Verdict.Reason}");
        stdout.WriteLine($"score          {outcome.Verdict.Score:F4}");
        stdout.WriteLine($"message        {outcome.Verdict.Message}");

        if (stopRequested)
            stdout.WriteLine("stop requested — this was the last experiment before the agent should stop");

        foreach (var w in outcome.Verdict.Warnings)
            stdout.WriteLine($"warning        {w}");

        var deltas = outcome.Measurements?.Time;
        if (deltas is { Count: > 0 })
        {
            stdout.WriteLine();
            stdout.WriteLine("benchmark                                  base(ns)    cand(ns)   change     p        sig");
            foreach (var d in deltas)
            {
                stdout.WriteLine(
                    $"{d.Name,-42} {d.BaseCenter,10:F1} {d.CandCenter,10:F1} {d.PctChange,7:+0.0;-0.0}%  {d.P:F4}  {(d.Significant ? "yes" : "no")}");
            }
        }
    }

    /// <summary>
    /// Cancels <paramref name="cts"/> when a force-stop marker appears.
    ///
    /// A poll rather than a signal, deliberately. The Go implementation sends SIGTERM,
    /// which Windows cannot deliver process-to-process — so `stop -force` there ends
    /// eval outright and it never records what it abandoned. Polling a marker behaves
    /// identically everywhere, and eval always gets to clean up after itself: the
    /// cancellation propagates into Runner, which tears down the benchmark process tree
    /// rather than orphaning it.
    ///
    /// 500 ms is chosen against a benchmark round of many seconds: the delay is
    /// invisible next to what it interrupts.
    /// </summary>
    private static async Task WatchForForceStopAsync(
        StateStore store, CancellationTokenSource cts, CancellationToken stopWatching)
    {
        try
        {
            while (!stopWatching.IsCancellationRequested)
            {
                if (store.ForceStopRequested)
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), stopWatching).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The evaluation finished first, which is the normal path.
        }
    }

    /// <summary>
    /// Registers every shutdown route onto one token, so there is a single teardown
    /// path rather than three that can disagree.
    ///
    /// Ctrl+C matters more than it sounds: `dotnet test` runs the benchmark as a
    /// grandchild, so an eval killed without a chance to clean up would leave that
    /// benchmark running — burning CPU and corrupting every later measurement on the
    /// machine.
    /// </summary>
    private static IDisposable RegisterShutdown(CancellationTokenSource cts)
    {
        var registrations = new List<IDisposable>();

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;   // handle it rather than dying under it
            cts.Cancel();
        };

        Console.CancelKeyPress += onCancel;
        registrations.Add(new Unregister(() => Console.CancelKeyPress -= onCancel));

        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM })
        {
            try
            {
                registrations.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    ctx.Cancel = true;
                    cts.Cancel();
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // Not every signal is deliverable on every platform. Console.CancelKeyPress
                // above already covers the interactive case everywhere.
            }
        }

        return new Unregister(() =>
        {
            foreach (var r in registrations) r.Dispose();
        });
    }

    private sealed class Unregister(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>
    /// Writes the verdict as exactly one JSON object and nothing else.
    ///
    /// The agent loop parses this, so the contract is absolute: subprocess output goes
    /// to run.log, never to stdout. A single stray line breaks every caller.
    /// </summary>
    private static void WriteJson(
        TextWriter stdout,
        EvalOutcome outcome,
        int experiment,
        string commit,
        bool stopRequested)
    {
        var deltas = (outcome.Measurements?.Time ?? []).Select(d => new
        {
            name = d.Name,
            base_ns = d.BaseCenter,
            cand_ns = d.CandCenter,
            pct_change = d.PctChange,
            p = d.P,
            significant = d.Significant,
            base_bytes = ByteCenter(outcome, d.Name, baseSide: true),
            cand_bytes = ByteCenter(outcome, d.Name, baseSide: false),
        }).ToArray();

        var payload = new
        {
            status = outcome.Verdict.Status.ToWireString(),
            reason = outcome.Verdict.Reason,
            score = outcome.Verdict.Score,
            message = outcome.Verdict.Message,
            experiment,
            commit,
            stop_requested = stopRequested,
            warnings = outcome.Verdict.Warnings,
            deltas,
        };

        stdout.WriteLine(JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = false }));
    }

    private static double? ByteCenter(EvalOutcome outcome, string name, bool baseSide)
    {
        var d = outcome.Measurements?.Bytes?.FirstOrDefault(b => b.Name == name);
        if (d is null) return null;
        return baseSide ? d.BaseCenter : d.CandCenter;
    }
}
