using Autor3Search.Core;
using Autor3Search.Core.Processes;
using Autor3Search.Core.SourceControl;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for <see cref="Git"/>, run against real temporary repositories.</summary>
public sealed class GitTests : IDisposable
{
    private readonly string _repo;

    /// <summary>Initializes a fresh temp repository with one commit.</summary>
    public GitTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"a3s-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);

        Run("init", "-q");
        Run("config", "user.name", "Test");
        Run("config", "user.email", "test@example.com");
        Run("config", "commit.gpgsign", "false");

        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one");
        Run("add", "-A");
        Run("commit", "-q", "-m", "first");
    }

    /// <summary>Removes the temp repository, tolerating leftover read-only git objects.</summary>
    public void Dispose()
    {
        try { if (Directory.Exists(_repo)) Directory.Delete(_repo, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Run(params string[] args)
    {
        var runner = new Runner(_repo, TimeSpan.FromSeconds(60), null);
        var r = runner.RunAsync("git", args, CancellationToken.None).GetAwaiter().GetResult();
        if (!r.OK) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {r.Tail(10)}");
    }

    /// <summary>The repository root is found from a nested subdirectory.</summary>
    [Fact]
    public async Task RootIsFoundFromASubdirectory()
    {
        var sub = Path.Combine(_repo, "nested", "deeper");
        Directory.CreateDirectory(sub);

        var root = await Git.RootAsync(sub, CancellationToken.None);

        // Git.RootAsync returns git's own canonical, symlink-resolved form (see its
        // XML doc); Path.GetFullPath on _repo does not resolve symlinks, so on macOS
        // (/var -> /private/var) the two need reconciling through the same realpath
        // algorithm Paths.RepoHash relies on, rather than a second copy of it here.
        Assert.Equal(
            Paths.ResolveRealPath(Path.GetFullPath(_repo)),
            Paths.ResolveRealPath(Path.GetFullPath(root)));
    }

    /// <summary>HEAD resolves to a full 40-character lowercase hex SHA.</summary>
    [Fact]
    public async Task HeadCommitIsAFullSha()
    {
        var head = await Git.HeadCommitAsync(_repo, CancellationToken.None);
        Assert.Equal(40, head.Length);
        Assert.Matches("^[0-9a-f]{40}$", head);
    }

    /// <summary>A freshly committed tree is clean.</summary>
    [Fact]
    public async Task ACommittedTreeIsClean()
    {
        Assert.True(await Git.IsCleanAsync(_repo, CancellationToken.None));
    }

    /// <summary>Editing a tracked file makes the tree dirty.</summary>
    [Fact]
    public async Task AnUncommittedChangeMakesTheTreeDirty()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "changed");
        Assert.False(await Git.IsCleanAsync(_repo, CancellationToken.None));
    }

    /// <summary>An untracked file also makes the tree dirty.</summary>
    [Fact]
    public async Task AnUntrackedFileAlsoMakesTheTreeDirty()
    {
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "new");
        Assert.False(await Git.IsCleanAsync(_repo, CancellationToken.None));
    }

    /// <summary>ChangedSinceAsync lists both committed and untracked changes since a commit.</summary>
    [Fact]
    public async Task ChangedSinceListsCommittedAndUncommittedChanges()
    {
        var baseCommit = await Git.HeadCommitAsync(_repo, CancellationToken.None);

        File.WriteAllText(Path.Combine(_repo, "b.txt"), "two");
        Run("add", "-A");
        Run("commit", "-q", "-m", "second");
        File.WriteAllText(Path.Combine(_repo, "c.txt"), "three");

        var changed = await Git.ChangedSinceAsync(_repo, baseCommit, CancellationToken.None);

        Assert.Contains("b.txt", changed);
        Assert.Contains("c.txt", changed);   // untracked, and therefore in scope
        Assert.DoesNotContain("a.txt", changed);
    }

    /// <summary>Paths returned by ChangedSinceAsync use forward slashes regardless of platform.</summary>
    [Fact]
    public async Task ChangedSincePathsUseForwardSlashes()
    {
        var baseCommit = await Git.HeadCommitAsync(_repo, CancellationToken.None);
        var nested = Path.Combine(_repo, "deep", "dir");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "d.txt"), "four");

        var changed = await Git.ChangedSinceAsync(_repo, baseCommit, CancellationToken.None);
        Assert.Contains("deep/dir/d.txt", changed);
    }

    /// <summary>A branch can be created and is then detected as current and existing.</summary>
    [Fact]
    public async Task BranchesAreCreatedAndDetected()
    {
        Assert.False(await Git.BranchExistsAsync(_repo, "autor3search-csharp/sep8", CancellationToken.None));

        await Git.CreateBranchAsync(_repo, "autor3search-csharp/sep8", CancellationToken.None);

        Assert.True(await Git.BranchExistsAsync(_repo, "autor3search-csharp/sep8", CancellationToken.None));
        Assert.Equal("autor3search-csharp/sep8", await Git.CurrentBranchAsync(_repo, CancellationToken.None));
    }

    /// <summary>A worktree is pinned at a commit on creation and can be re-pointed later.</summary>
    [Fact]
    public async Task AWorktreeIsPinnedAtACommitAndCanBeMoved()
    {
        var first = await Git.HeadCommitAsync(_repo, CancellationToken.None);

        File.WriteAllText(Path.Combine(_repo, "b.txt"), "two");
        Run("add", "-A");
        Run("commit", "-q", "-m", "second");
        var second = await Git.HeadCommitAsync(_repo, CancellationToken.None);

        var wt = Path.Combine(Path.GetTempPath(), $"a3s-wt-{Guid.NewGuid():N}");
        try
        {
            await Git.AddWorktreeAsync(_repo, wt, first, CancellationToken.None);
            Assert.Equal(first, await Git.HeadCommitAsync(wt, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(wt, "b.txt")));

            await Git.MoveWorktreeToAsync(wt, second, CancellationToken.None);
            Assert.Equal(second, await Git.HeadCommitAsync(wt, CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(wt, "b.txt")));
        }
        finally
        {
            try { Directory.Delete(wt, true); } catch (IOException) { }
        }
    }

    /// <summary>RootAsync fails with a clear message when run outside a git repository.</summary>
    [Fact]
    public async Task RootOutsideARepositoryFailsWithAClearMessage()
    {
        var notARepo = Path.Combine(Path.GetTempPath(), $"a3s-norepo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notARepo);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Git.RootAsync(notARepo, CancellationToken.None));
            Assert.Contains("not a git repository", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(notARepo, true); }
    }
}
