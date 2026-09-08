using System.Text.Json;

namespace Autor3Search.Core.Bench;

/// <summary>
/// Reads BenchmarkDotNet's JSON report.
///
/// The harness takes ONE value per benchmark per round — Statistics.Mean — and runs
/// its own statistics across rounds. BenchmarkDotNet's within-round summary is a
/// pre-filter beneath that, not a substitute for it.
/// </summary>
public static class BdnReport
{
    /// <summary>Parses one report file's contents.</summary>
    /// <exception cref="InvalidOperationException">The report has no benchmarks, or one has no Mean.</exception>
    public static IReadOnlyList<Observation> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("Benchmarks", out var benchmarks)
            || benchmarks.ValueKind != JsonValueKind.Array
            || benchmarks.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                "BenchmarkDotNet report contains no benchmarks — the filter matched nothing");
        }

        var result = new List<Observation>();
        foreach (var b in benchmarks.EnumerateArray())
        {
            var fullName = b.TryGetProperty("FullName", out var fn) ? fn.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(fullName))
                throw new InvalidOperationException("BenchmarkDotNet report contains a benchmark with no FullName");

            if (!b.TryGetProperty("Statistics", out var stats)
                || stats.ValueKind != JsonValueKind.Object
                || !stats.TryGetProperty("Mean", out var mean)
                || mean.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidOperationException(
                    $"benchmark {fullName} has no Statistics.Mean — it cannot be scored, and " +
                    "treating a missing mean as zero would make it look infinitely fast");
            }

            var meanNs = mean.GetDouble();
            if (!double.IsFinite(meanNs))
            {
                throw new InvalidOperationException(
                    $"benchmark {fullName} has no Statistics.Mean — it cannot be scored, and " +
                    $"a non-finite mean ({meanNs}) is exactly as unusable as a missing one");
            }

            long bytes = 0;
            if (b.TryGetProperty("Memory", out var mem)
                && mem.ValueKind == JsonValueKind.Object
                && mem.TryGetProperty("BytesAllocatedPerOperation", out var bpo)
                && bpo.ValueKind == JsonValueKind.Number)
            {
                bytes = bpo.GetInt64();
            }

            result.Add(new Observation(fullName, meanNs, bytes));
        }

        return result;
    }

    /// <summary>
    /// Parses every report under an artifacts directory and merges them.
    ///
    /// BenchmarkDotNet writes one report per benchmark TYPE, so a filter matching two
    /// classes produces two files. Reading only the first would silently shrink the
    /// benchmark set for that round.
    /// </summary>
    public static IReadOnlyList<Observation> ParseDirectory(string artifactsDir)
    {
        var files = Directory.Exists(artifactsDir)
            ? Directory.GetFiles(artifactsDir, "*-report-full-compressed.json", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray()
            : [];

        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"no BenchmarkDotNet JSON report was written under {artifactsDir} — " +
                "the benchmark process produced no results");
        }

        var merged = new List<Observation>();
        foreach (var f in files) merged.AddRange(Parse(File.ReadAllText(f)));
        return merged;
    }
}
