using System.Globalization;

namespace Autor3Search.Core.Reporting;

/// <summary>One experiment's row in the ledger.</summary>
/// <param name="Experiment">1-based experiment number.</param>
/// <param name="Timestamp">When the verdict was reached.</param>
/// <param name="Commit">The candidate commit that was evaluated.</param>
/// <param name="Status">KEEP, DISCARD, FAIL or CRASH.</param>
/// <param name="Reason">A constant from Reasons.</param>
/// <param name="Score">Geomean score, or 0 for a gate failure.</param>
/// <param name="Message">Human-readable explanation, flattened to one line.</param>
/// <param name="Version">The harness build that produced the row.</param>
public sealed record ResultsRow(
    int Experiment,
    DateTimeOffset Timestamp,
    string Commit,
    string Status,
    string Reason,
    double Score,
    string Message,
    string Version);

/// <summary>Counts and totals across a run.</summary>
/// <param name="Total">Rows recorded.</param>
/// <param name="Keeps">KEEP rows.</param>
/// <param name="Discards">DISCARD rows.</param>
/// <param name="Fails">FAIL rows.</param>
/// <param name="Crashes">CRASH rows.</param>
/// <param name="CumulativeSpeedup">Product of every kept score.</param>
/// <param name="BiggestWins">Kept rows, best score first.</param>
public sealed record ResultsSummary(
    int Total,
    int Keeps,
    int Discards,
    int Fails,
    int Crashes,
    double CumulativeSpeedup,
    IReadOnlyList<ResultsRow> BiggestWins);

/// <summary>
/// The append-only experiment ledger, written into the repository as results.tsv.
///
/// Human-readable and NOT part of the metric: the scoring inputs all live outside the
/// repository, where the agent cannot reach them. This file exists so a human waking
/// up to an overnight run can see what was tried, including what failed.
/// </summary>
public static class ResultsFile
{
    /// <summary>Ledger location relative to the repository root.</summary>
    public const string RelativePath = "results.tsv";

    /// <summary>Subprocess transcript location relative to the repository root.</summary>
    public const string RunLogName = "run.log";

    private const string Header =
        "experiment\ttimestamp\tcommit\tstatus\treason\tscore\tmessage\tversion";

    /// <summary>Appends one row, writing the header first if the file is new.</summary>
    public static void Append(string path, ResultsRow row)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

        using var writer = new StreamWriter(path, append: true);
        if (isNew) writer.WriteLine(Header);

        writer.WriteLine(string.Join('\t',
            row.Experiment.ToString(CultureInfo.InvariantCulture),
            row.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            row.Commit,
            row.Status,
            row.Reason,
            row.Score.ToString("F6", CultureInfo.InvariantCulture),
            Flatten(row.Message),
            row.Version));
    }

    /// <summary>
    /// Collapses tabs and newlines. The format is tab-separated and messages carry
    /// subprocess output, so an unflattened message would shift every later column.
    /// </summary>
    private static string Flatten(string message) =>
        message.Replace('\t', ' ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>Reads every row. A missing file is an empty ledger, not an error.</summary>
    public static IReadOnlyList<ResultsRow> Load(string path)
    {
        if (!File.Exists(path)) return [];

        var rows = new List<ResultsRow>();

        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var f = line.Split('\t');
            if (f.Length < 8) continue;   // a truncated final row from an interrupted write

            rows.Add(new ResultsRow(
                int.Parse(f[0], CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(f[1], CultureInfo.InvariantCulture),
                f[2], f[3], f[4],
                double.Parse(f[5], CultureInfo.InvariantCulture),
                f[6], f[7]));
        }

        return rows;
    }

    /// <summary>The number the next experiment should carry.</summary>
    public static int NextExperimentNumber(string path)
    {
        var rows = Load(path);
        return rows.Count == 0 ? 1 : rows.Max(r => r.Experiment) + 1;
    }

    /// <summary>
    /// Counts by status, the cumulative speedup, and the largest wins.
    ///
    /// The cumulative figure is the PRODUCT of every kept score, not the latest one.
    /// The measurement baseline advances to the kept commit after every KEEP, so each
    /// kept score is that experiment's own incremental contribution — successive real
    /// improvements compound the way percentage changes do.
    /// </summary>
    public static ResultsSummary Summarize(IReadOnlyList<ResultsRow> rows)
    {
        var keeps = rows.Where(r => r.Status == "KEEP").ToList();

        var cumulative = 1.0;
        foreach (var k in keeps)
        {
            if (k.Score > 0) cumulative *= k.Score;
        }

        return new ResultsSummary(
            Total: rows.Count,
            Keeps: keeps.Count,
            Discards: rows.Count(r => r.Status == "DISCARD"),
            Fails: rows.Count(r => r.Status == "FAIL"),
            Crashes: rows.Count(r => r.Status == "CRASH"),
            CumulativeSpeedup: cumulative,
            BiggestWins: keeps.OrderBy(k => k.Score).ToArray());
    }
}
