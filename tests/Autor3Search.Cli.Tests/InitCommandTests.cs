using Autor3Search.Cli;
using Autor3Search.Core.Configuration;
using Autor3Search.Core.Processes;
using Xunit;

namespace Autor3Search.Cli.Tests;

/// <summary>Tests for the <c>init</c> command: discovery, refusal, and generated output.</summary>
public sealed class InitCommandTests : IDisposable
{
    private readonly string _repo;

    /// <summary>Creates a fresh temporary repository root for each test.</summary>
    public InitCommandTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"a3s-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
    }

    /// <summary>Removes the temporary repository root.</summary>
    public void Dispose()
    {
        try { if (Directory.Exists(_repo)) Directory.Delete(_repo, true); }
        catch (IOException) { }
    }

    private void WriteDemo()
    {
        void W(string rel, string content)
        {
            var abs = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, content);
        }

        W("src/Demo/Demo.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        W("src/Demo/Code.cs", "namespace Demo; public static class C { }");
        W("tests/Demo.Tests/Demo.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
            </Project>
            """);
        W("tests/Demo.Tests/CTests.cs", "namespace Demo.Tests; public class T { }");
        W("bench/Demo.Benchmarks/Demo.Benchmarks.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="BenchmarkDotNet" Version="0.15.8" /></ItemGroup>
            </Project>
            """);
        W("bench/Demo.Benchmarks/B.cs", """
            using BenchmarkDotNet.Attributes;
            namespace Demo.Benchmarks;
            public class WordCountBench { [Benchmark] public int CountWords() => 0; }
            """);
    }

    private void WriteFile(string rel, string content)
    {
        var abs = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private async Task<(int Code, string Out, string Err)> RunInit(params string[] extra)
    {
        var argv = new List<string> { "init", "-C", _repo };
        argv.AddRange(extra);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await InitCommand.RunAsync(Args.Parse(argv.ToArray()), stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private void Git(params string[] args)
    {
        var runner = new Runner(_repo, TimeSpan.FromMinutes(2), null);
        var r = runner.RunAsync("git", args, CancellationToken.None).GetAwaiter().GetResult();
        if (!r.OK) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Tail(10)}");
    }

    private string GitOutput(params string[] args)
    {
        var runner = new Runner(_repo, TimeSpan.FromMinutes(2), null);
        var r = runner.RunAsync("git", args, CancellationToken.None).GetAwaiter().GetResult();
        if (!r.OK) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Tail(10)}");
        return r.Stdout;
    }

    /// <summary>A successful init writes config.yaml, program.md, and .gitignore entries.</summary>
    [Fact]
    public async Task InitWritesConfigProgramAndGitignoreEntries()
    {
        WriteDemo();
        var (code, _, _) = await RunInit();

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(_repo, ".autor3search", "config.yaml")));
        Assert.True(File.Exists(Path.Combine(_repo, "program.md")));

        var gitignore = File.ReadAllText(Path.Combine(_repo, ".gitignore"));
        Assert.Contains("results.tsv", gitignore);
        Assert.Contains("run.log", gitignore);
        Assert.Contains(".autor3search/", gitignore);
    }

    /// <summary>The generated config.yaml round-trips through RunConfig.Load and Validate.</summary>
    [Fact]
    public async Task TheGeneratedConfigLoadsAndValidates()
    {
        WriteDemo();
        await RunInit();

        var cfg = RunConfig.Load(Path.Combine(_repo, ".autor3search", "config.yaml"));
        Assert.Equal("bench/Demo.Benchmarks/Demo.Benchmarks.csproj", cfg.BenchmarkProject);
        Assert.Contains("tests/Demo.Tests/Demo.Tests.csproj", cfg.TestProjects);
        Assert.Contains("Demo.Benchmarks.WordCountBench.CountWords", cfg.Benchmarks);
    }

    /// <summary>config.yaml itself is explicitly un-ignored, since humans own and commit it.</summary>
    [Fact]
    public async Task TheConfigDirectoryItselfIsNotGitignored()
    {
        // config.yaml is the one file under .autor3search/ that humans own and commit.
        WriteDemo();
        await RunInit();

        var gitignore = File.ReadAllText(Path.Combine(_repo, ".gitignore"));
        Assert.Contains("!.autor3search/config.yaml", gitignore);
    }

    /// <summary>
    /// The generated .gitignore does not merely say config.yaml is un-ignored — it
    /// genuinely IS trackable by git. A prior version used ".autor3search/" (the
    /// directory form) rather than ".autor3search/*" (the contents form); git cannot
    /// re-include a file whose parent directory is excluded, so that pattern silently
    /// defeated the negation and made config.yaml permanently untrackable while still
    /// looking correct as text. This test proves the property against git's own
    /// behaviour (`git ls-files` after a real `git add -A`), not against the
    /// .gitignore's source text, since the text alone cannot tell the two apart.
    /// </summary>
    [Fact]
    public async Task ConfigYamlIsActuallyTrackableByGit()
    {
        WriteDemo();
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("checkout", "-q", "-b", "main");

        await RunInit();
        Git("add", "-A");

        var tracked = GitOutput("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains(".autor3search/config.yaml", tracked);
        Assert.DoesNotContain(tracked, f => f.StartsWith(".autor3search/") && f != ".autor3search/config.yaml");
    }

    /// <summary>program.md names the discovered benchmarks and leaves no unsubstituted placeholder.</summary>
    [Fact]
    public async Task ProgramMdNamesTheDiscoveredBenchmarks()
    {
        WriteDemo();
        await RunInit();

        var program = File.ReadAllText(Path.Combine(_repo, "program.md"));
        Assert.Contains("Demo.Benchmarks.WordCountBench.CountWords", program);
        Assert.DoesNotContain("{{BENCHMARKS}}", program);
        Assert.DoesNotContain("{{BENCHMARK_PROJECT}}", program);
    }

    // No benchmarks means no notion of "faster". Writing a config with an empty
    // benchmark list would silently optimize nothing while looking like it worked.
    /// <summary>init refuses a repository with no benchmarks, and writes no config.</summary>
    [Fact]
    public async Task InitRefusesARepositoryWithNoBenchmarks()
    {
        var abs = Path.Combine(_repo, "src", "Demo", "Demo.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, """<Project Sdk="Microsoft.NET.Sdk"></Project>""");

        var (code, _, err) = await RunInit();

        Assert.Equal(2, code);
        Assert.Contains("no benchmarks", err, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_repo, ".autor3search", "config.yaml")));
    }

    /// <summary>init refuses to overwrite an existing config.yaml without -force.</summary>
    [Fact]
    public async Task InitRefusesToOverwriteAnExistingConfig()
    {
        WriteDemo();
        await RunInit();

        var configPath = Path.Combine(_repo, ".autor3search", "config.yaml");
        File.AppendAllText(configPath, "\n# a human edited this\n");

        var (code, _, err) = await RunInit();

        Assert.Equal(2, code);
        Assert.Contains("-force", err);
        Assert.Contains("a human edited this", File.ReadAllText(configPath));
    }

    /// <summary>-force overwrites an existing config.yaml.</summary>
    [Fact]
    public async Task ForceOverwritesTheConfig()
    {
        WriteDemo();
        await RunInit();

        var configPath = Path.Combine(_repo, ".autor3search", "config.yaml");
        File.AppendAllText(configPath, "\n# a human edited this\n");

        var (code, _, _) = await RunInit("-force");

        Assert.Equal(0, code);
        Assert.DoesNotContain("a human edited this", File.ReadAllText(configPath));
    }

    /// <summary>Running init twice does not duplicate .gitignore entries.</summary>
    [Fact]
    public async Task RunningInitTwiceDoesNotDuplicateGitignoreEntries()
    {
        WriteDemo();
        await RunInit();
        await RunInit("-force");

        var lines = File.ReadAllLines(Path.Combine(_repo, ".gitignore"));
        Assert.Equal(1, lines.Count(l => l.Trim() == "results.tsv"));
    }

    /// <summary>init reports what it discovered on stdout.</summary>
    [Fact]
    public async Task InitReportsWhatItDiscovered()
    {
        WriteDemo();
        var (_, output, _) = await RunInit();

        Assert.Contains("Demo.Benchmarks.WordCountBench.CountWords", output);
        Assert.Contains("Demo.Tests", output);
    }

    // A source project sitting at the repository root has no directory of its own to
    // scope to, so the only correct pattern is "**" — but that also matches the
    // frozen test/benchmark directories and .autor3search/, so it must never be
    // written silently. The original WriteDemo() fixture puts every project in a
    // subdirectory, so this layout was previously unexercised.
    /// <summary>
    /// A root-level source project forces scope "**", surfaced with a loud warning
    /// rather than written silently.
    /// </summary>
    [Fact]
    public async Task InitWarnsWhenASourceProjectSitsAtTheRepositoryRoot()
    {
        WriteFile("Root.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        WriteFile("Code.cs", "namespace Root; public static class C { }");
        WriteFile("tests/Demo.Tests/Demo.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
            </Project>
            """);
        WriteFile("tests/Demo.Tests/CTests.cs", "namespace Demo.Tests; public class T { }");
        WriteFile("bench/Demo.Benchmarks/Demo.Benchmarks.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="BenchmarkDotNet" Version="0.15.8" /></ItemGroup>
            </Project>
            """);
        WriteFile("bench/Demo.Benchmarks/B.cs", """
            using BenchmarkDotNet.Attributes;
            namespace Demo.Benchmarks;
            public class WordCountBench { [Benchmark] public int CountWords() => 0; }
            """);

        var (code, output, _) = await RunInit();

        Assert.Equal(0, code);
        Assert.Contains("WARNING", output);
        Assert.Contains("repository root", output);

        var cfg = RunConfig.Load(Path.Combine(_repo, ".autor3search", "config.yaml"));
        Assert.Equal(["**"], cfg.Scope);
    }

    // When the benchmark project is the ONLY project in the repository, excluding it
    // (as the tool must) leaves no source project at all. Writing a config whose scope
    // has nothing to point at would be as silently useless as an empty benchmark list.
    /// <summary>
    /// init refuses when the only discovered project is the benchmark project itself,
    /// since excluding it leaves no source project to optimize.
    /// </summary>
    [Fact]
    public async Task InitRefusesWhenTheOnlyProjectIsTheBenchmarkProject()
    {
        WriteFile("bench/OnlyProj/OnlyProj.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="BenchmarkDotNet" Version="0.15.8" /></ItemGroup>
            </Project>
            """);
        WriteFile("bench/OnlyProj/B.cs", """
            using BenchmarkDotNet.Attributes;
            namespace OnlyProj;
            public class OnlyBench { [Benchmark] public int Run() => 0; }
            """);

        var (code, _, err) = await RunInit();

        Assert.Equal(2, code);
        Assert.Contains("no source project", err, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_repo, ".autor3search", "config.yaml")));
    }
}
