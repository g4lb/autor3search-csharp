using Autor3Search.Core.RunState;
using Autor3Search.Core.SourceControl;

namespace Autor3Search.Cli;

/// <summary>Asks a run to end.</summary>
internal static class StopCommand
{
    /// <summary>Runs `stop`, `stop -clear` or `stop -force`.</summary>
    public static async Task<int> RunAsync(
        Args args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var start = Path.GetFullPath(args.GetString("C") ?? Directory.GetCurrentDirectory());
        var repo = await Git.RootAsync(start, ct);
        var tag = await RunTag.ResolveAsync(args, repo, ct);

        var store = new StateStore(repo, tag);
        if (!store.Exists)
        {
            stderr.WriteLine($"no run found for tag \"{tag}\" — nothing recorded at {store.Root}");
            return 2;
        }

        if (args.GetFlag("clear"))
        {
            store.ClearStop();
            stdout.WriteLine($"pending stop for \"{tag}\" cancelled. The run continues.");
            return 0;
        }

        if (args.GetFlag("force"))
        {
            store.RequestForceStop();

            stdout.WriteLine($"stop requested for \"{tag}\", and the running experiment has been");
            stdout.WriteLine("asked to abandon what it is measuring.");
            stdout.WriteLine();
            stdout.WriteLine("The abandoned experiment is lost: nothing was measured, so no results.tsv");
            stdout.WriteLine("row is written for it. Every commit kept before it is untouched.");
            stdout.WriteLine();
            stdout.WriteLine("The agent may have committed the abandoned experiment. If so, drop it:");
            stdout.WriteLine();
            stdout.WriteLine("  git log --oneline -1        # check what the last commit is");
            stdout.WriteLine("  git reset --hard HEAD~1     # drop it, if it is the abandoned experiment");
            stdout.WriteLine();
            stdout.WriteLine("This command does not drop anything for you.");
            return 0;
        }

        store.RequestStop();

        stdout.WriteLine($"stop requested for \"{tag}\" — the run ends after the current experiment.");
        stdout.WriteLine();
        stdout.WriteLine("The experiment under way finishes and is scored, its verdict is applied,");
        stdout.WriteLine("and only then does the loop exit. Nothing is thrown away.");
        stdout.WriteLine();
        stdout.WriteLine("changed your mind:  autor3search-csharp stop -clear");
        stdout.WriteLine("cannot wait:        autor3search-csharp stop -force");

        return 0;
    }
}
