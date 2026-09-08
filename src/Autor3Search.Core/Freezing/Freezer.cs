using System.Security.Cryptography;

namespace Autor3Search.Core.Freezing;

/// <summary>A frozen path is, or has become, a symlink. Tampering, not a malfunction.</summary>
public sealed class SymlinkRefusedException(string message) : Exception(message);

/// <summary>A frozen reference copy no longer matches the hash recorded for it.</summary>
public sealed class StoreTamperedException(string message) : Exception(message);

/// <summary>
/// Snapshots and restores the files a run freezes.
///
/// All I/O here is byte-exact: <see cref="File.ReadAllBytes(string)"/> and
/// <see cref="File.WriteAllBytes(string, byte[])"/>, never the text overloads. With
/// core.autocrlf=true a Windows checkout holds CRLF, and any newline translation
/// between hashing and restoring would desynchronize the two.
/// </summary>
public static class Freezer
{
    /// <summary>Manifest filename inside the state directory.</summary>
    public const string ManifestFileName = "frozen-manifest.json";

    /// <summary>Directory name holding the frozen reference copies.</summary>
    public const string StoreDirName = "frozen";

    /// <summary>Lowercase hex SHA-256 of a file's bytes.</summary>
    public static string HashFile(string absolutePath) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(absolutePath)));

    /// <summary>
    /// Copies each file into the store and records its hash.
    /// </summary>
    /// <exception cref="SymlinkRefusedException">A path is a symlink, or lies under one.</exception>
    /// <exception cref="InvalidOperationException">Two paths differ only in case.</exception>
    public static Manifest Snapshot(string repoRoot, string storeDir, IEnumerable<string> relativePaths)
    {
        var paths = relativePaths.Select(Paths.ToSlash).Distinct(StringComparer.Ordinal).ToList();
        RefuseCaseCollisions(paths);

        var entries = new List<ManifestEntry>();

        foreach (var rel in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            var abs = Path.Combine(repoRoot, Paths.FromSlash(rel));
            RefuseLinkOnPath(repoRoot, abs, rel);

            var hash = HashFile(abs);
            var stored = Path.Combine(storeDir, Paths.FromSlash(rel));
            Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
            File.WriteAllBytes(stored, File.ReadAllBytes(abs));

            entries.Add(new ManifestEntry(rel, hash));
        }

        return Manifest.FromEntries(entries);
    }

    /// <summary>
    /// Rewrites every frozen file whose working-tree bytes differ from the frozen copy.
    /// Returns the paths actually rewritten, in sorted order.
    /// </summary>
    /// <exception cref="SymlinkRefusedException">A frozen path became a symlink after baseline.</exception>
    /// <exception cref="StoreTamperedException">A frozen reference copy was modified.</exception>
    public static IReadOnlyList<string> Restore(string repoRoot, string storeDir, Manifest manifest)
    {
        var restored = new List<string>();

        foreach (var (rel, expectedHash) in manifest.Files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var stored = Path.Combine(storeDir, Paths.FromSlash(rel));

            if (!File.Exists(stored))
            {
                throw new StoreTamperedException(
                    $"the frozen copy of {rel} is missing from {storeDir}. The frozen " +
                    "reference this run scores against can no longer be trusted — start a " +
                    "fresh baseline with `autor3search-csharp baseline`.");
            }

            // Verify the reference BEFORE trusting it. If the store was rewritten, the
            // tests this run scores against can no longer be trusted, and unlike a
            // symlink swap it cannot be undone by fixing the working tree — the
            // reference itself is what was lost.
            var storedBytes = File.ReadAllBytes(stored);
            var storedHash = Convert.ToHexStringLower(SHA256.HashData(storedBytes));
            if (!string.Equals(storedHash, expectedHash, StringComparison.Ordinal))
            {
                throw new StoreTamperedException(
                    $"the frozen copy of {rel} no longer matches its recorded hash. The " +
                    "frozen reference this run scores against can no longer be trusted — " +
                    "start a fresh baseline with `autor3search-csharp baseline`.");
            }

            var abs = Path.Combine(repoRoot, Paths.FromSlash(rel));

            if (File.Exists(abs) || Directory.Exists(abs))
            {
                RefuseLinkOnPath(repoRoot, abs, rel);
                if (File.ReadAllBytes(abs).AsSpan().SequenceEqual(storedBytes)) continue;
            }
            else
            {
                RefuseLinkOnDirectories(repoRoot, abs, rel);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllBytes(abs, storedBytes);
            restored.Add(rel);
        }

        return restored;
    }

    /// <summary>
    /// Refuses when the file itself, or any directory on the way to it, is a link.
    ///
    /// Both halves matter. A symlinked FILE would have Restore write through it to a
    /// path outside the repository. A symlinked DIRECTORY does the same thing one
    /// level up and is easier to miss.
    ///
    /// The leaf check reads <see cref="FileSystemInfo.LinkTarget"/> directly rather than
    /// gating on <see cref="FileSystemInfo.Exists"/>: Exists follows the link to its
    /// target and, for a FileInfo, is false when that target is a directory (e.g. a
    /// frozen path replaced with `ln -s /etc tests/A.cs`) — gating on it would let a
    /// directory-resolving symlink slip past as "not a link" and fail later with an
    /// unrelated IOException instead of SymlinkRefusedException. LinkTarget itself is
    /// populated from an lstat of the path itself, so it reports the link regardless of
    /// what — or whether anything — it resolves to, and is null for an ordinary missing
    /// path (nothing to lstat).
    /// </summary>
    private static void RefuseLinkOnPath(string repoRoot, string absolutePath, string rel)
    {
        var linkTarget = new FileInfo(absolutePath).LinkTarget;
        if (linkTarget is not null)
        {
            throw new SymlinkRefusedException(
                $"{rel} is a symbolic link (to {linkTarget}) — frozen files must be " +
                "regular files, so that restoring them cannot write outside the repository");
        }

        RefuseLinkOnDirectories(repoRoot, absolutePath, rel);
    }

    private static void RefuseLinkOnDirectories(string repoRoot, string absolutePath, string rel)
    {
        var root = Path.GetFullPath(repoRoot);
        var dir = Path.GetDirectoryName(Path.GetFullPath(absolutePath));

        while (dir is not null && dir.Length > root.Length)
        {
            var di = new DirectoryInfo(dir);
            if (di.Exists && di.LinkTarget is not null)
            {
                throw new SymlinkRefusedException(
                    $"the directory {Paths.ToSlash(dir)} on the path to {rel} is a link " +
                    $"(to {di.LinkTarget}) — every directory on the way to a frozen file " +
                    "must be a real directory");
            }
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static void RefuseCaseCollisions(List<string> paths)
    {
        var byLower = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            var key = p.ToLowerInvariant();
            if (byLower.TryGetValue(key, out var other))
            {
                throw new InvalidOperationException(
                    $"\"{other}\" and \"{p}\" differ only in case. Linux would freeze two " +
                    "files where macOS and Windows would see one, so a baseline taken on " +
                    "one platform could not be restored on another. Rename one of them.");
            }
            byLower[key] = p;
        }
    }
}
