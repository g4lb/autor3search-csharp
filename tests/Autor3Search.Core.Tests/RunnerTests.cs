using Autor3Search.Core.Processes;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for <see cref="Runner"/> and <see cref="RunResult"/>.</summary>
public class RunnerTests
{
    private static readonly string Shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static string[] ShellArgs(string script) =>
        OperatingSystem.IsWindows() ? ["/c", script] : ["-c", script];

    private static Runner NewRunner(TimeSpan? timeout = null) =>
        new(Path.GetTempPath(), timeout ?? TimeSpan.FromSeconds(30), null);

    /// <summary>A process that exits zero reports success.</summary>
    [Fact]
    public async Task ASuccessfulCommandReportsExitCodeZero()
    {
        var r = await NewRunner().RunAsync(Shell, ShellArgs("exit 0"), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.OK);
        Assert.False(r.TimedOut);
    }

    /// <summary>A process that exits non-zero reports its exit code and is not OK.</summary>
    [Fact]
    public async Task AFailingCommandReportsItsExitCode()
    {
        var r = await NewRunner().RunAsync(Shell, ShellArgs("exit 7"), CancellationToken.None);
        Assert.Equal(7, r.ExitCode);
        Assert.False(r.OK);
    }

    /// <summary>Standard output written by the child is captured.</summary>
    [Fact]
    public async Task StdoutIsCaptured()
    {
        var r = await NewRunner().RunAsync(Shell, ShellArgs("echo hello-from-stdout"), CancellationToken.None);
        Assert.Contains("hello-from-stdout", r.Stdout);
    }

    /// <summary>Standard error written by the child is captured.</summary>
    [Fact]
    public async Task StderrIsCaptured()
    {
        var script = OperatingSystem.IsWindows()
            ? "echo oops 1>&2"
            : "echo oops >&2";
        var r = await NewRunner().RunAsync(Shell, ShellArgs(script), CancellationToken.None);
        Assert.Contains("oops", r.Stderr);
    }

