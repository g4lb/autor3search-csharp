using Autor3Search.Core.Doctoring;

namespace Autor3Search.Cli;

/// <summary>Reports whether this machine can measure reliably. Always exits 0.</summary>
internal static class DoctorCommand
{
    /// <summary>Runs `doctor`.</summary>
    public static async Task<int> RunAsync(
        Args args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        _ = stderr;

        var repo = Path.GetFullPath(args.GetString("C") ?? Directory.GetCurrentDirectory());
        var checks = await Doctor.RunAsync(repo, ct);

        foreach (var c in checks)
        {
            var marker = c.Status switch
            {
                CheckStatus.Ok => "ok  ",
                CheckStatus.Warn => "WARN",
                _ => "n/a ",
            };
            stdout.WriteLine($"{marker}  {c.Name,-24}  {c.Detail}");
        }

        var warnings = checks.Count(c => c.Status == CheckStatus.Warn);
        var unavailable = checks.Count(c => c.Status == CheckStatus.Unavailable);

        stdout.WriteLine();
        stdout.WriteLine(warnings == 0
            ? "no warnings — this machine looks fit to measure."
            : $"{warnings} warning(s). Numbers are only as good as the machine that produced them.");

        if (unavailable > 0)
        {
            stdout.WriteLine(
                $"{unavailable} check(s) could not be made on this platform. They are listed as n/a " +
                "rather than omitted, because silence would read as a pass.");
        }

        // Informational by contract: doctor never decides whether a run may proceed.
        return 0;
    }
}
