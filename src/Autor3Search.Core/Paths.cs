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
    /// </summary>
    public static string RepoHash(string repoRoot)
    {
        var normalized = ToSlash(Path.TrimEndingDirectorySeparator(repoRoot));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes)[..8];
    }

    /// <summary>The state directory for one repository and one run tag.</summary>
    public static string StateDir(string repoRoot, string tag) =>
        Path.Combine(StateHome(), RepoHash(repoRoot), tag);
}
