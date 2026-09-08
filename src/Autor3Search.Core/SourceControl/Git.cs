using Autor3Search.Core.Processes;

namespace Autor3Search.Core.SourceControl;

/// <summary>Git operations the harness needs, as subprocess calls.</summary>
public static class Git
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private static async Task<RunResult> RunAsync(
        string dir, CancellationToken ct, params string[] args)
    {
        var runner = new Runner(dir, DefaultTimeout, null);
        return await runner.RunAsync("git", args, ct).ConfigureAwait(false);
    }

    private static async Task<string> RunCheckedAsync(
        string dir, CancellationToken ct, params string[] args)
    {
        var r = await RunAsync(dir, ct, args).ConfigureAwait(false);
        if (!r.OK)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed in {dir} (exit {r.ExitCode}):\n{r.Tail(20)}");
        }
        return r.Stdout.Trim();
    }

    /// <summary>
    /// The repository root containing <paramref name="startDir"/>.
    ///
    /// The returned path is git's own canonical, symlink-resolved form (from
    /// <c>rev-parse --show-toplevel</c>), which need not equal <paramref name="startDir"/>
    /// or any prefix of it byte-for-byte — on macOS, for instance, a temp directory
    /// under /var resolves through /private/var. Callers that derive further paths or
    /// cache keys (see <see cref="Paths.RepoHash"/>) from the repository root must use
    /// THIS RETURN VALUE, not the path they originally passed in, or the same
    /// repository can end up addressed under two different keys.
    /// </summary>
    public static async Task<string> RootAsync(string startDir, CancellationToken ct)
    {
        var r = await RunAsync(startDir, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (!r.OK)
        {
            throw new InvalidOperationException(
                $"{startDir} is not a git repository — this tool works on committed history, " +
                "because a baseline pinned against what is on disk would not be reproducible");
        }
        return r.Stdout.Trim();
    }

    /// <summary>The full SHA of HEAD.</summary>
    public static Task<string> HeadCommitAsync(string dir, CancellationToken ct) =>
        RunCheckedAsync(dir, ct, "rev-parse", "HEAD");

    /// <summary>True when the working tree has no changes, tracked or untracked.</summary>
    public static async Task<bool> IsCleanAsync(string dir, CancellationToken ct)
    {
        var status = await RunCheckedAsync(dir, ct, "status", "--porcelain").ConfigureAwait(false);
        return status.Length == 0;
    }

    /// <summary>
    /// Repo-relative paths changed since a commit, including untracked files.
    ///
    /// Untracked files count: an agent adding a new source file has changed the tree
    /// just as much as one editing an existing file, and the scope gate must see it.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ChangedSinceAsync(
        string dir, string commit, CancellationToken ct)
    {
        var tracked = await RunCheckedAsync(dir, ct, "diff", "--name-only", commit).ConfigureAwait(false);
        var untracked = await RunCheckedAsync(
            dir, ct, "ls-files", "--others", "--exclude-standard").ConfigureAwait(false);

        return (tracked + "\n" + untracked)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Paths.ToSlash(l.Trim()))
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The checked-out branch name.</summary>
    public static Task<string> CurrentBranchAsync(string dir, CancellationToken ct) =>
        RunCheckedAsync(dir, ct, "rev-parse", "--abbrev-ref", "HEAD");

    /// <summary>True when the branch exists.</summary>
    public static async Task<bool> BranchExistsAsync(string dir, string branch, CancellationToken ct)
    {
        var r = await RunAsync(dir, ct,
            "show-ref", "--verify", "--quiet", $"refs/heads/{branch}").ConfigureAwait(false);
        return r.ExitCode == 0;
    }

    /// <summary>Creates the branch and checks it out.</summary>
    public static async Task CreateBranchAsync(string dir, string branch, CancellationToken ct) =>
        await RunCheckedAsync(dir, ct, "checkout", "-b", branch).ConfigureAwait(false);

    /// <summary>Adds a detached worktree pinned at a commit.</summary>
    public static async Task AddWorktreeAsync(
        string dir, string worktreePath, string commit, CancellationToken ct) =>
        await RunCheckedAsync(dir, ct, "worktree", "add", "--detach", worktreePath, commit)
            .ConfigureAwait(false);

    /// <summary>Re-points an existing detached worktree at another commit.</summary>
    public static async Task MoveWorktreeToAsync(
        string worktreePath, string commit, CancellationToken ct) =>
        await RunCheckedAsync(worktreePath, ct, "checkout", "--detach", commit).ConfigureAwait(false);

    /// <summary>Removes a worktree registration, tolerating one already gone.</summary>
    public static async Task RemoveWorktreeAsync(string dir, string worktreePath, CancellationToken ct) =>
        await RunAsync(dir, ct, "worktree", "remove", "--force", worktreePath).ConfigureAwait(false);
}
