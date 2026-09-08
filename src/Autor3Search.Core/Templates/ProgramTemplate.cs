using System.Reflection;

namespace Autor3Search.Core.Templates;

/// <summary>Renders the agent instruction set written into a repository as program.md.</summary>
public static class ProgramTemplate
{
    /// <summary>Fills in the discovered benchmark set and benchmark project.</summary>
    public static string Render(IReadOnlyList<string> benchmarks, string benchmarkProject)
    {
        var template = ReadEmbedded();

        var list = benchmarks.Count == 0
            ? "_none discovered_"
            : string.Join("\n", benchmarks.Select(b => $"- `{b}`"));

        return template
            .Replace("{{BENCHMARKS}}", list, StringComparison.Ordinal)
            .Replace("{{BENCHMARK_PROJECT}}", benchmarkProject, StringComparison.Ordinal);
    }

    private static string ReadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
                       .FirstOrDefault(n => n.EndsWith("program.md", StringComparison.Ordinal))
                   ?? throw new InvalidOperationException("program.md is not embedded in the assembly");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
