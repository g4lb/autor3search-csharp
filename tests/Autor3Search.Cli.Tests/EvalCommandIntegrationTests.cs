using System.Text.Json;
using Autor3Search.Core;
using Autor3Search.Core.Processes;
using Autor3Search.Core.Reporting;
using Autor3Search.Core.RunState;
using Autor3Search.Core.SourceControl;
using Xunit;

namespace Autor3Search.Cli.Tests;

/// <summary>
/// Exercises `eval` end to end with real builds and real BenchmarkDotNet runs.
///
/// Most tests here only need a verdict to be reached at all, so they pin the baseline
/// with the `dry` job — one un-warmed iteration per round — which keeps a run to
/// minutes rather than tens of minutes. Two tests need the verdict to go a specific
/// direction (a real optimization must measure as faster), and `dry`'s single
/// iteration cannot support that: tiered-JIT cost on the candidate's larger call
/// surface can swamp a real ~28% algorithmic win and invert the comparison, exactly as
/// <c>MeasurerIntegrationTests.AFasterCandidateMeasuresFaster</c> documents for the same
/// fixture. Those two tests pin the baseline with `short` instead, matching that
/// existing precedent.
/// </summary>
[Trait("Category", "Integration")]
[Collection("StateHomeEnv")]
public sealed class EvalCommandIntegrationTests : IDisposable
{
    private const string Tag = "evaltest";

    private readonly string _repo;
    private readonly string _stateHome;

    /// <summary>Creates a fresh demo git repository and state home. Call <see cref="PinBaseline"/> next.</summary>
    public EvalCommandIntegrationTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"a3s-eval-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(Path.GetTempPath(), $"a3s-evalstate-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, _stateHome);

        TestRepo.CreateDemoGitRepo(_repo);
    }

    /// <summary>Restores the environment and removes the temporary repository and state home.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, null);
        TestRepo.DeleteTree(_repo);
        TestRepo.DeleteTree(_stateHome);
    }

    /// <summary>
    /// Sets the BenchmarkDotNet job and pins the baseline. Must run before any eval.
    ///
    /// The config edit is not committed: `init` gitignores the whole `.autor3search/`
    /// directory with a `!.autor3search/config.yaml` negation, but git does not
    /// descend into an ignored directory to honour a negation for a file inside it —
    /// so config.yaml is never actually tracked here, and this edit is invisible to
    /// `git status --porcelain` (and therefore to <c>baseline</c>'s dirty-tree gate)
    /// either way.
    /// </summary>
    private void PinBaseline(string job = "dry")
    {
        var configPath = Path.Combine(_repo, ".autor3search", "config.yaml");
        File.WriteAllText(configPath, File.ReadAllText(configPath).Replace("job: short", $"job: {job}"));

        var baseline = BaselineCommand.RunAsync(
            Args.Parse(["baseline", "-C", _repo, "-tag", Tag]),
            new StringWriter(), new StringWriter(), CancellationToken.None)
            .GetAwaiter().GetResult();

        if (baseline != 0) throw new InvalidOperationException("baseline setup failed");
    }

    // Ports the known-good StringBuilder rewrite from MeasurerIntegrationTests: allocates
    // roughly half of what the quadratic-string-concatenation baseline does.
    private const string OptimizedWordCount = """
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
        """;

    private void ApplyOptimizationAndCommit()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "Demo", "WordCount.cs"), OptimizedWordCount);
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "apply the StringBuilder optimization");
    }

    private void ApplyNoOpChangeAndCommit()
    {
        var path = Path.Combine(_repo, "src", "Demo", "WordCount.cs");
        File.WriteAllText(path, "// a comment that changes nothing\n" + File.ReadAllText(path));
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "a no-op comment change");
    }

    private async Task<(int Code, string Out, string Err)> RunEval(params string[] extra)
    {
        var argv = new List<string> { "eval", "-C", _repo, "-tag", Tag };
        argv.AddRange(extra);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await EvalCommand.RunAsync(
            Args.Parse(argv.ToArray()), stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A known-good optimization is measured, kept, and recorded as a KEEP row.</summary>
    [Fact]
    public async Task AKnownGoodOptimizationIsKept()
    {
        PinBaseline("short");
        ApplyOptimizationAndCommit();

        var (code, _, err) = await RunEval();

        Assert.Equal("", err);
        Assert.Equal(0, code);

        var rows = ResultsFile.Load(Path.Combine(_repo, ResultsFile.RelativePath));
        Assert.Contains(rows, r => r.Status == "KEEP");
    }

    /// <summary>A comment-only change carries no measurable improvement and is discarded.</summary>
    [Fact]
    public async Task ANoOpCommentChangeIsDiscarded()
    {
        PinBaseline();
        ApplyNoOpChangeAndCommit();

        var (code, _, _) = await RunEval();

        Assert.Equal(1, code);
    }

    /// <summary>--json prints exactly one JSON object on stdout and nothing else.</summary>
    [Fact]
    public async Task TheJsonContractIsExactlyOneObject()
    {
        PinBaseline();
        ApplyNoOpChangeAndCommit();

        var (_, output, _) = await RunEval("-json");

        var trimmed = output.Trim();
        using var doc = JsonDocument.Parse(trimmed);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        // Exactly one line: a trailing WriteLine newline is expected, but no second
        // JSON document and no leading banner or trailing summary text.
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    /// <summary>A KEEP advances the measurement baseline to the candidate commit and re-points the worktree.</summary>
    [Fact]
    public async Task AKeepAdvancesTheMeasurementBaselineAndTheWorktree()
    {
        PinBaseline("short");
        ApplyOptimizationAndCommit();
        var candidate = await Git.HeadCommitAsync(_repo, CancellationToken.None);

        var (code, _, _) = await RunEval();
        Assert.Equal(0, code);

        var store = new StateStore(_repo, Tag);
        var baseline = store.LoadBaseline();

        Assert.Equal(candidate, baseline.MeasureCommit);
        Assert.Equal(candidate, await Git.HeadCommitAsync(store.WorktreePath, CancellationToken.None));
    }

    /// <summary>A DISCARD leaves the measurement baseline exactly where it was.</summary>
    [Fact]
    public async Task ADiscardLeavesTheMeasurementBaselineAlone()
    {
        PinBaseline();
        var store = new StateStore(_repo, Tag);
        var before = store.LoadBaseline().MeasureCommit;

        ApplyNoOpChangeAndCommit();
        var (code, _, _) = await RunEval();
        Assert.Equal(1, code);

        Assert.Equal(before, store.LoadBaseline().MeasureCommit);
    }

    /// <summary>A pending stop request is surfaced in the JSON verdict.</summary>
    [Fact]
    public async Task StopRequestedIsReportedInTheVerdict()
    {
        PinBaseline();
        new StateStore(_repo, Tag).RequestStop();
        ApplyNoOpChangeAndCommit();

        var (_, output, _) = await RunEval("-json");

        Assert.Contains("\"stop_requested\":true", output);
    }

    private static void RunGit(string repo, params string[] args)
    {
        var runner = new Runner(repo, TimeSpan.FromMinutes(2), null);
        var r = runner.RunAsync("git", args, CancellationToken.None).GetAwaiter().GetResult();
        if (!r.OK) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Tail(10)}");
    }
}
