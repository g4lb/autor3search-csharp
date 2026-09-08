using Autor3Search.Core.Discovery;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Locates and copies the demo fixture for tests that need a mutable tree.</summary>
public static class DemoFixture
{
    /// <summary>Absolute path to testdata/demo in the source tree.</summary>
    public static string SourcePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testdata", "demo")))
                dir = dir.Parent;

            return dir is null
                ? throw new InvalidOperationException("could not locate testdata/demo from " + AppContext.BaseDirectory)
                : Path.Combine(dir.FullName, "testdata", "demo");
        }
    }

    /// <summary>Copies the fixture, skipping build output, and returns the destination.</summary>
    public static string CopyTo(string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(SourcePath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(SourcePath, dir);
            if (IsBuildOutput(rel)) continue;
            Directory.CreateDirectory(Path.Combine(destination, rel));
        }

        foreach (var file in Directory.EnumerateFiles(SourcePath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(SourcePath, file);
            if (IsBuildOutput(rel)) continue;
            var target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, File.ReadAllBytes(file));
        }

        return destination;
    }

    private static bool IsBuildOutput(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj" or "BenchmarkDotNet.Artifacts");
    }
}

/// <summary>Verifies the demo fixture is present, discoverable, isolated and copyable.</summary>
public class DemoFixtureTests
{
    /// <summary>The fixture directory is present in the source tree.</summary>
    [Fact]
    public void TheFixtureExists()
    {
        Assert.True(Directory.Exists(DemoFixture.SourcePath));
    }

    /// <summary>Discoverer finds the fixture's one benchmark method by its full name.</summary>
    [Fact]
    public void DiscoveryFindsTheBenchmark()
    {
        var found = Discoverer.Benchmarks(DemoFixture.SourcePath);
        Assert.Contains(found, b => b.FullName == "Demo.Benchmarks.WordCountBench.CountWords");
    }

    /// <summary>Discoverer classifies the fixture's test, benchmark and plain projects correctly.</summary>
    [Fact]
    public void DiscoveryClassifiesAllThreeProjects()
    {
        var projects = Discoverer.Projects(DemoFixture.SourcePath);

        Assert.Contains(projects, p => p.IsTestProject && p.ProjectPath.EndsWith("Demo.Tests.csproj"));
        Assert.Contains(projects, p => p.IsBenchmarkProject && p.ProjectPath.EndsWith("Demo.Benchmarks.csproj"));
        Assert.Contains(projects, p => !p.IsTestProject && !p.IsBenchmarkProject
                                       && p.ProjectPath.EndsWith("Demo.csproj"));
    }

    // The fixture must not inherit the harness's own build settings. Without its own
    // Directory.Build.props the MSBuild walk reaches the repository root and applies
    // TreatWarningsAsErrors to a fixture whose job is to be an ordinary consumer repo.
    // MSBuild looks up .props and .targets independently, each stopping at the first one
    // found walking up, so both files must be present or a future root-level
    // Directory.Build.targets would leak into the fixture undetected.
    /// <summary>The fixture's own Directory.Build.props and .targets exist, stopping the MSBuild walk.</summary>
    [Fact]
    public void TheFixtureStopsTheDirectoryBuildPropsWalk()
    {
        Assert.True(File.Exists(Path.Combine(DemoFixture.SourcePath, "Directory.Build.props")));
        Assert.True(File.Exists(Path.Combine(DemoFixture.SourcePath, "Directory.Build.targets")));
    }

    /// <summary>CopyTo reproduces the source files, including Directory.Build.props, without bin/obj.</summary>
    [Fact]
    public void CopyToProducesABuildableTreeWithoutBuildOutput()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"a3s-demo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        try
        {
            DemoFixture.CopyTo(dest);

            Assert.True(File.Exists(Path.Combine(dest, "src", "Demo", "WordCount.cs")));
            Assert.True(File.Exists(Path.Combine(dest, "bench", "Demo.Benchmarks", "Program.cs")));
            Assert.True(File.Exists(Path.Combine(dest, "Directory.Build.props")));
            Assert.Empty(Directory.EnumerateDirectories(dest, "obj", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(dest, "bin", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(dest, true); }
    }
}
