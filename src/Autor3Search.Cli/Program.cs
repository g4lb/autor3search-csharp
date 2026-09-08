namespace Autor3Search.Cli;

/// <summary>Entry point: subcommand dispatch and exit codes only.</summary>
public static class Program
{
    /// <summary>Runs the CLI. Exit codes: 0 KEEP, 1 DISCARD, 2 FAIL, 3 CRASH.</summary>
    public static async Task<int> Main(string[] args)
    {
        var parsed = Args.Parse(args);
        var ct = CancellationToken.None;

        try
        {
            return parsed.Command switch
            {
                "init" => await InitCommand.RunAsync(parsed, Console.Out, Console.Error, ct),
                "baseline" => await BaselineCommand.RunAsync(parsed, Console.Out, Console.Error, ct),
                "eval" => await EvalCommand.RunAsync(parsed, Console.Out, Console.Error, ct),
                "status" => await StatusCommand.RunAsync(parsed, Console.Out, Console.Error, ct),
                "stop" => await StopCommand.RunAsync(parsed, Console.Out, Console.Error, ct),
                "report" => await ReportCommand.RunAsync(parsed, Console.Out, Console.Error, ct),
                "doctor" => await DoctorCommand.RunAsync(parsed, Console.Out, Console.Error, ct),
                "profile" => await ProfileCommand.RunAsync(parsed, Console.Out, Console.Error, ct),
                "version" => await VersionCommand.RunAsync(parsed, Console.Out, Console.Error, ct),
                "" => Usage(),
                _ => Unknown(parsed.Command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage: autor3search-csharp <command> [flags]\n\n" +
            "  init      scan the repository, write config.yaml and program.md\n" +
            "  doctor    check whether this machine can measure reliably\n" +
            "  baseline  freeze tests, pin the baseline commit\n" +
            "  profile   run the benchmarks under a profiler\n" +
            "  eval      run one experiment and return a verdict\n" +
            "  status    show where a run is\n" +
            "  stop      ask the run to end\n" +
            "  report    summarize results.tsv\n" +
            "  version   print the build\n\n" +
            "every command accepts -C <dir> to operate on another repository");
        return 2;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        return 2;
    }
}
