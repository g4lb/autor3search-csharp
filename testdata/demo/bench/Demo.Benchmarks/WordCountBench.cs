using BenchmarkDotNet.Attributes;
using Demo;

namespace Demo.Benchmarks;

/// <summary>Benchmarks the word counter over a realistic input.</summary>
[MemoryDiagnoser]
public class WordCountBench
{
    private static readonly string Input =
        string.Concat(Enumerable.Repeat("The Quick, Brown Fox! jumps over 2 lazy dogs. ", 200));

    /// <summary>Counts words over the shared input.</summary>
    [Benchmark]
    public int CountWords()
    {
        // Consume the result so nothing can be optimized away.
        var counts = WordCount.CountWords(Input);
        if (counts.Count == 0) throw new InvalidOperationException("empty result");
        return counts.Count;
    }
}
