using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Autor3Search.Core.Discovery;

/// <summary>One discovered benchmark method.</summary>
/// <param name="FullName">BenchmarkDotNet identity: Namespace.Type.Method.</param>
/// <param name="File">Repo-relative file declaring it, forward slashes.</param>
/// <param name="Project">Repo-relative project owning that file, forward slashes.</param>
public readonly record struct DiscoveredBenchmark(string FullName, string File, string Project);

/// <summary>One project in the repository, classified.</summary>
/// <param name="ProjectPath">Repo-relative .csproj path, forward slashes.</param>
/// <param name="Directory">Repo-relative project directory, forward slashes.</param>
/// <param name="IsTestProject">References a test framework, or sets IsTestProject.</param>
/// <param name="IsBenchmarkProject">References BenchmarkDotNet.</param>
public readonly record struct DiscoveredProject(
    string ProjectPath,
    string Directory,
    bool IsTestProject,
    bool IsBenchmarkProject);

/// <summary>
/// Finds benchmarks and classifies projects, without invoking MSBuild or the compiler.
///
/// Roslyn parses here but never compiles: <c>init</c> runs before the repository is
/// known to build, and a scan that required a healthy tree would be useless exactly
/// when it is most needed.
/// </summary>
public static class Discoverer
{
    /// <summary>
    /// Files whose modification is rejected regardless of scope. Changing a dependency
    /// is a human supply-chain decision, and it changes WHAT is measured rather than
    /// how fast it runs.
    /// </summary>
    public static readonly string[] DependencyFiles =
    [
        "Directory.Packages.props",
        "Directory.Build.props",
        "Directory.Build.targets",
        "packages.lock.json",
        "nuget.config",
        "NuGet.config",
        "NuGet.Config",
    ];

    private static readonly string[] TestFrameworkPackages =
    [
        "xunit", "xunit.v3", "NUnit", "MSTest.TestFramework", "MSTest",
    ];

    private static readonly string[] SkippedDirectories =
    [
        "bin", "obj", "node_modules", "packages", "TestResults", "artifacts",
    ];

    /// <summary>Every project in the repository, classified. Sorted by path.</summary>
    public static IReadOnlyList<DiscoveredProject> Projects(string repoRoot)
    {
        var projects = new List<DiscoveredProject>();

        foreach (var abs in EnumerateFiles(repoRoot, "*.csproj"))
        {
            var rel = Relative(repoRoot, abs);
            var (isTest, isBench) = Classify(abs);
            var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
            projects.Add(new DiscoveredProject(rel, dir, isTest, isBench));
        }

        return projects.OrderBy(p => p.ProjectPath, StringComparer.Ordinal).ToArray();
    }

    private static (bool IsTest, bool IsBenchmark) Classify(string csprojPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(csprojPath);
        }
        catch (System.Xml.XmlException)
        {
            // A malformed project file is not a discovery failure: it classifies as
            // neither, and the build gate will report it properly later.
            return (false, false);
        }

        var packages = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? "")
            .ToList();

        var isTest =
            packages.Any(p => TestFrameworkPackages.Contains(p, StringComparer.OrdinalIgnoreCase))
            || doc.Descendants()
                .Any(e => e.Name.LocalName == "IsTestProject"
                          && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

        var isBench = packages.Any(p =>
            p.Equals("BenchmarkDotNet", StringComparison.OrdinalIgnoreCase));

        return (isTest, isBench);
    }

    /// <summary>Every benchmark method in the repository, sorted by FullName.</summary>
    public static IReadOnlyList<DiscoveredBenchmark> Benchmarks(string repoRoot)
    {
        var projects = Projects(repoRoot);
        var found = new List<DiscoveredBenchmark>();

        foreach (var abs in EnumerateFiles(repoRoot, "*.cs"))
        {
            var rel = Relative(repoRoot, abs);
            var project = OwningProject(projects, rel);

            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(abs));
            var root = tree.GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!HasBenchmarkAttribute(method)) continue;
                found.Add(new DiscoveredBenchmark(QualifiedName(method), rel, project));
            }
        }

        return found.OrderBy(b => b.FullName, StringComparer.Ordinal).ToArray();
    }

    private static bool HasBenchmarkAttribute(MethodDeclarationSyntax method) =>
        method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Select(a => a.Name.ToString())
            .Select(n => n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n)
            .Any(n => n is "Benchmark" or "BenchmarkAttribute");

    /// <summary>
    /// Builds BenchmarkDotNet's FullName for a method: namespace, then every enclosing
    /// type outermost-first, then the method. Parameters are not included — they are
    /// only known at runtime and appear in the report, which is what the comparison
    /// keys on.
    /// </summary>
    private static string QualifiedName(MethodDeclarationSyntax method)
    {
        var parts = new List<string>();

        for (SyntaxNode? node = method.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case TypeDeclarationSyntax t:
                    parts.Insert(0, t.Identifier.Text);
                    break;
                case NamespaceDeclarationSyntax ns:
                    parts.Insert(0, ns.Name.ToString());
                    break;
                case FileScopedNamespaceDeclarationSyntax fns:
                    parts.Insert(0, fns.Name.ToString());
                    break;
            }
        }

        parts.Add(method.Identifier.Text);
        return string.Join(".", parts);
    }

    /// <summary>
    /// Every file frozen for the given projects: all .cs beneath each project's
    /// directory, plus the project file itself. Sorted, forward slashes.
    ///
    /// Coarser than freezing individual test files, deliberately: C# has no filename
    /// convention for tests, and the guarantee that matters is that the agent cannot
    /// weaken an assertion or add an easier benchmark. A helper living in a test
    /// project freezes with it.
    /// </summary>
    public static IReadOnlyList<string> FrozenFiles(
        string repoRoot, IEnumerable<string> projectPaths, IEnumerable<string> unfreeze)
    {
        var exempt = new HashSet<string>(unfreeze.Select(Paths.ToSlash), StringComparer.Ordinal);
        var frozen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var projectRel in projectPaths.Select(Paths.ToSlash))
        {
            if (!exempt.Contains(projectRel)) frozen.Add(projectRel);

            var projectDir = Path.GetDirectoryName(Path.Combine(repoRoot, Paths.FromSlash(projectRel)));
            if (projectDir is null || !Directory.Exists(projectDir)) continue;

            foreach (var abs in EnumerateFiles(projectDir, "*.cs"))
            {
                var rel = Relative(repoRoot, abs);
                if (!exempt.Contains(rel)) frozen.Add(rel);
            }
        }

        return frozen.OrderBy(f => f, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Walks a tree, skipping build output, VCS metadata and dot-directories.</summary>
    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name.StartsWith('_')) continue;
                if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                // Never descend a symlinked directory: it can leave the repository
                // entirely, and Freezer refuses such paths anyway.
                if (new DirectoryInfo(sub).LinkTarget is not null) continue;

                stack.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir, pattern)) yield return file;
        }
    }

    private static string Relative(string root, string absolute) =>
        Paths.ToSlash(Path.GetRelativePath(root, absolute));

    private static string OwningProject(IReadOnlyList<DiscoveredProject> projects, string fileRel)
    {
        string best = "";
        foreach (var p in projects)
        {
            var prefix = p.Directory.Length == 0 ? "" : p.Directory + "/";
            if (!fileRel.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (p.Directory.Length >= best.Length) best = p.ProjectPath;
        }
        return best;
    }
}
