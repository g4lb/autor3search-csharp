using Autor3Search.Cli;
using Autor3Search.Core;
using Autor3Search.Core.Reporting;
using Autor3Search.Core.RunState;
using Xunit;

namespace Autor3Search.Cli.Tests;

/// <summary>
/// Drives the real CLI over the real fixture, start to finish, proving the whole
/// baseline-to-report loop and the tool's central claim that a broken optimization
/// still fails the frozen tests.
/// </summary>
[Trait("Category", "EndToEnd")]
[Collection("StateHomeEnv")]
public sealed class EndToEndTests : IDisposable
{
    private readonly string _repo;
    private readonly string _stateHome;

    /// <summary>
    /// Creates a fresh temporary git repository from the demo fixture, points a fresh
    /// state home at it, and switches the fixture's job to <c>dry</c> so the whole
    /// flow completes in a few minutes.
    /// </summary>
    public EndToEndTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"a3s-e2e-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(Path.GetTempPath(), $"a3s-e2estate-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, _stateHome);

        TestRepo.CreateDemoGitRepo(_repo);

        // A dry job keeps the whole flow to a few minutes. It is enough to prove the
        // pipeline end to end; it is NOT enough to prove a statistically real win,
        // which is what the manual verification in Step 3 exists for.
        //
        // The timeout is raised well above the 15m product default because it is
        // measuring the wrong thing here. These tests assert which verdict the
        // pipeline reaches, and a subprocess killed on the clock reports Crash --
        // so on a machine slow enough to trip it, the assertion under test is
        // replaced by an assertion about the runner's speed. That is not
        // hypothetical: ubuntu-latest ran this assembly in 94 minutes against 17
        // locally, and both end-to-end tests died at 15m06s with Crash where Fail
        // was expected. xunit runs collections in parallel and each of these spawns
        // real builds and real BenchmarkDotNet runs, so the load is self-inflicted
        // and the slowest subprocess is far slower than the same command run alone.
        // 15m stays the default for real repositories, where a build or test suite
        // that runs that long is a genuine problem worth surfacing as Crash.
        var configPath = Path.Combine(_repo, ".autor3search", "config.yaml");
        File.WriteAllText(configPath,
            File.ReadAllText(configPath)
                .Replace("job: short", "job: dry")
                .Replace("timeout: 15m", "timeout: 90m"));

        Git("add", "-A");
        Git("commit", "-q", "-m", "use a dry job for the end-to-end test");
    }

    /// <summary>Restores the environment and removes the temporary repository and state home.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, null);
        TestRepo.DeleteTree(_repo);
        TestRepo.DeleteTree(_stateHome);
    }

    /// <summary>
    /// Runs baseline, applies a known-good optimization, evaluates it, and confirms
    /// status, report and doctor all still succeed afterward.
    /// </summary>
    [Fact]
    public async Task TheFullLoopRunsFromBaselineToReport()
    {
        Assert.Equal(0, await Cli("baseline", "-tag", "e2e"));

        ApplyKnownGoodOptimization();
        Git("add", "-A");
        Git("commit", "-q", "-m", "exp 1: StringBuilder instead of string concatenation");

        var evalCode = await Cli("eval");
        Assert.InRange(evalCode, 0, 1);   // KEEP or DISCARD; never FAIL or CRASH

        var rows = ResultsFile.Load(Path.Combine(_repo, ResultsFile.RelativePath));
        Assert.Single(rows);

        Assert.Equal(0, await Cli("status"));
        Assert.Equal(0, await Cli("report"));
        Assert.Equal(0, await Cli("doctor"));
    }

    /// <summary>Confirms a stop request survives into the next eval's JSON verdict and ends the loop.</summary>
    [Fact]
    public async Task AStopRequestIsReportedToTheAgentAndEndsTheLoop()
    {
        await Cli("baseline", "-tag", "e2e");
        Assert.Equal(0, await Cli("stop"));

        ApplyKnownGoodOptimization();
        Git("add", "-A");
        Git("commit", "-q", "-m", "exp 1");

        var stdout = new StringWriter();
        await EvalCommand.RunAsync(
            Args.Parse(["eval", "-C", _repo, "--json"]), stdout, new StringWriter(), CancellationToken.None);

        using var doc = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.True(doc.RootElement.GetProperty("stop_requested").GetBoolean());
    }

    /// <summary>Confirms <c>eval --json</c> emits exactly one JSON object with all required fields, and nothing else.</summary>
    [Fact]
    public async Task TheJsonVerdictIsExactlyOneObjectWithNothingElse()
    {
        await Cli("baseline", "-tag", "e2e");

        File.AppendAllText(Path.Combine(_repo, "src", "Demo", "WordCount.cs"), "\n// a no-op\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "exp 1: no-op");

        var stdout = new StringWriter();
        await EvalCommand.RunAsync(
            Args.Parse(["eval", "-C", _repo, "--json"]), stdout, new StringWriter(), CancellationToken.None);

        var text = stdout.ToString().Trim();
        Assert.StartsWith("{", text);
        Assert.EndsWith("}", text);

        using var doc = System.Text.Json.JsonDocument.Parse(text);
        foreach (var field in new[] { "status", "reason", "score", "deltas", "stop_requested" })
            Assert.True(doc.RootElement.TryGetProperty(field, out _), $"missing field {field}");
    }

    /// <summary>
    /// Proves the product's central claim: an agent that deletes the real work and
    /// replaces it with a stub still fails the frozen tests, regardless of how fast
    /// the stub runs.
    /// </summary>
    [Fact]
    public async Task AnAgentThatDeletesTheWorkStillFailsTheFrozenTests()
    {
        await Cli("baseline", "-tag", "e2e");

        File.WriteAllText(Path.Combine(_repo, "src", "Demo", "WordCount.cs"), """
            namespace Demo;
            public static class WordCount
            {
                public static Dictionary<string, int> CountWords(string s) => new();
            }
            """);
        Git("add", "-A");
        Git("commit", "-q", "-m", "exp 1: do nothing, very fast");

        Assert.Equal(2, await Cli("eval"));   // FAIL
    }

    private void ApplyKnownGoodOptimization() =>
        File.WriteAllText(Path.Combine(_repo, "src", "Demo", "WordCount.cs"), """
            using System.Text;

            namespace Demo;

            /// <summary>Counts word occurrences.</summary>
            public static class WordCount
            {
                /// <summary>Returns how many times each lowercase word appears in <paramref name="s"/>.</summary>
                public static Dictionary<string, int> CountWords(string s)
                {
                    var counts = new Dictionary<string, int>(32);
                    var sb = new StringBuilder();

                    foreach (var field in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        sb.Clear();
                        foreach (var r in field)
                        {
                            var c = char.ToLowerInvariant(r);
                            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                        }

                        if (sb.Length > 0)
                        {
                            var word = sb.ToString();
                            counts[word] = counts.TryGetValue(word, out var n) ? n + 1 : 1;
                        }
                    }

                    return counts;
                }
            }
            """);

    private async Task<int> Cli(params string[] argv)
    {
        var all = new List<string> { argv[0], "-C", _repo };
        all.AddRange(argv.Skip(1));

        var a = Args.Parse(all.ToArray());
        var o = new StringWriter();
        var e = new StringWriter();

        return argv[0] switch
        {
            "baseline" => await BaselineCommand.RunAsync(a, o, e, CancellationToken.None),
            "eval" => await EvalCommand.RunAsync(a, o, e, CancellationToken.None),
            "status" => await StatusCommand.RunAsync(a, o, e, CancellationToken.None),
            "stop" => await StopCommand.RunAsync(a, o, e, CancellationToken.None),
            "report" => await ReportCommand.RunAsync(a, o, e, CancellationToken.None),
            "doctor" => await DoctorCommand.RunAsync(a, o, e, CancellationToken.None),
            _ => throw new ArgumentException($"unhandled command {argv[0]}"),
        };
    }

    private void Git(params string[] args)
    {
        var runner = new Core.Processes.Runner(_repo, TimeSpan.FromMinutes(2), null);
        var r = runner.RunAsync("git", args, CancellationToken.None).GetAwaiter().GetResult();
        if (!r.OK) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Tail(10)}");
    }
}
