using System.Security.Cryptography;
using System.Text;

namespace Autor3Search.Core;

/// <summary>
/// Path normalization and the location of out-of-tree run state.
///
/// Two rules hold everywhere in this codebase. Every path that is RECORDED —
/// in a manifest, a config, a results row — is stored with forward slashes, so
/// state written on Windows is readable on Linux. Every path that TOUCHES the
/// filesystem is converted back with <see cref="FromSlash"/> first.
/// </summary>
public static class Paths
{
    /// <summary>The application directory name used under the state home.</summary>
    public const string AppName = "autor3search-csharp";

    /// <summary>Environment variable that overrides the state home. Must be absolute.</summary>
    public const string StateHomeEnvVar = "AUTOR3SEARCH_CSHARP_STATE_HOME";

    /// <summary>Converts a native path to its recorded, forward-slash form.</summary>
    public static string ToSlash(string path) => path.Replace('\\', '/');

    /// <summary>Converts a recorded, forward-slash path to this platform's form.</summary>
    public static string FromSlash(string path) =>
        Path.DirectorySeparatorChar == '/' ? path : path.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Resolves the directory holding run state for every repository and tag.
    ///
    /// This deliberately does NOT use Environment.SpecialFolder.LocalApplicationData,
    /// which resolves to ~/Library/Application Support on macOS. The Go implementation
    /// uses os.UserCacheDir (~/Library/Caches there), and a user running both tools on
    /// one machine should find their state in the same place.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The override is set but not fully qualified. A relative value would resolve
    /// against whatever directory each command happened to run from, so eval from a
    /// subdirectory and stop from the repository root would address different state
    /// for the same run.
    /// </exception>
    public static string StateHome()
    {
        var overridden = Environment.GetEnvironmentVariable(StateHomeEnvVar);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            if (!Path.IsPathFullyQualified(overridden))
            {
                throw new InvalidOperationException(
                    $"{StateHomeEnvVar} must be an absolute path, got \"{overridden}\". " +
                    "A relative value resolves against each command's working directory, " +
                    "so different commands would address different state for the same run.");
            }
            return overridden;
        }

        return Path.Combine(DefaultCacheRoot(), AppName);
    }

    private static string DefaultCacheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(local)) return local;
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Caches");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg)) return xdg;
        return Path.Combine(home, ".cache");
    }

    /// <summary>
    /// A short, stable key for a repository, so two repositories on one machine
    /// never share run state. Derived from the full path rather than the directory
    /// name, which collides constantly.
    ///
    /// The path is canonicalised (symlinks resolved) before hashing WHEN IT EXISTS
    /// on disk. Without this, the same repository reachable through two spellings —
    /// for example macOS's temp directory, where /var is itself a symlink to
    /// /private/var — would hash to two different keys and address two different
    /// state directories. A run whose baseline was recorded under one spelling and
    /// whose eval arrives under the other would silently miss its baseline, its
    /// frozen manifest, and its pinned worktree, and report a bare "no baseline
    /// recorded" instead of anything diagnostic. A path that does not exist (as in
    /// several tests, and possibly a repository not yet checked out) falls back to
    /// its literal spelling — there is nothing on disk to resolve.
    /// </summary>
    public static string RepoHash(string repoRoot)
    {
        var canonical = CanonicalizeIfExists(repoRoot);
        var normalized = ToSlash(Path.TrimEndingDirectorySeparator(canonical));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes)[..8];
    }

    /// <summary>The state directory for one repository and one run tag.</summary>
    public static string StateDir(string repoRoot, string tag) =>
        Path.Combine(StateHome(), RepoHash(repoRoot), tag);

    /// <summary>
    /// Resolves <paramref name="path"/> to its real, symlink-free form when it exists
    /// on disk; returns it unchanged otherwise. Falls back to the input on any I/O or
    /// permission failure — a filesystem hiccup while computing a cache key must never
    /// crash the tool.
    /// </summary>
    private static string CanonicalizeIfExists(string path)
    {
        if (!Directory.Exists(path)) return path;

        try
        {
            return ResolveRealPath(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static readonly char[] SeparatorChars =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// A minimal <c>realpath(3)</c>: resolves every symlink in <paramref name="fullPath"/>,
    /// including one sitting in an ANCESTOR directory rather than at the leaf — .NET's
    /// <see cref="FileSystemInfo.LinkTarget"/> only resolves a path that IS itself a
    /// link, so a plain leaf-level resolve would miss exactly the macOS /var case this
    /// exists for. Segments are processed from a work queue rather than a single
    /// left-to-right pass because a symlink's OWN target can reintroduce further
    /// unresolved segments — including further ancestor symlinks — that must be
    /// re-walked from wherever they land, not appended past as if already resolved.
    /// </summary>
    private static string ResolveRealPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var resolvedRoot = root.Length > 0 ? root.TrimEnd(SeparatorChars) : string.Empty;
        if (resolvedRoot.Length == 0 && Path.DirectorySeparatorChar == '/') resolvedRoot = "/";

        var resolved = resolvedRoot;
        var remaining = new List<string>(
            fullPath[root.Length..].Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries));

        var hops = 0;
        while (remaining.Count > 0)
        {
            // A pathological symlink cycle must not hang the tool; give up and return
            // whatever is resolved so far rather than loop forever.
            if (++hops > 200) break;

            var segment = remaining[0];
            remaining.RemoveAt(0);

            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (resolved.Length > resolvedRoot.Length)
                {
                    var cut = resolved.LastIndexOfAny(SeparatorChars);
                    resolved = cut > resolvedRoot.Length ? resolved[..cut] : resolvedRoot;
                }
                continue;
            }

            var candidate = resolved.Length == 0 ? segment : Path.Combine(resolved, segment);
            var target = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate).LinkTarget
                : File.Exists(candidate) ? new FileInfo(candidate).LinkTarget : null;

            if (target is null)
            {
                resolved = candidate;
                continue;
            }

            if (Path.IsPathFullyQualified(target))
            {
                var targetRoot = Path.GetPathRoot(target) ?? string.Empty;
                resolved = targetRoot.Length > 0 ? targetRoot.TrimEnd(SeparatorChars) : string.Empty;
                if (resolved.Length == 0 && Path.DirectorySeparatorChar == '/') resolved = "/";
                remaining.InsertRange(0, target[targetRoot.Length..].Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries));
            }
            else
            {
                // Relative target: resolved (the symlink's own directory) stays put,
                // and the target's segments are re-walked from there.
                remaining.InsertRange(0, target.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return resolved;
    }
}
