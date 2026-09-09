using System.Text;
using Autor3Search.Core.Configuration;
using Autor3Search.Core.Discovery;
using Autor3Search.Core.Freezing;
using Autor3Search.Core.Pipelines;
using Autor3Search.Core.Processes;
using Autor3Search.Core.RunState;
using Autor3Search.Core.SourceControl;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>
/// Builds a real git repository from the demo fixture and a real baseline through Core
/// APIs directly — no CLI dependency — so gate tests exercise <see cref="Pipeline"/>
/// exactly as the `eval` command would, without a process boundary.
///
/// Sets <see cref="Paths.StateHomeEnvVar"/> to a fresh temporary directory for the
/// lifetime of the instance and restores it on <see cref="Dispose"/>. Uses the
/// <c>dry</c> BenchmarkDotNet job so a gate test that unexpectedly clears every gate
/// still runs in milliseconds rather than paying for a real measurement.
/// </summary>
internal sealed class GateHarness : IDisposable
{
    private const string Tag = "gate";

    private readonly string? _previousStateHome;
    private readonly string _stateHome;
    private string _repo;
    private readonly StateStore _store;

    // Runner mirrors every child process's output into the pipeline log, so this is
    // where a `dotnet build` or `dotnet test` failure explains itself. Passing null
    // discarded it, which is why a gate test failing on an earlier stage than the one
    // under test left nothing to diagnose. Synchronized because the pipeline writes to
    // it from the reader threads draining stdout and stderr.
    // Read through _logBuffer, not _log: TextWriter.Synchronized wraps the writer in a
    // type that does not forward ToString() to it, so calling ToString() on the wrapper
    // yields its type name rather than the captured text.
    private readonly StringWriter _logBuffer = new();
    private readonly TextWriter _log;

    /// <summary>Absolute path to the run configuration written for this repository.</summary>
    public string ConfigPath { get; }

    /// <summary>Builds the repository, writes and commits the config, and takes the baseline.</summary>
    public GateHarness()
    {
        _log = TextWriter.Synchronized(_logBuffer);

        _previousStateHome = Environment.GetEnvironmentVariable(Paths.StateHomeEnvVar);
        _stateHome = Path.Combine(Path.GetTempPath(), $"a3s-gatestate-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, _stateHome);

        _repo = Path.Combine(Path.GetTempPath(), $"a3s-gaterepo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        DemoFixture.CopyTo(_repo);

        RunGit("init", "-q");
        RunGit("config", "user.name", "Test");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "commit.gpgsign", "false");
        RunGit("checkout", "-q", "-b", "main");

        // Canonicalise to git's own symlink-resolved form — exactly what EvalCommand
        // does in production via Git.RootAsync. Without this, on macOS a repo created
        // under the raw (non-canonical) temp path leaves Pipeline's own "dotnet build"
        // operating under the OS-resolved cwd (/private/var/...) while Measurer's later
        // absolute-path build uses the unresolved spelling (/var/...) — two different
        // strings for the same file that MSBuild's incremental "up-to-date" check does
        // not reconcile, so a referenced project's reference assembly silently never
        // gets emitted on the second build.
        _repo = RunAsync(() => Git.RootAsync(_repo, CancellationToken.None));

        const string testProject = "tests/Demo.Tests/Demo.Tests.csproj";
        const string benchProject = "bench/Demo.Benchmarks/Demo.Benchmarks.csproj";
        var testProjects = new List<string> { testProject };
        var frozenProjects = new List<string>(testProjects) { benchProject };

        ConfigPath = Path.Combine(_repo, Paths.FromSlash(RunConfig.RelativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, RenderConfig(testProjects, benchProject));

        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "initial");

        _store = new StateStore(_repo, Tag);

        var frozenFiles = Discoverer.FrozenFiles(_repo, frozenProjects, []);
        var manifest = Freezer.Snapshot(_repo, _store.FrozenStorePath, frozenFiles);
        manifest.Save(_store.ManifestPath);

        var commit = RunAsync(() => Git.HeadCommitAsync(_repo, CancellationToken.None));
        Git.AddWorktreeAsync(_repo, _store.WorktreePath, commit, CancellationToken.None)
            .GetAwaiter().GetResult();

        _store.SaveBaseline(new Baseline
        {
            Tag = Tag,
            Commit = commit,
            MeasureCommit = commit,
            ConfigSha256 = Freezer.HashFile(ConfigPath),
            CreatedAt = DateTimeOffset.UtcNow,
            FrozenProjects = frozenProjects,
            BenchmarkProject = benchProject,
        });
    }

    /// <summary>Restores the state-home environment variable and removes the temp directories.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, _previousStateHome);
        DeleteTree(_repo);
        DeleteTree(_stateHome);
    }

