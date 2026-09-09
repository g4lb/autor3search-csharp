using System.Diagnostics;
using System.Text;

namespace Autor3Search.Core.Processes;

/// <summary>The outcome of one subprocess.</summary>
/// <param name="ExitCode">Process exit code, or -1 when it was killed.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
/// <param name="TimedOut">True when the process exceeded its timeout and was killed.</param>
public readonly record struct RunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    /// <summary>True when the process exited cleanly within its timeout.</summary>
    public bool OK => ExitCode == 0 && !TimedOut;

    /// <summary>
    /// The last lines of combined output, for an error message. Subprocess output can
    /// run to thousands of lines and an unattended agent's context is finite; the tail
    /// is where the failure is.
    /// </summary>
    public string Tail(int lines)
    {
        var combined = (Stdout + Stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

        return string.Join("\n", combined.Skip(Math.Max(0, combined.Length - lines)));
    }
}

/// <summary>
/// Runs subprocesses with a timeout, capturing output.
///
/// Every kill is a TREE kill. `dotnet test` and `dotnet build` run real work in
/// grandchild processes, and killing only the direct child leaves a benchmark binary
/// running — which burns CPU and corrupts every later measurement on the machine.
/// This is the failure the Go implementation needed process groups and Windows job
/// objects to avoid; .NET offers it directly.
/// </summary>
/// <param name="workingDirectory">
/// Directory the child runs in. For callers that shell out to BenchmarkDotNet, this is
/// load-bearing for correctness, not just a base for relative paths: BDN's default
/// toolchain resolves the project it actually rebuilds and runs by searching upward from
/// this directory, not from any assembly path given on the command line.
/// </param>
/// <param name="timeout">Wall-clock bound on the child.</param>
/// <param name="log">Receives mirrored output. May be null.</param>
public sealed class Runner(string workingDirectory, TimeSpan timeout, TextWriter? log)
{
    /// <summary>Environment entries applied on top of the inherited environment.</summary>
    public IDictionary<string, string> Environment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Runs one command to completion, a timeout, or cancellation.</summary>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    /// <exception cref="InvalidOperationException">The executable could not be started.</exception>
    public async Task<RunResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in Environment) psi.Environment[k] = v;

        // Keep benchmark subprocesses from paying for telemetry and startup banners,
        // and from picking up an ambient colour setting that would corrupt parsing.
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Exit and end-of-output are DIFFERENT events, and only the first one is this
        // process's to wait for. A redirected pipe reaches EOF when its last writer
        // closes it, and `dotnet build` leaves persistent MSBuild worker nodes and the
        // Roslyn compiler server running by design, holding inherited copies of these
        // handles. Waiting on EOF therefore waits on processes that outlive the command
        // and were never part of it. Subscribed before Start so a fast exit cannot be
        // missed.
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var lastOutputTicks = DateTime.UtcNow.Ticks;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
            stdout.AppendLine(e.Data);
            log?.WriteLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
            stderr.AppendLine(e.Data);
            log?.WriteLine(e.Data);
        };

        // Bounded, because what draining ultimately waits on — every writer closing the
        // pipe — is not under this process's control. Returns the instant the readers
        // reach EOF, which is the normal case; when a surviving grandchild holds the
        // pipe open, returns as soon as output falls quiet instead, so the pathological
        // case costs milliseconds rather than the whole cap.
        async Task DrainAsync()
        {
            var drain = Task.Run(process.WaitForExit);

            // The process object is disposed on the way out from under a drain that
            // never finished; observe the resulting fault so it is not unhandled.
            _ = drain.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

            while (true)
            {
                if (await Task.WhenAny(drain, Task.Delay(50)).ConfigureAwait(false) == drain) return;

                var quiet = DateTime.UtcNow.Ticks - Interlocked.Read(ref lastOutputTicks);
                if (quiet > TimeSpan.TicksPerMillisecond * 500 || DateTime.UtcNow > deadline) return;
            }
        }

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"could not start {fileName}: {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        try
        {
            // Deliberately NOT WaitForExitAsync: that also waits for the redirected
            // streams to reach EOF, so one surviving grandchild turns a command that
            // finished in milliseconds into a timeout at the full wall-clock bound.
            await exited.Task.WaitAsync(linked.Token).ConfigureAwait(false);

            // The process is gone and its own output is already in the pipe, but the
            // reader events for the last of it may not have fired yet. Drain, bounded:
            // in the normal case every writer has closed and this returns at once, and
            // when one has not, the tail of a command that already exited is not worth
            // hanging the harness for.
            await DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);

            // Give the tree a moment to actually die so its output flushes, then
            // distinguish "the caller cancelled" from "this phase ran too long". Drain
            // via the synchronous WaitForExit() (see above) so the tail is not lost,
            // but keep the wait BOUNDED: a descendant could still be holding the pipe
            // open, and this is the one path that exists for when things have already
            // gone wrong.
            await DrainAsync().ConfigureAwait(false);

            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }

        return new RunResult(
            timedOut ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            timedOut);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill. That is the outcome
            // wanted, not a failure.
        }
        catch (NotSupportedException)
        {
            // Documented for remote processes; unreachable here, but never let a
            // teardown path throw over the real result.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // A descendant could not be terminated (permissions, or it is already
            // terminating). Nothing further to do, and the real result must survive.
        }
        catch (AggregateException)
        {
            // Kill(entireProcessTree: true) reports this when not every descendant in
            // the tree could be terminated. Whatever died, died; whatever did not is
            // not something a teardown path can fix by throwing.
        }
    }
}
