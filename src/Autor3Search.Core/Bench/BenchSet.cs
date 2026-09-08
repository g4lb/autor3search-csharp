namespace Autor3Search.Core.Bench;

/// <summary>
/// Observations accumulated across measured rounds for one side of a comparison.
///
/// One round contributes one value per benchmark per unit. The cross-round comparison
/// that later tasks build on top of this is where the interleaved A/B design earns its
/// keep. See <see cref="Stats"/> for comparing two <see cref="BenchSet"/> instances.
/// </summary>
public sealed class BenchSet
{
    private readonly Dictionary<string, Dictionary<string, List<double>>> _series = new(StringComparer.Ordinal);

    /// <summary>Adds one round's observations.</summary>
    public void Add(IEnumerable<Observation> observations)
    {
        foreach (var o in observations)
        {
            if (!_series.TryGetValue(o.FullName, out var units))
            {
                units = new Dictionary<string, List<double>>(StringComparer.Ordinal);
                _series[o.FullName] = units;
            }

            Append(units, Units.TimeNs, o.MeanNs);
            Append(units, Units.BytesPerOp, o.BytesPerOp);
        }
    }

    private static void Append(Dictionary<string, List<double>> units, string unit, double value)
    {
        if (!units.TryGetValue(unit, out var list))
        {
            list = [];
            units[unit] = list;
        }
        list.Add(value);
    }

    /// <summary>Benchmark names, ordered for deterministic output.</summary>
    public IReadOnlyList<string> Names() =>
        _series.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>Reports whether this set carries the named benchmark and unit.</summary>
    public bool Has(string name, string unit) =>
        _series.TryGetValue(name, out var units) && units.ContainsKey(unit);

    /// <summary>The observed values in round order. Empty when absent.</summary>
    public double[] Values(string name, string unit) =>
        _series.TryGetValue(name, out var units) && units.TryGetValue(unit, out var list)
            ? list.ToArray()
            : [];
}
