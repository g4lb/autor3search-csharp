using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Autor3Search.Core.Freezing;

namespace Autor3Search.Core.RunState;

/// <summary>
/// Out-of-tree run state: frozen copies, the baseline record, the pinned worktree, the
/// run claim and the stop markers.
///
/// All of it lives OUTSIDE the repository. The agent edits the repository, so anything
/// the score depends on that lived there would be silently writable by the very agent
/// it is meant to constrain.
/// </summary>
/// <param name="repoRoot">The repository this run belongs to.</param>
/// <param name="tag">The run tag.</param>
public sealed class StateStore(string repoRoot, string tag)
{
    /// <summary>Prefix for run branches.</summary>
    public const string BranchPrefix = "autor3search-csharp/";

    /// <summary>Directory name of the pinned baseline worktree.</summary>
    public const string WorktreeName = "baseline-worktree";

    /// <summary>The state directory for this repository and tag.</summary>
    public string Root { get; } = Paths.StateDir(repoRoot, tag);

    /// <summary>The pinned, detached baseline worktree.</summary>
    public string WorktreePath => Path.Combine(Root, WorktreeName);

    /// <summary>The frozen reference copies.</summary>
    public string FrozenStorePath => Path.Combine(Root, Freezer.StoreDirName);

    /// <summary>The frozen manifest.</summary>
    public string ManifestPath => Path.Combine(Root, Freezer.ManifestFileName);

    /// <summary>The baseline record.</summary>
    public string BaselinePath => Path.Combine(Root, "baseline.json");

    /// <summary>The pid file of a running eval.</summary>
    public string ClaimPath => Path.Combine(Root, "eval.pid");

    /// <summary>Marker asking the agent to stop after the current experiment.</summary>
    public string StopRequestPath => Path.Combine(Root, "stop-requested");

    /// <summary>Marker asking a running eval to abandon the current experiment.</summary>
    public string ForceStopPath => Path.Combine(Root, "force-stop");

    /// <summary>The run branch for a tag.</summary>
    public static string BranchForTag(string t) => BranchPrefix + t;

    /// <summary>The tag a run branch carries, or null when the branch is not a run branch.</summary>
    public static string? TagFromBranch(string branch) =>
        branch.StartsWith(BranchPrefix, StringComparison.Ordinal)
            ? branch[BranchPrefix.Length..]
            : null;

    /// <summary>True when a baseline has been recorded for this tag.</summary>
    public bool Exists => File.Exists(BaselinePath);

    /// <summary>Writes the baseline record.</summary>
    public void SaveBaseline(Baseline baseline)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(BaselinePath,
            JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Reads the baseline record.</summary>
    public Baseline LoadBaseline()
    {
        if (!File.Exists(BaselinePath))
        {
            throw new InvalidOperationException(
                $"no baseline recorded for tag \"{tag}\" — run 'autor3search-csharp baseline -tag {tag}' first");
        }

        return JsonSerializer.Deserialize<Baseline>(File.ReadAllText(BaselinePath))
               ?? throw new InvalidOperationException($"{BaselinePath} is not a valid baseline record");
    }

    /// <summary>Records that this process holds the run.</summary>
    public void WriteClaim(int pid)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(ClaimPath, pid.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The pid recorded in the claim, or null when none is held.</summary>
    public int? ReadClaim()
    {
        if (!File.Exists(ClaimPath)) return null;
        var text = File.ReadAllText(ClaimPath).Trim();
        return int.TryParse(text, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    /// <summary>Releases the claim. Safe to call when none is held.</summary>
    public void ClearClaim()
    {
        try { File.Delete(ClaimPath); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>
    /// True when the claim names a process that is still running.
    ///
    /// A crashed eval leaves its pid file behind. Treating that as a live run would
    /// wedge the repository until a human deleted a file nobody told them about, so
    /// liveness is checked rather than assumed.
    /// </summary>
    public bool IsClaimAlive()
    {
        var pid = ReadClaim();
        if (pid is null) return false;

        try
        {
            using var p = Process.GetProcessById(pid.Value);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True when a stop has been requested.</summary>
    public bool StopRequested => File.Exists(StopRequestPath);

    /// <summary>True when a running eval has been asked to abandon its experiment.</summary>
    public bool ForceStopRequested => File.Exists(ForceStopPath);

    /// <summary>Asks the agent to stop after the experiment it is running.</summary>
    public void RequestStop()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(StopRequestPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Additionally asks a running eval to abandon the current experiment.
    ///
    /// A file rather than a signal, deliberately. The Go implementation sends SIGTERM,
    /// which Windows cannot deliver process-to-process — leaving that platform unable
    /// to record what it abandoned. A marker the running eval polls behaves identically
    /// on macOS, Linux and Windows, and eval always gets to clean up after itself.
    /// </summary>
    public void RequestForceStop()
    {
        RequestStop();
        File.WriteAllText(ForceStopPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Cancels a pending stop, forced or not.</summary>
    public void ClearStop()
    {
        foreach (var p in new[] { StopRequestPath, ForceStopPath })
        {
            try { File.Delete(p); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
