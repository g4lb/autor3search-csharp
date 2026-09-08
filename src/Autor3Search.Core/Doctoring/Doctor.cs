using System.Globalization;
using System.Runtime.InteropServices;
using Autor3Search.Core.Processes;

namespace Autor3Search.Core.Doctoring;

/// <summary>How a measurement-hygiene check came out.</summary>
public enum CheckStatus
{
    /// <summary>Nothing to worry about.</summary>
    Ok,

    /// <summary>Something that will make numbers noisier.</summary>
    Warn,

    /// <summary>This platform cannot make the check.</summary>
    Unavailable,
}

/// <summary>One measurement-hygiene check.</summary>
/// <param name="Name">Short check name.</param>
/// <param name="Status">Outcome.</param>
/// <param name="Detail">What was found, or why it could not be.</param>
public readonly record struct Check(string Name, CheckStatus Status, string Detail);

/// <summary>
/// Checks whether this machine can measure reliably.
///
/// Informational only — it never blocks a run and always exits 0. A check this platform
/// cannot make is reported as Unavailable WITH A REASON rather than omitted: silence
/// would read as a pass, and a user would believe they had been told something they
/// had not.
/// </summary>
public static class Doctor
{
    /// <summary>Runs every check.</summary>
    public static async Task<IReadOnlyList<Check>> RunAsync(string repo, CancellationToken ct)
    {
        return
        [
            DiskSpace(repo),
            CpuCount(),
            OperatingSystemCheck(),
            await DotnetSdkAsync(repo, ct).ConfigureAwait(false),
            await LoadAverageAsync(ct).ConfigureAwait(false),
            await CpuScalingAsync(ct).ConfigureAwait(false),
            await PowerSourceAsync(ct).ConfigureAwait(false),
        ];
    }

    private static Check DiskSpace(string repo)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(repo));
            var free = new DriveInfo(root!).AvailableFreeSpace / (1024.0 * 1024 * 1024);

            return free < 5
                ? new Check("disk space", CheckStatus.Warn,
                    $"{free:F1} GiB free — builds and BenchmarkDotNet artifacts need room, and a " +
                    "full disk fails a run mid-experiment")
                : new Check("disk space", CheckStatus.Ok, $"{free:F1} GiB free");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new Check("disk space", CheckStatus.Unavailable, $"could not read: {ex.Message}");
        }
    }

    private static Check CpuCount()
    {
        var n = Environment.ProcessorCount;

        return n < 4
            ? new Check("cpu count", CheckStatus.Warn,
                $"{n} logical processors — a benchmark competing with the build and the test " +
                "runner for this few cores produces noisy numbers")
            : new Check("cpu count", CheckStatus.Ok, $"{n} logical processors");
    }

    private static Check OperatingSystemCheck() =>
        new("operating system", CheckStatus.Ok,
            $"{RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}");

    private static async Task<Check> DotnetSdkAsync(string repo, CancellationToken ct)
    {
        try
        {
            var runner = new Runner(repo, TimeSpan.FromSeconds(60), null);
            var r = await runner.RunAsync("dotnet", ["--version"], ct).ConfigureAwait(false);

            return r.OK
                ? new Check(".NET SDK", CheckStatus.Ok, r.Stdout.Trim())
                : new Check(".NET SDK", CheckStatus.Warn,
                    "`dotnet --version` failed — the harness builds and benchmarks through the SDK");
        }
        catch (Exception ex)
        {
            return new Check(".NET SDK", CheckStatus.Warn, $"could not run dotnet: {ex.Message}");
        }
    }

    private static async Task<Check> LoadAverageAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return new Check("load average", CheckStatus.Unavailable,
                "Windows exposes no load-average equivalent, so this run is not checked for " +
                "competing work. Close what you can before measuring.");
        }

        try
        {
            double one;

            if (OperatingSystem.IsLinux())
            {
                var text = await File.ReadAllTextAsync("/proc/loadavg", ct).ConfigureAwait(false);
                one = double.Parse(text.Split(' ')[0], CultureInfo.InvariantCulture);
            }
            else
            {
                var runner = new Runner(Path.GetTempPath(), TimeSpan.FromSeconds(15), null);
                var r = await runner.RunAsync("sysctl", ["-n", "vm.loadavg"], ct).ConfigureAwait(false);
                if (!r.OK) return new Check("load average", CheckStatus.Unavailable, "sysctl vm.loadavg failed");

                // "{ 1.83 2.05 2.11 }"
                one = double.Parse(r.Stdout.Trim().Trim('{', '}').Trim().Split(' ')[0],
                    CultureInfo.InvariantCulture);
            }

            var perCore = one / Environment.ProcessorCount;

            return perCore > 0.5
                ? new Check("load average", CheckStatus.Warn,
                    $"{one:F2} ({perCore:P0} per core) — this machine is busy, and a benchmark " +
                    "sharing it with other work measures the other work too")
                : new Check("load average", CheckStatus.Ok, $"{one:F2}");
        }
        catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException)
        {
            return new Check("load average", CheckStatus.Unavailable, $"could not read: {ex.Message}");
        }
    }

    private static async Task<Check> CpuScalingAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            const string path = "/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor";
            if (!File.Exists(path))
            {
                return new Check("cpu frequency scaling", CheckStatus.Unavailable,
                    "no cpufreq governor exposed — common in containers and VMs, where the host " +
                    "controls frequency and you cannot see or influence it");
            }

            var governor = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();

            return governor == "performance"
                ? new Check("cpu frequency scaling", CheckStatus.Ok, $"governor: {governor}")
                : new Check("cpu frequency scaling", CheckStatus.Warn,
                    $"governor: {governor} — the CPU changes speed during a run, which lands in " +
                    "your measurements as though it were your code. Consider: " +
                    "sudo cpupower frequency-set -g performance");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new Check("cpu frequency scaling", CheckStatus.Unavailable,
                "macOS exposes no governor to read, and Apple Silicon schedules across P and E " +
                "cores at its own discretion — the single largest source of measurement noise on " +
                "this platform. Interleaving mitigates it; nothing eliminates it.");
        }

        _ = ct;
        return new Check("cpu frequency scaling", CheckStatus.Unavailable,
            "not checked on this platform");
    }

    private static async Task<Check> PowerSourceAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var runner = new Runner(Path.GetTempPath(), TimeSpan.FromSeconds(15), null);
                var r = await runner.RunAsync("pmset", ["-g", "batt"], ct).ConfigureAwait(false);

                if (!r.OK) return new Check("power source", CheckStatus.Unavailable, "pmset failed");

                return r.Stdout.Contains("AC Power", StringComparison.Ordinal)
                    ? new Check("power source", CheckStatus.Ok, "on AC power")
                    : new Check("power source", CheckStatus.Warn,
                        "on battery — macOS throttles aggressively on battery, and a run that " +
                        "starts plugged in and ends unplugged attributes the difference to your code");
            }

            if (OperatingSystem.IsLinux())
            {
                const string path = "/sys/class/power_supply/AC/online";
                if (!File.Exists(path))
                    return new Check("power source", CheckStatus.Unavailable, "no AC adapter exposed");

                var online = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();

                return online == "1"
                    ? new Check("power source", CheckStatus.Ok, "on AC power")
                    : new Check("power source", CheckStatus.Warn,
                        "on battery — expect throttling and noisier numbers");
            }

            return new Check("power source", CheckStatus.Unavailable,
                "not checked on this platform — if this is a laptop, plug it in before measuring");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return new Check("power source", CheckStatus.Unavailable, $"could not read: {ex.Message}");
        }
    }
}
