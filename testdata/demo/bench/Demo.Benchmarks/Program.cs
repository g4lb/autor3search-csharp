using BenchmarkDotNet.Running;

namespace Demo.Benchmarks;

/// <summary>Benchmark entry point. BenchmarkSwitcher is what makes --filter work.</summary>
public static class Program
{
    /// <summary>Runs the benchmarks selected by the command line.</summary>
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
