namespace Autor3Search.Core.Bench;

/// <summary>One benchmark's result from one measured round.</summary>
/// <param name="FullName">BenchmarkDotNet's FullName, including parameters. The benchmark identity.</param>
/// <param name="MeanNs">Arithmetic mean per operation, in nanoseconds. The scored metric.</param>
/// <param name="BytesPerOp">Allocated bytes per operation. A hint, never scored.</param>
public readonly record struct Observation(string FullName, double MeanNs, long BytesPerOp);

/// <summary>Metric unit names used as dictionary keys throughout comparison.</summary>
public static class Units
{
    /// <summary>Nanoseconds per operation. The only unit that is scored.</summary>
    public const string TimeNs = "time_ns";

    /// <summary>Allocated bytes per operation. Reported as a hint only.</summary>
    public const string BytesPerOp = "alloc_bytes";
}
