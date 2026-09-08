using Autor3Search.Core.Bench;
using Autor3Search.Core.Measuring;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>
/// Exercises <see cref="Measurer"/> end to end against two real copies of the demo
/// fixture, with real builds and real BenchmarkDotNet runs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MeasurerIntegrationTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _candDir;

    /// <summary>Copies the demo fixture twice, once per side, so each worktree can be mutated independently.</summary>
    public MeasurerIntegrationTests()
    {
        _baseDir = DemoFixture.CopyTo(Path.Combine(Path.GetTempPath(), $"a3s-base-{Guid.NewGuid():N}"));
        _candDir = DemoFixture.CopyTo(Path.Combine(Path.GetTempPath(), $"a3s-cand-{Guid.NewGuid():N}"));
    }

    /// <summary>Removes both scratch worktrees.</summary>
    public void Dispose()
    {
        foreach (var d in new[] { _baseDir, _candDir })
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch (IOException) { }
        }
    }

    private MeasureOptions Options(int rounds) => new(
        BaseDir: _baseDir,
        CandDir: _candDir,
        BenchmarkProject: "bench/Demo.Benchmarks/Demo.Benchmarks.csproj",
        Filter: "*WordCountBench*",
        Job: "dry",              // dry keeps the test minutes rather than tens of minutes
        InProcess: false,
        Rounds: rounds,
        Warmup: false,
        Timeout: TimeSpan.FromMinutes(10),
        Log: null);

    /// <summary>Two identical worktrees produce a comparable pair of sets with one value per round each.</summary>
    [Fact]
    public async Task IdenticalTreesProduceComparableSets()
    {
        var (b, c) = await Measurer.RunAsync(Options(2), CancellationToken.None);

        Assert.Contains("Demo.Benchmarks.WordCountBench.CountWords", b.Names());
        Assert.Equal(2, b.Values("Demo.Benchmarks.WordCountBench.CountWords", Units.TimeNs).Length);
        Assert.Equal(2, c.Values("Demo.Benchmarks.WordCountBench.CountWords", Units.TimeNs).Length);
    }

    /// <summary>A candidate with a known-good optimization applied measures faster than the unmodified baseline.</summary>
    [Fact]
    public async Task AFasterCandidateMeasuresFaster()
    {
        // Apply the known-good optimization to the candidate only.
        var target = Path.Combine(_candDir, "src", "Demo", "WordCount.cs");
        File.WriteAllText(target, """
            using System.Text;

            namespace Demo;

            /// <summary>Counts word occurrences.</summary>
            public static class WordCount
            {
                /// <summary>Returns how many times each lowercase word appears in <paramref name="s"/>.</summary>
                public static Dictionary<string, int> CountWords(string s)
                {
                    var counts = new Dictionary<string, int>(32);
                    var sb = new StringBuilder();

                    foreach (var field in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        sb.Clear();
                        foreach (var r in field)
                        {
                            var c = char.ToLowerInvariant(r);
                            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                        }

                        if (sb.Length > 0)
                        {
                            var word = sb.ToString();
                            counts[word] = counts.TryGetValue(word, out var n) ? n + 1 : 1;
                        }
                    }

                    return counts;
                }
            }
            """);

        var (b, c) = await Measurer.RunAsync(Options(2), CancellationToken.None);

        const string name = "Demo.Benchmarks.WordCountBench.CountWords";
        var baseMean = b.Values(name, Units.TimeNs).Average();
        var candMean = c.Values(name, Units.TimeNs).Average();

        // A dry job gives one iteration, so this asserts direction, not significance.
        Assert.True(candMean < baseMean,
            $"expected the optimized candidate to be faster: base {baseMean:F0}ns, cand {candMean:F0}ns");
    }

    /// <summary>A filter that matches no benchmark fails loudly instead of silently returning nothing.</summary>
    [Fact]
    public async Task AFilterMatchingNothingFailsLoudly()
    {
        var options = Options(2) with { Filter = "*NoSuchBenchmarkAnywhere*" };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Measurer.RunAsync(options, CancellationToken.None));
    }
}
