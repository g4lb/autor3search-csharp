using Autor3Search.Core.Doctoring;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for <see cref="Doctor"/>.</summary>
public class DoctorTests
{
    /// <summary>Every platform produces at least one check.</summary>
    [Fact]
    public async Task DoctorReturnsChecksOnEveryPlatform()
    {
        var checks = await Doctor.RunAsync(Directory.GetCurrentDirectory(), CancellationToken.None);
        Assert.NotEmpty(checks);
    }

    /// <summary>The checks with no platform dependency always run.</summary>
    [Fact]
    public async Task TheAlwaysAvailableChecksRunEverywhere()
    {
        var checks = await Doctor.RunAsync(Directory.GetCurrentDirectory(), CancellationToken.None);

        foreach (var name in new[] { "disk space", "cpu count", "operating system", ".NET SDK" })
        {
            var check = Assert.Single(checks, c => c.Name == name);
            Assert.NotEqual(CheckStatus.Unavailable, check.Status);
        }
    }

    // Silence would read as a pass. A check this platform cannot make must SAY it
    // cannot, with the reason, or a user believes they were told something they were not.
    /// <summary>Every unavailable check carries a non-empty reason.</summary>
    [Fact]
    public async Task UnavailableChecksExplainThemselves()
    {
        var checks = await Doctor.RunAsync(Directory.GetCurrentDirectory(), CancellationToken.None);

        foreach (var c in checks.Where(c => c.Status == CheckStatus.Unavailable))
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Detail));
        }
    }

    /// <summary>Platform-specific checks are always present, even when unavailable.</summary>
    [Fact]
    public async Task EveryPlatformSpecificCheckIsPresentEvenWhenUnavailable()
    {
        var checks = await Doctor.RunAsync(Directory.GetCurrentDirectory(), CancellationToken.None);

        foreach (var name in new[] { "load average", "cpu frequency scaling", "power source" })
        {
            Assert.Contains(checks, c => c.Name == name);
        }
    }

    /// <summary>Load average is unavailable only on Windows.</summary>
    [Fact]
    public async Task LoadAverageIsUnavailableOnWindowsAndAvailableElsewhere()
    {
        var checks = await Doctor.RunAsync(Directory.GetCurrentDirectory(), CancellationToken.None);
        var loadAverage = Assert.Single(checks, c => c.Name == "load average");

        if (OperatingSystem.IsWindows())
            Assert.Equal(CheckStatus.Unavailable, loadAverage.Status);
        else
            Assert.NotEqual(CheckStatus.Unavailable, loadAverage.Status);
    }

    /// <summary>Every check carries both a name and a detail.</summary>
    [Fact]
    public async Task EveryCheckHasANameAndDetail()
    {
        var checks = await Doctor.RunAsync(Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.All(checks, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.Detail));
        });
    }
}
