using Autor3Search.Core.Processes;

namespace Autor3Search.Cli.Tests;

/// <summary>Builds a committed git repository holding the demo fixture, for CLI tests.</summary>
internal static class TestRepo
{
    /// <summary>Copies the demo fixture into a fresh git repository and runs init.</summary>
    public static void CreateDemoGitRepo(string path)
    {
        Directory.CreateDirectory(path);
        CopyDemo(path);

        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("checkout", "-q", "-b", "main");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = InitCommand.RunAsync(
            Args.Parse(["init", "-C", path]), stdout, stderr, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (code != 0) throw new InvalidOperationException($"init failed: {stderr}");

        Git("add", "-A");
        Git("commit", "-q", "-m", "initial");

        void Git(params string[] args)
        {
            var runner = new Runner(path, TimeSpan.FromMinutes(2), null);
            var r = runner.RunAsync("git", args, CancellationToken.None).GetAwaiter().GetResult();
            if (!r.OK) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Tail(10)}");
        }
    }

    private static void CopyDemo(string destination)
    {
        var source = FindDemo();

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => p is "bin" or "obj" or "BenchmarkDotNet.Artifacts")) continue;

            var target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, File.ReadAllBytes(file));
        }
    }

    private static string FindDemo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testdata", "demo")))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException("could not locate testdata/demo")
            : Path.Combine(dir.FullName, "testdata", "demo");
    }

    /// <summary>Deletes a tree, tolerating the read-only files git leaves on Windows.</summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
