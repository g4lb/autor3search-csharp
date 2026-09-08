using System.Reflection;

namespace Autor3Search.Cli;

/// <summary>Prints which build of the harness is running.</summary>
internal static class VersionCommand
{
    /// <summary>
    /// Runs `version`.
    ///
    /// A results.tsv row is only as reproducible as the binary that produced it, so
    /// this reports the informational version, which carries the source commit for a
    /// build made from a checkout.
    /// </summary>
    public static Task<int> RunAsync(
        Args args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        _ = args;
        _ = stderr;
        _ = ct;

        var asm = typeof(VersionCommand).Assembly;

        var informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        stdout.WriteLine(informational ?? asm.GetName().Version?.ToString() ?? "unknown");
        return Task.FromResult(0);
    }
}
