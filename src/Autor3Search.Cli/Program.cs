namespace Autor3Search.Cli;

/// <summary>Entry point: subcommand dispatch and exit codes only.</summary>
public static class Program
{
    /// <summary>Runs the CLI. Exit codes: 0 KEEP, 1 DISCARD, 2 FAIL, 3 CRASH.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: autor3search-csharp <command> [flags]");
            return 2;
        }

        return args[0] switch
        {
            "version" => RunVersion(),
            _ => Unknown(args[0]),
        };
    }

    private static int RunVersion()
    {
        Console.WriteLine(ThisAssembly.InformationalVersion);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        return 2;
    }
}

/// <summary>Build identity, replaced with real version metadata in Task 20.</summary>
internal static class ThisAssembly
{
    /// <summary>The informational version of the running build.</summary>
    public static string InformationalVersion =>
        typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0-dev";
}
