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

    /// <summary>
    /// The exact manifest resource name, not a suffix match: a future embedded resource
    /// whose name also ends in "program.md" must never be picked up by mistake — this
    /// file is a control program, and the wrong one is a bad outcome.
    /// </summary>
    private const string ResourceName = "Autor3Search.Core.Templates.program.md";

    private static string ReadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the assembly");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