    /// <summary>A command that runs past its timeout is killed rather than left to hang.</summary>
    [Fact]
    public async Task ALongCommandTimesOutRatherThanHanging()
    {
        var script = OperatingSystem.IsWindows()
            ? "ping -n 30 127.0.0.1 > nul"
            : "sleep 30";

        var runner = NewRunner(TimeSpan.FromSeconds(2));
        var start = DateTime.UtcNow;
        var r = await runner.RunAsync(Shell, ShellArgs(script), CancellationToken.None);

        Assert.True(r.TimedOut);
        Assert.False(r.OK);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(20));
    }

    /// <summary>Caller cancellation stops the command promptly and throws rather than returning a timed-out result.</summary>
    [Fact]
    public async Task CancellationStopsTheCommandPromptly()
    {
        var script = OperatingSystem.IsWindows()
            ? "ping -n 30 127.0.0.1 > nul"
            : "sleep 30";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var start = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewRunner().RunAsync(Shell, ShellArgs(script), cts.Token));

        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(20));
    }

    // The reason the tree kill matters: `dotnet test` runs the benchmark as a
    // grandchild. Killing only the direct child would orphan it, and an orphaned
    // benchmark burns CPU and corrupts every later measurement on the machine.
    /// <summary>A timeout kills the whole process tree, not just the direct child, so grandchildren cannot survive as orphans.</summary>
    [Fact]
    public async Task TimeoutKillsGrandchildrenNotJustTheDirectChild()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"a3s-orphan-{Guid.NewGuid():N}.txt");

        var script = OperatingSystem.IsWindows()
            ? $"start /b cmd /c \"ping -n 20 127.0.0.1 > nul & echo alive > {marker}\" & ping -n 20 127.0.0.1 > nul"
            : $"( sleep 8; echo alive > {marker} ) & sleep 20";

        var runner = NewRunner(TimeSpan.FromSeconds(2));
        var r = await runner.RunAsync(Shell, ShellArgs(script), CancellationToken.None);
        Assert.True(r.TimedOut);

        // The grandchild would write its marker at +8s. Wait past that.
        await Task.Delay(TimeSpan.FromSeconds(12));

        try
        {
            Assert.False(File.Exists(marker), "a grandchild process survived the tree kill");
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    // `dotnet build` does exactly this: it leaves persistent MSBuild worker nodes and
    // the Roslyn compiler server running by design, and they inherit the redirected
    // stdout and stderr handles. A pipe reaches EOF only when the LAST writer closes
    // it, so a wait keyed on EOF rather than on process exit reports a timeout for a
    // command that finished in milliseconds — which is how a gate test that expected
    // baseline_tampered got Crash/timeout after a build that took 1.32 seconds.
    /// <summary>A child that has exited is reported immediately, even while a grandchild still holds the output pipe open.</summary>
    [Fact]
    public async Task AnExitedChildIsNotReportedAsTimedOutWhileAGrandchildHoldsThePipe()
    {
        var script = OperatingSystem.IsWindows()
            ? "start /b cmd /c \"ping -n 30 127.0.0.1 > nul\" & echo done"
            : "( sleep 25 ) & echo done";

        var start = DateTime.UtcNow;
        var r = await NewRunner(TimeSpan.FromSeconds(6))
            .RunAsync(Shell, ShellArgs(script), CancellationToken.None);
        var elapsed = DateTime.UtcNow - start;

        Assert.False(r.TimedOut,
            $"reported a timeout for a command that exited immediately (elapsed {elapsed})");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("done", r.Stdout);
        Assert.True(elapsed < TimeSpan.FromSeconds(6), $"blocked on the grandchild for {elapsed}");
    }

    /// <summary>The child process runs in the configured working directory.</summary>
    [Fact]
    public async Task TheWorkingDirectoryIsHonoured()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"a3s-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var script = OperatingSystem.IsWindows() ? "cd" : "pwd";
            var runner = new Runner(dir, TimeSpan.FromSeconds(30), null);
            var r = await runner.RunAsync(Shell, ShellArgs(script), CancellationToken.None);

            // macOS reports /private/var for /var, so compare on the leaf.
            Assert.Contains(Path.GetFileName(dir), r.Stdout);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Entries set on <see cref="Runner.Environment"/> reach the child process.</summary>
    [Fact]
    public async Task EnvironmentEntriesReachTheChild()
    {
        var runner = NewRunner();
        runner.Environment["A3S_TEST_VAR"] = "present";

        var script = OperatingSystem.IsWindows() ? "echo %A3S_TEST_VAR%" : "echo $A3S_TEST_VAR";
        var r = await runner.RunAsync(Shell, ShellArgs(script), CancellationToken.None);

        Assert.Contains("present", r.Stdout);
    }

    /// <summary>When a log writer is supplied, child output is mirrored to it.</summary>
    [Fact]
    public async Task OutputIsMirroredToTheLogWhenOneIsGiven()
    {
        var log = new StringWriter();
        var runner = new Runner(Path.GetTempPath(), TimeSpan.FromSeconds(30), log);
        await runner.RunAsync(Shell, ShellArgs("echo logged-line"), CancellationToken.None);

        Assert.Contains("logged-line", log.ToString());
    }

    /// <summary><see cref="RunResult.Tail"/> returns only the last lines of combined stdout and stderr.</summary>
    [Fact]
    public void TailReturnsTheLastLinesOfCombinedOutput()
    {
        var r = new RunResult(1, "one\ntwo\nthree\n", "err-line\n", false);
        var tail = r.Tail(2);

        Assert.Contains("three", tail);
        Assert.Contains("err-line", tail);
        Assert.DoesNotContain("one", tail);
    }

    /// <summary>Attempting to run a missing executable fails with a message naming it.</summary>
    [Fact]
    public async Task AMissingExecutableFailsWithAClearMessage()
    {
        var runner = NewRunner();
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => runner.RunAsync("definitely-not-a-real-binary-a3s", [], CancellationToken.None));
        Assert.Contains("definitely-not-a-real-binary-a3s", ex.Message);
    }
}
