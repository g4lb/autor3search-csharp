using Autor3Search.Core.Configuration;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for <see cref="RunConfig"/> defaults, YAML loading and validation.</summary>
public class RunConfigTests
{
    private static string WriteTemp(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"a3s-cfg-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>Default() returns the documented default values.</summary>
    [Fact]
    public void DefaultsMatchTheDocumentedValues()
    {
        var c = RunConfig.Default();
        Assert.Equal(10, c.Count);
        Assert.Equal("short", c.Job);
        Assert.True(c.Warmup);
        Assert.False(c.InProcess);
        Assert.Equal(5.0, c.MaxRegressPct);
        Assert.Equal(1.0, c.MinEffectPct);
        Assert.Equal("15m", c.Timeout);
        Assert.False(c.WarningsAsErrors);
    }

    /// <summary>Load applies defaults for fields omitted from the YAML document.</summary>
    [Fact]
    public void LoadAppliesDefaultsForOmittedFields()
    {
        var path = WriteTemp("""
            benchmark_project: Benchmarks/Benchmarks.csproj
            scope:
              - "src/**"
            """);
        try
        {
            var c = RunConfig.Load(path);
            Assert.Equal(10, c.Count);            // defaulted
            Assert.Equal("short", c.Job);         // defaulted
            Assert.Equal("Benchmarks/Benchmarks.csproj", c.BenchmarkProject);
            Assert.Equal(["src/**"], c.Scope);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Load reads every field of a fully-specified document, including zero-ish values.</summary>
    [Fact]
    public void LoadReadsEveryField()
    {
        var path = WriteTemp("""
            benchmarks:
              - Demo.WordCountBench.CountWords
            benchmark_project: bench/Bench.csproj
            test_projects:
              - tests/Demo.Tests/Demo.Tests.csproj
            scope:
              - "src/**"
            count: 12
            job: medium
            warmup: false
            in_process: true
            max_regress_pct: 3.5
            min_effect_pct: 2.0
            timeout: 30m
            warnings_as_errors: true
            unfreeze:
              - tests/Demo.Tests/Scratch.cs
            """);
        try
        {
            var c = RunConfig.Load(path);
            Assert.Equal(["Demo.WordCountBench.CountWords"], c.Benchmarks);
            Assert.Equal("bench/Bench.csproj", c.BenchmarkProject);
            Assert.Equal(["tests/Demo.Tests/Demo.Tests.csproj"], c.TestProjects);
            Assert.Equal(12, c.Count);
            Assert.Equal("medium", c.Job);
            Assert.False(c.Warmup);
            Assert.True(c.InProcess);
            Assert.Equal(3.5, c.MaxRegressPct);
            Assert.Equal(2.0, c.MinEffectPct);
            Assert.Equal(TimeSpan.FromMinutes(30), c.TimeoutDuration);
            Assert.True(c.WarningsAsErrors);
            Assert.Equal(["tests/Demo.Tests/Scratch.cs"], c.Unfreeze);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// An explicit zero must survive the defaults merge. This is the case a naive
    /// "if the parsed value is falsy, use the default" merge gets wrong, and it would
    /// fail silently: the run would quietly enforce a 1% minimum effect the user had
    /// deliberately turned off.
    /// </summary>
    [Fact]
    public void LoadPreservesAnExplicitZeroAgainstANonZeroDefault()
    {
        var path = WriteTemp("""
            benchmark_project: bench/B.csproj
            min_effect_pct: 0
            max_regress_pct: 0
            """);
        try
        {
            var c = RunConfig.Load(path);
            Assert.Equal(0.0, c.MinEffectPct);
            Assert.Equal(0.0, c.MaxRegressPct);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Load throws InvalidOperationException naming the path when the file does not exist.</summary>
    [Fact]
    public void LoadRefusesAMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"a3s-cfg-missing-{Guid.NewGuid():N}.yaml");
        var ex = Assert.Throws<InvalidOperationException>(() => RunConfig.Load(path));
        Assert.Contains(path, ex.Message);
    }

    /// <summary>Load throws InvalidOperationException naming the path when the YAML is syntactically broken.</summary>
    [Fact]
    public void LoadRefusesBrokenYaml()
    {
        var path = WriteTemp("benchmarks:\n  - [unclosed\n");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RunConfig.Load(path));
            Assert.Contains(path, ex.Message);
        }
        finally { File.Delete(path); }
    }

    // The floor exists because the exact Mann-Whitney test cannot report p < 0.05
    // below 4 observations per side no matter how large the improvement, so every
    // experiment would DISCARD on a technicality with nothing explaining why.
    /// <summary>Validate refuses a count below the significance-test floor of 4.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ValidateRefusesCountBelowFour(int count)
    {
        var c = RunConfig.Default();
        c.Count = count;
        c.BenchmarkProject = "b/b.csproj";
        var ex = Assert.Throws<InvalidOperationException>(c.Validate);
        Assert.Contains("count must be at least 4", ex.Message);
        Assert.Contains("every experiment would be discarded", ex.Message);
    }

    /// <summary>Validate accepts count exactly at the floor of 4.</summary>
    [Fact]
    public void ValidateAcceptsCountOfFour()
    {
        var c = RunConfig.Default();
        c.Count = 4;
        c.BenchmarkProject = "b/b.csproj";
        c.Validate();
    }

    /// <summary>Validate refuses a negative max_regress_pct.</summary>
    [Fact]
    public void ValidateRefusesNegativeMaxRegress()
    {
        var c = RunConfig.Default();
        c.BenchmarkProject = "b/b.csproj";
        c.MaxRegressPct = -0.1;
        Assert.Throws<InvalidOperationException>(c.Validate);
    }

    /// <summary>Validate refuses min_effect_pct values outside the valid [0, 100) range.</summary>
    [Theory]
    [InlineData(-1.0)]
    [InlineData(100.0)]
    [InlineData(150.0)]
    public void ValidateRefusesMinEffectOutsideRange(double pct)
    {
        var c = RunConfig.Default();
        c.BenchmarkProject = "b/b.csproj";
        c.MinEffectPct = pct;
        Assert.Throws<InvalidOperationException>(c.Validate);
    }

    /// <summary>Validate refuses an empty scope list.</summary>
    [Fact]
    public void ValidateRefusesEmptyScope()
    {
        var c = RunConfig.Default();
        c.BenchmarkProject = "b/b.csproj";
        c.Scope = [];
        Assert.Throws<InvalidOperationException>(c.Validate);
    }

    /// <summary>Validate refuses a scope list containing a whitespace-only entry.</summary>
    [Fact]
    public void ValidateRefusesWhitespaceScopeEntry()
    {
        var c = RunConfig.Default();
        c.BenchmarkProject = "b/b.csproj";
        c.Scope = ["src/**", "   "];
        Assert.Throws<InvalidOperationException>(c.Validate);
    }

    /// <summary>Validate accepts every recognized BenchmarkDotNet job name, case-insensitively.</summary>
    [Theory]
    [InlineData("dry")]
    [InlineData("short")]
    [InlineData("medium")]
    [InlineData("long")]
    [InlineData("default")]
    [InlineData("SHORT")]
    public void ValidateAcceptsEveryBdnJob(string job)
    {
        var c = RunConfig.Default();
        c.BenchmarkProject = "b/b.csproj";
        c.Job = job;
        c.Validate();
    }

    /// <summary>Validate refuses an unrecognized job and names the valid ones in the message.</summary>
    [Fact]
    public void ValidateRefusesUnknownJobAndNamesTheValidOnes()
    {
        var c = RunConfig.Default();
        c.BenchmarkProject = "b/b.csproj";
        c.Job = "turbo";
        var ex = Assert.Throws<InvalidOperationException>(c.Validate);
        Assert.Contains("dry, short, medium, long, default", ex.Message);
    }

    /// <summary>Validate refuses a timeout string that cannot be parsed as a duration.</summary>
    [Fact]
    public void ValidateRefusesUnparseableTimeout()
    {
        var c = RunConfig.Default();
        c.BenchmarkProject = "b/b.csproj";
        c.Timeout = "quarter of an hour";
        Assert.Throws<InvalidOperationException>(c.Validate);
    }

    /// <summary>Validate refuses a missing benchmark_project and names the field in the message.</summary>
    [Fact]
    public void ValidateRefusesMissingBenchmarkProject()
    {
        var c = RunConfig.Default();
        c.BenchmarkProject = "";
        var ex = Assert.Throws<InvalidOperationException>(c.Validate);
        Assert.Contains("benchmark_project", ex.Message);
    }

    /// <summary>TimeoutDuration parses Go-style duration strings, not TimeSpan.Parse's day-first form.</summary>
    [Theory]
    [InlineData("15m", 15)]
    [InlineData("1h", 60)]
    [InlineData("90s", 1.5)]
    [InlineData("2h30m", 150)]
    public void TimeoutDurationParsesGoStyleDurations(string text, double expectedMinutes)
    {
        var c = RunConfig.Default();
        c.Timeout = text;
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), c.TimeoutDuration);
    }
}
