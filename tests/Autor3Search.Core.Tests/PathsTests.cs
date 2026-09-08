using Autor3Search.Core;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for path normalization and state home resolution.</summary>
public class PathsTests
{
    /// <summary>Verifies that ToSlash converts backslashes to forward slashes.</summary>
    [Fact]
    public void ToSlashNormalizesBackslashes()
    {
        Assert.Equal("src/Foo/Bar.cs", Paths.ToSlash(@"src\Foo\Bar.cs"));
        Assert.Equal("src/Foo/Bar.cs", Paths.ToSlash("src/Foo/Bar.cs"));
    }

    /// <summary>Verifies that FromSlash round-trips with ToSlash on this platform.</summary>
    [Fact]
    public void FromSlashRoundTripsOnThisPlatform()
    {
        var stored = "src/Foo/Bar.cs";
        Assert.Equal(stored, Paths.ToSlash(Paths.FromSlash(stored)));
    }

    /// <summary>Verifies that an absolute override path is honored by StateHome.</summary>
    [Fact]
    public void StateHomeHonoursAbsoluteOverride()
    {
        var abs = Path.Combine(Path.GetTempPath(), "a3s-state-home");
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, abs);
        try
        {
            Assert.Equal(abs, Paths.StateHome());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, null);
        }
    }

    /// <summary>Verifies that StateHome rejects relative path overrides.</summary>
    [Fact]
    public void StateHomeRefusesRelativeOverride()
    {
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, "relative/dir");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Paths.StateHome());
            Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, null);
        }
    }

    /// <summary>
    /// Verifies that StateHome rejects drive-relative paths on Windows.
    /// On Windows "\foo" satisfies IsPathRooted but is drive-relative: it resolves
    /// against whatever drive the process is on, so eval from D: and stop from C:
    /// would address different state for the same run. IsPathFullyQualified is the
    /// correct guard and this test pins that choice.
    /// </summary>
    [Fact]
    public void StateHomeRefusesDriveRelativeOverride()
    {
        if (!OperatingSystem.IsWindows()) return;

        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, @"\drive-relative");
        try
        {
            Assert.Throws<InvalidOperationException>(() => Paths.StateHome());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, null);
        }
    }

    /// <summary>Verifies that StateHome defaults to the platform cache directory.</summary>
    [Fact]
    public void StateHomeDefaultIsPlatformCacheDirectory()
    {
        Environment.SetEnvironmentVariable(Paths.StateHomeEnvVar, null);
        var home = Paths.StateHome();

        Assert.True(Path.IsPathFullyQualified(home));
        Assert.EndsWith(Paths.AppName, home);

        if (OperatingSystem.IsMacOS())
        {
            // Deliberately NOT SpecialFolder.LocalApplicationData, which is
            // ~/Library/Application Support on macOS. Go's os.UserCacheDir is
            // ~/Library/Caches and the two tools must agree.
            Assert.Contains(Path.Combine("Library", "Caches"), home);
        }
    }

    /// <summary>Verifies that RepoHash is stable and short (8 characters).</summary>
    [Fact]
    public void RepoHashIsStableAndShort()
    {
        var a = Paths.RepoHash("/home/u/proj");
        var b = Paths.RepoHash("/home/u/proj");
        var c = Paths.RepoHash("/home/u/other");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(8, a.Length);
    }

    /// <summary>Verifies that StateDir combines state home, repo hash, and tag.</summary>
    [Fact]
    public void StateDirCombinesHomeHashAndTag()
    {
        var dir = Paths.StateDir("/home/u/proj", "sep8");
        Assert.EndsWith(Path.Combine(Paths.RepoHash("/home/u/proj"), "sep8"), dir);
    }
}
