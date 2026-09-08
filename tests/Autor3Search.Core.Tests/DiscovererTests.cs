using Autor3Search.Core.Discovery;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for <see cref="Discoverer"/>: benchmark and project discovery, and file freezing.</summary>
public sealed class DiscovererTests : IDisposable
{
    private readonly string _root;

    /// <summary>Creates a fresh temporary repository root for each test.</summary>
    public DiscovererTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"a3s-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    /// <summary>Removes the temporary repository root.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private void Write(string relative, string content)
    {
        var abs = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private const string BenchProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup><PackageReference Include="BenchmarkDotNet" Version="0.15.8" /></ItemGroup>
        </Project>
        """;

    private const string XunitProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
        </Project>
        """;

    private const string LibProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    /// <summary>A method with [Benchmark] is found; a plain method on the same class is not.</summary>
    [Fact]
    public void BenchmarksAreFoundByAttribute()
    {
        Write("bench/Bench.csproj", BenchProject);
        Write("bench/WordCountBench.cs", """
            using BenchmarkDotNet.Attributes;
            namespace Demo;
            public class WordCountBench
            {
                [Benchmark]
                public int CountWords() => 0;

                public int NotABenchmark() => 0;
            }
            """);

        var found = Discoverer.Benchmarks(_root);
        Assert.Single(found);
        Assert.Equal("Demo.WordCountBench.CountWords", found[0].FullName);
    }

    /// <summary>The [BenchmarkAttribute] spelling is recognised the same as [Benchmark].</summary>
    [Fact]
    public void TheAttributeIsRecognisedWithItsSuffix()
    {
        Write("bench/Bench.csproj", BenchProject);
        Write("bench/B.cs", """
            namespace Demo;
            public class B { [BenchmarkAttribute] public void M() { } }
            """);

        Assert.Single(Discoverer.Benchmarks(_root));
    }

    /// <summary>A benchmark with no namespace has no leading dot in its FullName.</summary>
    [Fact]
    public void BenchmarksWithoutANamespaceOmitIt()
    {
        Write("bench/Bench.csproj", BenchProject);
        Write("bench/B.cs", """
            using BenchmarkDotNet.Attributes;
            public class TopLevel { [Benchmark] public void M() { } }
            """);

        Assert.Equal("TopLevel.M", Discoverer.Benchmarks(_root)[0].FullName);
    }

    /// <summary>Nested classes are qualified outermost-first in the FullName.</summary>
    [Fact]
    public void NestedClassesAreQualifiedByTheirOuterType()
    {
        Write("bench/Bench.csproj", BenchProject);
        Write("bench/B.cs", """
            using BenchmarkDotNet.Attributes;
            namespace Demo;
            public class Outer { public class Inner { [Benchmark] public void M() { } } }
            """);

        Assert.Equal("Demo.Outer.Inner.M", Discoverer.Benchmarks(_root)[0].FullName);
    }

    // Discovery must survive a tree that does not compile: init runs before anything
    // is known to be healthy, and a parse-only scan is what makes that possible.
    /// <summary>A benchmark is still found in a file containing unrelated invalid syntax.</summary>
    [Fact]
    public void BenchmarksAreFoundInAFileThatDoesNotCompile()
    {
        Write("bench/Bench.csproj", BenchProject);
        Write("bench/B.cs", """
            using BenchmarkDotNet.Attributes;
            namespace Demo;
            public class B
            {
                [Benchmark] public void M() { }
                public void Broken() { this is not valid C# at all ((( }
            }
            """);

        Assert.Contains(Discoverer.Benchmarks(_root), b => b.FullName == "Demo.B.M");
    }

    /// <summary>Files under bin/ and obj/ are never scanned for benchmarks.</summary>
    [Fact]
    public void BuildOutputIsNotScanned()
    {
        Write("bench/Bench.csproj", BenchProject);
        Write("bench/obj/Generated.cs", """
            using BenchmarkDotNet.Attributes;
            public class Ghost { [Benchmark] public void M() { } }
            """);
        Write("bench/bin/Release/Copy.cs", """
            using BenchmarkDotNet.Attributes;
            public class Ghost2 { [Benchmark] public void M() { } }
            """);

        Assert.Empty(Discoverer.Benchmarks(_root));
    }

    /// <summary>Discovered benchmarks are sorted by FullName.</summary>
    [Fact]
    public void BenchmarksAreSortedByFullName()
    {
        Write("bench/Bench.csproj", BenchProject);
        Write("bench/B.cs", """
            using BenchmarkDotNet.Attributes;
            namespace Demo;
            public class Z { [Benchmark] public void M() { } }
            public class A { [Benchmark] public void M() { } }
            """);

        var found = Discoverer.Benchmarks(_root);
        Assert.Equal(["Demo.A.M", "Demo.Z.M"], found.Select(b => b.FullName));
    }

    /// <summary>A project referencing a known test framework package is a test project.</summary>
    [Theory]
    [InlineData("xunit")]
    [InlineData("NUnit")]
    [InlineData("MSTest.TestFramework")]
    public void ProjectsReferencingATestFrameworkAreTestProjects(string package)
    {
        Write("tests/T.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="{package}" Version="1.0.0" /></ItemGroup>
            </Project>
            """);

        var p = Discoverer.Projects(_root).Single();
        Assert.True(p.IsTestProject);
        Assert.False(p.IsBenchmarkProject);
    }

    /// <summary>Setting IsTestProject true also marks a project as a test project.</summary>
    [Fact]
    public void IsTestProjectPropertyAlsoMarksATestProject()
    {
        Write("tests/T.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
            </Project>
            """);

        Assert.True(Discoverer.Projects(_root).Single().IsTestProject);
    }

    /// <summary>A project referencing BenchmarkDotNet is a benchmark project.</summary>
    [Fact]
    public void ProjectsReferencingBenchmarkDotNetAreBenchmarkProjects()
    {
        Write("bench/Bench.csproj", BenchProject);
        var p = Discoverer.Projects(_root).Single();
        Assert.True(p.IsBenchmarkProject);
        Assert.False(p.IsTestProject);
    }

    /// <summary>A plain library project is neither a test nor a benchmark project.</summary>
    [Fact]
    public void APlainLibraryIsNeither()
    {
        Write("src/Lib.csproj", LibProject);
        var p = Discoverer.Projects(_root).Single();
        Assert.False(p.IsTestProject);
        Assert.False(p.IsBenchmarkProject);
    }

    /// <summary>Freezing a project covers every .cs file beneath it, including helpers, plus the project file.</summary>
    [Fact]
    public void FrozenFilesCoverEveryCsFileAndTheProjectFile()
    {
        Write("tests/T.csproj", XunitProject);
        Write("tests/ATests.cs", "class A {}");
        Write("tests/helpers/Helper.cs", "class H {}");
        Write("src/Lib.csproj", LibProject);
        Write("src/Code.cs", "class C {}");

        var frozen = Discoverer.FrozenFiles(_root, ["tests/T.csproj"], []);

        Assert.Contains("tests/T.csproj", frozen);
        Assert.Contains("tests/ATests.cs", frozen);
        Assert.Contains("tests/helpers/Helper.cs", frozen);   // helpers freeze too, by design
        Assert.DoesNotContain("src/Code.cs", frozen);          // outside the frozen project
    }

    /// <summary>Build output beneath a frozen project is excluded from the frozen set.</summary>
    [Fact]
    public void FrozenFilesExcludeBuildOutput()
    {
        Write("tests/T.csproj", XunitProject);
        Write("tests/ATests.cs", "class A {}");
        Write("tests/obj/Gen.cs", "class G {}");
        Write("tests/bin/Debug/Out.cs", "class O {}");

        var frozen = Discoverer.FrozenFiles(_root, ["tests/T.csproj"], []);
        Assert.DoesNotContain(frozen, f => f.Contains("/obj/") || f.Contains("/bin/"));
    }

    /// <summary>Files listed in the unfreeze scope are excluded from the frozen set.</summary>
    [Fact]
    public void FrozenFilesHonourUnfreeze()
    {
        Write("tests/T.csproj", XunitProject);
        Write("tests/ATests.cs", "class A {}");
        Write("tests/Scratch.cs", "class S {}");

        var frozen = Discoverer.FrozenFiles(_root, ["tests/T.csproj"], ["tests/Scratch.cs"]);
        Assert.Contains("tests/ATests.cs", frozen);
        Assert.DoesNotContain("tests/Scratch.cs", frozen);
    }

    /// <summary>Frozen file paths use forward slashes and are sorted ordinally.</summary>
    [Fact]
    public void FrozenPathsUseForwardSlashesAndAreSorted()
    {
        Write("tests/T.csproj", XunitProject);
        Write("tests/z/Z.cs", "class Z {}");
        Write("tests/a/A.cs", "class A {}");

        var frozen = Discoverer.FrozenFiles(_root, ["tests/T.csproj"], []);
        Assert.All(frozen, f => Assert.DoesNotContain('\\', f));
        Assert.Equal(frozen.OrderBy(f => f, StringComparer.Ordinal), frozen);
    }

    /// <summary>DependencyFiles contains the documented set of always-rejected paths.</summary>
    [Fact]
    public void DependencyFilesAreTheDocumentedSet()
    {
        Assert.Contains("Directory.Packages.props", Discoverer.DependencyFiles);
        Assert.Contains("Directory.Build.props", Discoverer.DependencyFiles);
        Assert.Contains("packages.lock.json", Discoverer.DependencyFiles);
        Assert.Contains("nuget.config", Discoverer.DependencyFiles);
    }
}