    /// <summary>Runs the pipeline against the repository's current state and the live baseline.</summary>
    public async Task<EvalOutcome> EvalAsync()
    {
        var config = RunConfig.Load(ConfigPath);
        var baseline = _store.LoadBaseline();

        var options = new PipelineOptions(_repo, _store, config, baseline, Log: _log);
        return await Pipeline.EvalAsync(options, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The tail of everything the pipeline logged, for failure diagnostics. Bounded
    /// because a gate test that clears every gate runs a real measurement, and the
    /// whole log is megabytes of build and BenchmarkDotNet output; the end is the part
    /// that says why something failed.
    /// </summary>
    public string LogTail(int chars = 4000)
    {
        string text;
        lock (_logBuffer) text = _logBuffer.ToString();
        return text.Length <= chars ? text : string.Concat("...", text.AsSpan(text.Length - chars));
    }

    /// <summary>Reads a repo-relative file as text.</summary>
    public string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(_repo, Paths.FromSlash(relativePath)));

    /// <summary>Writes a repo-relative file and commits it, creating parent directories as needed.</summary>
    public void WriteAndCommit(string relativePath, string content)
    {
        var abs = Path.Combine(_repo, Paths.FromSlash(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);

        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", $"edit {relativePath}");
    }

    /// <summary>
    /// Renames a directory in the working tree, uncommitted. Used to simulate a frozen
    /// file whose path has shifted under a discovery-skipped (dot-prefixed) directory.
    /// </summary>
    public void MoveDirectory(string fromRelative, string toRelative)
    {
        var from = Path.Combine(_repo, Paths.FromSlash(fromRelative));
        var to = Path.Combine(_repo, Paths.FromSlash(toRelative));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(from, to);
    }

    /// <summary>Corrupts one frozen reference copy in the out-of-tree store, tripping StoreTamperedException.</summary>
    public void TamperFrozenStore()
    {
        var manifest = Manifest.Load(_store.ManifestPath);
        var rel = manifest.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        var stored = Path.Combine(_store.FrozenStorePath, Paths.FromSlash(rel));
        File.WriteAllText(stored, "// tampered directly in the frozen store");
    }

    /// <summary>
    /// Re-points the pinned baseline worktree at a commit other than the one the run
    /// record still names, simulating the worktree having been moved out from under it.
    /// </summary>
    public async Task MoveBaselineWorktreeOffItsCommitAsync()
    {
        RunGit("commit", "--allow-empty", "-q", "-m", "an unrelated commit to move the worktree to");
        var other = await Git.HeadCommitAsync(_repo, CancellationToken.None).ConfigureAwait(false);
        await Git.MoveWorktreeToAsync(_store.WorktreePath, other, CancellationToken.None).ConfigureAwait(false);
    }

    private static string RenderConfig(IReadOnlyList<string> testProjects, string benchProject)
    {
        var sb = new StringBuilder();
        sb.AppendLine("benchmarks: []");
        sb.AppendLine($"benchmark_project: {benchProject}");
        sb.AppendLine("test_projects:");
        foreach (var t in testProjects) sb.AppendLine($"  - {t}");
        sb.AppendLine("scope:");
        sb.AppendLine("  - \"src/**\"");
        sb.AppendLine("  - \"tests/**\"");
        sb.AppendLine("  - \"bench/**\"");
        sb.AppendLine("count: 4");
        sb.AppendLine("job: dry");
        sb.AppendLine("warmup: false");
        sb.AppendLine("in_process: false");
        sb.AppendLine("max_regress_pct: 5.0");
        sb.AppendLine("min_effect_pct: 1.0");
        sb.AppendLine("timeout: 5m");
        sb.AppendLine("warnings_as_errors: false");
        sb.AppendLine("unfreeze: []");
        return sb.ToString();
    }

    private void RunGit(params string[] args)
    {
        var runner = new Runner(_repo, TimeSpan.FromMinutes(2), null);
        var r = runner.RunAsync("git", args, CancellationToken.None).GetAwaiter().GetResult();
        if (!r.OK) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Tail(20)}");
    }

    private static T RunAsync<T>(Func<Task<T>> func) => func().GetAwaiter().GetResult();

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
