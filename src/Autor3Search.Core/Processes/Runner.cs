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
/// <param name="workingDirectory">Directory the child runs in.</param>
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

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            log?.WriteLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            log?.WriteLine(e.Data);
        };

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
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            // WaitForExitAsync can return before the OutputDataReceived/ErrorDataReceived
            // events for the process's final output have fired. The parameterless
            // synchronous WaitForExit() blocks until redirected-stream processing is
            // drained; the process has already exited, so this returns as soon as the
            // readers finish.
            process.WaitForExit();
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
            try
            {
                await Task.Run(process.WaitForExit).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The tree kill was already issued; a descendant is still holding the
                // pipe open. Return what was captured rather than hanging the harness.
            }

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
