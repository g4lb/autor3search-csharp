using Autor3Search.Core.Freezing;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for <see cref="Freezer"/> snapshot/restore and its tamper refusals.</summary>
public sealed class FreezerTests : IDisposable
{
    private readonly string _root;
    private readonly string _store;

    /// <summary>Creates isolated temp directories for the repo root and the frozen store.</summary>
    public FreezerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"a3s-freeze-{Guid.NewGuid():N}");
        _store = Path.Combine(Path.GetTempPath(), $"a3s-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_store);
    }

    /// <summary>Removes the temp directories created for the test.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_store)) Directory.Delete(_store, true);
    }

    private string Write(string relative, string content)
    {
        var abs = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, System.Text.Encoding.UTF8.GetBytes(content));
        return abs;
    }

    private string Read(string relative) =>
        System.Text.Encoding.UTF8.GetString(
            File.ReadAllBytes(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>Snapshot records every requested file with a 64-char hex SHA-256 hash.</summary>
    [Fact]
    public void SnapshotRecordsEveryFileWithItsHash()
    {
        Write("tests/A.cs", "class A {}");
        Write("tests/B.cs", "class B {}");

        var m = Freezer.Snapshot(_root, _store, ["tests/A.cs", "tests/B.cs"]);

        Assert.Equal(2, m.Files.Count);
        Assert.Contains("tests/A.cs", m.Files.Keys);
        Assert.Contains("tests/B.cs", m.Files.Keys);
        Assert.Equal(64, m.Files["tests/A.cs"].Length);   // hex sha256
    }

    /// <summary>Manifest keys are always recorded with forward slashes, regardless of platform.</summary>
    [Fact]
    public void ManifestKeysUseForwardSlashesOnEveryPlatform()
    {
        Write("tests/nested/deep/C.cs", "class C {}");
        var m = Freezer.Snapshot(_root, _store, ["tests/nested/deep/C.cs"]);
        Assert.All(m.Files.Keys, k => Assert.DoesNotContain('\\', k));
    }

    /// <summary>Restore rewrites a file that was edited after the baseline snapshot.</summary>
    [Fact]
    public void RestoreRewritesAnEditedFile()
    {
        Write("tests/A.cs", "original");
        var m = Freezer.Snapshot(_root, _store, ["tests/A.cs"]);

        Write("tests/A.cs", "the agent weakened this assertion");
        var restored = Freezer.Restore(_root, _store, m);

        Assert.Equal(["tests/A.cs"], restored);
        Assert.Equal("original", Read("tests/A.cs"));
    }

    /// <summary>Restore recreates a file that was deleted after the baseline snapshot.</summary>
    [Fact]
    public void RestoreRecreatesADeletedFile()
    {
        Write("tests/A.cs", "original");
        var m = Freezer.Snapshot(_root, _store, ["tests/A.cs"]);

        File.Delete(Path.Combine(_root, "tests", "A.cs"));
        var restored = Freezer.Restore(_root, _store, m);

        Assert.Equal(["tests/A.cs"], restored);
        Assert.Equal("original", Read("tests/A.cs"));
    }

    /// <summary>Restore reports no rewritten files when the working tree already matches the freeze.</summary>
    [Fact]
    public void RestoreReportsNothingWhenNothingChanged()
    {
        Write("tests/A.cs", "original");
        var m = Freezer.Snapshot(_root, _store, ["tests/A.cs"]);
        Assert.Empty(Freezer.Restore(_root, _store, m));
    }

    // Byte-exact I/O: with core.autocrlf=true a checkout can hold CRLF, and any
    // newline translation between hashing and restoring would make every eval
    // report a spurious restore or, worse, a hash mismatch.
    /// <summary>Snapshot and restore preserve carriage returns byte-for-byte.</summary>
    [Fact]
    public void FreezingPreservesCarriageReturnsExactly()
    {
        var crlf = "line one\r\nline two\r\n";
        Write("tests/A.cs", crlf);
        var m = Freezer.Snapshot(_root, _store, ["tests/A.cs"]);

        Write("tests/A.cs", "line one\nline two\n");   // LF-only tamper
        Freezer.Restore(_root, _store, m);

        Assert.Equal(crlf, Read("tests/A.cs"));
        Assert.Empty(Freezer.Restore(_root, _store, m));  // now byte-identical
    }

    /// <summary>Snapshot refuses a frozen path that is itself a symlink.</summary>
    [Fact]
    public void SnapshotRefusesASymlinkedFile()
    {
        if (!SymlinkSupported()) return;

        var target = Write("outside.cs", "elsewhere");
        var link = Path.Combine(_root, "tests", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);

        Assert.Throws<SymlinkRefusedException>(
            () => Freezer.Snapshot(_root, _store, ["tests/A.cs"]));
    }

    // A frozen file swapped for a symlink after baseline would have Restore write
    // THROUGH the link to a file outside the repository. It must fail loudly.
    /// <summary>Restore refuses to write through a symlink that replaced a frozen file after baseline.</summary>
    [Fact]
    public void RestoreRefusesToWriteThroughASymlinkAddedLater()
    {
        if (!SymlinkSupported()) return;

        Write("tests/A.cs", "original");
        var m = Freezer.Snapshot(_root, _store, ["tests/A.cs"]);

        var abs = Path.Combine(_root, "tests", "A.cs");
        var target = Write("outside.cs", "elsewhere");
        File.Delete(abs);
        File.CreateSymbolicLink(abs, target);

        Assert.Throws<SymlinkRefusedException>(() => Freezer.Restore(_root, _store, m));
    }

    // A frozen FILE path can be replaced with a symlink whose target is a DIRECTORY
    // (e.g. `ln -s /etc tests/A.cs`). FileInfo.Exists is false for such a path (its
    // target isn't a file), so a leaf check gated on Exists would skip it entirely and
    // let it fail later as an unrelated IOException instead of SymlinkRefusedException —
    // and the exception type is load-bearing: the pipeline maps SymlinkRefusedException
    // to a FAIL verdict with an explanatory results.tsv row, while an IOException
    // propagates as a harness malfunction that aborts the run and records nothing.
    /// <summary>Restore refuses a frozen path replaced with a symlink that resolves to a directory.</summary>
    [Fact]
    public void RestoreRefusesASymlinkResolvingToADirectory()
    {
        if (!SymlinkSupported()) return;

        Write("tests/A.cs", "original");
        var m = Freezer.Snapshot(_root, _store, ["tests/A.cs"]);

        var abs = Path.Combine(_root, "tests", "A.cs");
        var targetDir = Path.Combine(_root, "somedir");
        Directory.CreateDirectory(targetDir);
        File.Delete(abs);
        Directory.CreateSymbolicLink(abs, targetDir);

        Assert.Throws<SymlinkRefusedException>(() => Freezer.Restore(_root, _store, m));
    }

    /// <summary>Snapshot refuses a frozen path that is a symlink resolving to a directory.</summary>
    [Fact]
    public void SnapshotRefusesASymlinkResolvingToADirectory()
    {
        if (!SymlinkSupported()) return;

        var targetDir = Path.Combine(_root, "somedir");
        Directory.CreateDirectory(targetDir);
        var link = Path.Combine(_root, "tests", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, targetDir);

        Assert.Throws<SymlinkRefusedException>(
            () => Freezer.Snapshot(_root, _store, ["tests/A.cs"]));
    }

    // The frozen copy is the reference the run scores against. If it was rewritten,
    // no honest recovery exists short of a new baseline.
    /// <summary>Restore refuses a stored reference copy whose bytes no longer match its recorded hash.</summary>
    [Fact]
    public void RestoreRefusesAStoreCopyThatNoLongerMatchesItsHash()
    {
        Write("tests/A.cs", "original");
        var m = Freezer.Snapshot(_root, _store, ["tests/A.cs"]);

        var stored = Directory.GetFiles(_store, "*", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(stored, System.Text.Encoding.UTF8.GetBytes("tampered reference"));

        Assert.Throws<StoreTamperedException>(() => Freezer.Restore(_root, _store, m));
    }

    /// <summary>A manifest saved to disk and reloaded carries the same file/hash entries.</summary>
    [Fact]
    public void ManifestRoundTripsThroughDisk()
    {
        Write("tests/A.cs", "original");
        var m = Freezer.Snapshot(_root, _store, ["tests/A.cs"]);

        var path = Path.Combine(_store, Freezer.ManifestFileName);
        m.Save(path);
        var loaded = Manifest.Load(path);

        // Guards the private-field JSON trap: a manifest that round-trips to empty
        // would silently disable every freeze gate instead of failing loudly.
        Assert.NotEmpty(loaded.Files);
        Assert.Equal(m.Files.Count, loaded.Files.Count);
        Assert.Equal(m.Files["tests/A.cs"], loaded.Files["tests/A.cs"]);
    }

    // Linux is case-sensitive; macOS and Windows usually are not. A baseline holding
    // both would freeze two files on one platform and one on another, and the
    // manifest-agreement gate would then fire spuriously on the other platform.
    /// <summary>Snapshot refuses two requested paths that differ only in case.</summary>
    [Fact]
    public void SnapshotRefusesTwoPathsDifferingOnlyInCase()
    {
        Write("tests/A.cs", "one");
        var ex = Assert.Throws<InvalidOperationException>(
            () => Freezer.Snapshot(_root, _store, ["tests/A.cs", "tests/a.cs"]));
        Assert.Contains("case", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Snapshot fails when a requested file does not exist.</summary>
    [Fact]
    public void SnapshotFailsOnAMissingFile()
    {
        Assert.ThrowsAny<Exception>(() => Freezer.Snapshot(_root, _store, ["tests/Nope.cs"]));
    }

    /// <summary>HashFile is deterministic and depends only on file content.</summary>
    [Fact]
    public void HashFileIsStableAndContentDependent()
    {
        var a = Write("a.cs", "same");
        var b = Write("b.cs", "same");
        var c = Write("c.cs", "different");

        Assert.Equal(Freezer.HashFile(a), Freezer.HashFile(b));
        Assert.NotEqual(Freezer.HashFile(a), Freezer.HashFile(c));
    }

    /// <summary>
    /// Probes whether this platform/process can create symbolic links. On Windows CI,
    /// <see cref="File.CreateSymbolicLink(string, string)"/> needs Developer Mode or
    /// elevation and throws <see cref="UnauthorizedAccessException"/> otherwise; Linux
    /// and macOS runners always support it and prove the refusal behavior.
    /// </summary>
    private static bool SymlinkSupported()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"a3s-symlink-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "target.txt");
            File.WriteAllBytes(target, "x"u8.ToArray());
            var link = Path.Combine(dir, "link.txt");
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
