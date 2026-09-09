using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Autor3Search.Core.Configuration;

/// <summary>
/// The run configuration: what is measured, what may be edited, and how strictly.
///
/// This file lives inside the repository because humans own it, which means the
/// agent can reach it. It is hashed at <c>baseline</c> and any mid-run change fails
/// the run — raising max_regress_pct or dropping a benchmark would otherwise defeat
/// the very guards it configures.
/// </summary>
public sealed class RunConfig
{
    /// <summary>Config location relative to the repository root.</summary>
    public const string RelativePath = ".autor3search/config.yaml";

    /// <summary>
    /// Smallest Count at which the exact Mann-Whitney test can ever report p &lt; 0.05.
    /// At 2 and 3 observations per side the best achievable two-sided p-values are
    /// 0.3333 and 0.1 — both above the default alpha — so every experiment would be
    /// discarded regardless of what changed.
    /// </summary>
    public const int MinCount = 4;

    private static readonly string[] ValidJobs = ["dry", "short", "medium", "long", "default"];

    /// <summary>Declared benchmark set, by BenchmarkDotNet FullName. Empty means all discovered.</summary>
    [YamlMember(Alias = "benchmarks")]
    public List<string> Benchmarks { get; set; } = [];

    /// <summary>Repo-relative path to the project declaring the benchmarks.</summary>
    [YamlMember(Alias = "benchmark_project")]
    public string BenchmarkProject { get; set; } = "";

    /// <summary>Repo-relative paths to the test projects frozen for this run.</summary>
    [YamlMember(Alias = "test_projects")]
    public List<string> TestProjects { get; set; } = [];

    /// <summary>Glob patterns the agent may modify.</summary>
    [YamlMember(Alias = "scope")]
    public List<string> Scope { get; set; } = [];

    /// <summary>Measured rounds per side.</summary>
    [YamlMember(Alias = "count")]
    public int Count { get; set; }

    /// <summary>BenchmarkDotNet job: dry, short, medium, long or default.</summary>
    [YamlMember(Alias = "job")]
    public string Job { get; set; } = "short";

    /// <summary>Adds one leading round that is measured and discarded.</summary>
    [YamlMember(Alias = "warmup")]
    public bool Warmup { get; set; }

    /// <summary>
    /// Runs benchmarks in the host process, skipping BenchmarkDotNet's per-run project
    /// generation. Faster, less isolated. A config key rather than a flag on purpose:
    /// it changes what the numbers mean, so it belongs in the file that is frozen for
    /// the run, not in an argument the agent could pass on its own.
    /// </summary>
    [YamlMember(Alias = "in_process")]
    public bool InProcess { get; set; }

    /// <summary>Largest tolerated significant regression, as a percentage.</summary>
    [YamlMember(Alias = "max_regress_pct")]
    public double MaxRegressPct { get; set; }

    /// <summary>Smallest geomean improvement a KEEP will accept, as a percentage.</summary>
    [YamlMember(Alias = "min_effect_pct")]
    public double MinEffectPct { get; set; }

    /// <summary>Bounds each subprocess phase. A unit-suffixed duration, e.g. "15m".</summary>
    [YamlMember(Alias = "timeout")]
    public string Timeout { get; set; } = "15m";

    /// <summary>
    /// Builds the optimized repository with warnings as errors. This is the only static
    /// analysis the pipeline can offer -- .NET analyzers run inside the build, so there is
    /// no separate analysis stage to gate on. Off by default because arbitrary
    /// repositories carry pre-existing warnings that have nothing to do with the agent's
    /// change, and a gate that fires on those is noise rather than a gate.
    /// </summary>
    [YamlMember(Alias = "warnings_as_errors")]
    public bool WarningsAsErrors { get; set; }

    /// <summary>Files deliberately exempted from freezing.</summary>
    [YamlMember(Alias = "unfreeze")]
    public List<string> Unfreeze { get; set; } = [];

    /// <summary>The configuration used when a field is omitted.</summary>
    public static RunConfig Default() => new()
    {
        Benchmarks = [],
        BenchmarkProject = "",
        TestProjects = [],
        Scope = ["src/**"],
        Count = 10,
        Job = "short",
        Warmup = true,
        InProcess = false,
        MaxRegressPct = 5.0,
        MinEffectPct = 1.0,
        Timeout = "15m",
        WarningsAsErrors = false,
        Unfreeze = [],
    };

    /// <summary>
    /// Reads a config file, applying defaults for omitted fields, then validates.
    /// Every failure mode — a missing or unreadable file, syntactically invalid YAML,
    /// or a value that fails <see cref="Validate"/> — surfaces as an
    /// <see cref="InvalidOperationException"/> naming <paramref name="absolutePath"/>.
    /// </summary>
    public static RunConfig Load(string absolutePath)
    {
        string yaml;
        try
        {
            yaml = File.ReadAllText(absolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"could not read {absolutePath}: {ex.Message}", ex);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // Deserializing onto a fresh object then merging keeps "omitted" distinct from
        // "explicitly set to a zero value" for the value-typed fields.
        RunConfig parsed;
        try
        {
            parsed = deserializer.Deserialize<RunConfig?>(yaml) ?? new RunConfig();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidOperationException($"{absolutePath} is not valid YAML: {ex.Message}", ex);
        }

        var merged = Merge(Default(), parsed, yaml);

        try
        {
            merged.Validate();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"invalid {absolutePath}: {ex.Message}", ex);
        }

        return merged;
    }

    private static RunConfig Merge(RunConfig defaults, RunConfig parsed, string yaml)
    {
        bool Present(string key) =>
            Regex.IsMatch(yaml, $@"^\s*{Regex.Escape(key)}\s*:", RegexOptions.Multiline);

        return new RunConfig
        {
            Benchmarks = parsed.Benchmarks.Count > 0 ? parsed.Benchmarks : defaults.Benchmarks,
            BenchmarkProject = string.IsNullOrWhiteSpace(parsed.BenchmarkProject)
                ? defaults.BenchmarkProject : parsed.BenchmarkProject,
            TestProjects = parsed.TestProjects.Count > 0 ? parsed.TestProjects : defaults.TestProjects,
            Scope = parsed.Scope.Count > 0 ? parsed.Scope : defaults.Scope,
            Count = Present("count") ? parsed.Count : defaults.Count,
            Job = string.IsNullOrWhiteSpace(parsed.Job) ? defaults.Job : parsed.Job,
            Warmup = Present("warmup") ? parsed.Warmup : defaults.Warmup,
            InProcess = Present("in_process") ? parsed.InProcess : defaults.InProcess,
            MaxRegressPct = Present("max_regress_pct") ? parsed.MaxRegressPct : defaults.MaxRegressPct,
            MinEffectPct = Present("min_effect_pct") ? parsed.MinEffectPct : defaults.MinEffectPct,
            Timeout = string.IsNullOrWhiteSpace(parsed.Timeout) ? defaults.Timeout : parsed.Timeout,
            WarningsAsErrors = Present("warnings_as_errors") ? parsed.WarningsAsErrors : defaults.WarningsAsErrors,
            Unfreeze = parsed.Unfreeze.Count > 0 ? parsed.Unfreeze : defaults.Unfreeze,
        };
    }

    /// <summary>Throws when the configuration is unusable, explaining why rather than failing bare.</summary>
    public void Validate()
    {
        if (Count < MinCount)
        {
            throw new InvalidOperationException(
                $"count must be at least {MinCount}: the significance test cannot report p < 0.05 " +
                $"with fewer than {MinCount} measured rounds per side no matter how large the " +
                "improvement is, so every experiment would be discarded regardless of what " +
                "changed (the default is 10)");
        }

        if (MaxRegressPct < 0)
            throw new InvalidOperationException("max_regress_pct must not be negative");

        if (MinEffectPct < 0 || MinEffectPct >= 100)
            throw new InvalidOperationException("min_effect_pct must be at least 0 and less than 100");

        if (Scope.Count == 0)
            throw new InvalidOperationException("scope must list at least one path pattern");

        if (Scope.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("scope must not contain an empty or whitespace-only entry");

        if (!ValidJobs.Contains(Job.ToLowerInvariant()))
        {
            throw new InvalidOperationException(
                $"job \"{Job}\" is not a BenchmarkDotNet job. Valid values are: " +
                string.Join(", ", ValidJobs));
        }

        if (string.IsNullOrWhiteSpace(BenchmarkProject))
        {
            throw new InvalidOperationException(
                "benchmark_project must name the project that declares the benchmarks — " +
                "this tool has no other notion of \"faster\"");
        }

        _ = TimeoutDuration;
    }

    /// <summary>Parses <see cref="Timeout"/> as a unit-suffixed duration.</summary>
    public TimeSpan TimeoutDuration => ParseDuration(Timeout);

    private static readonly Regex DurationPart =
        new(@"(?<value>\d+(?:\.\d+)?)(?<unit>ms|s|m|h)", RegexOptions.Compiled);

    /// <summary>
    /// Parses a suffixed duration such as "15m", "90s" or "2h30m".
    ///
    /// Deliberately not TimeSpan.Parse, whose "15" means fifteen DAYS. A bare number in
    /// a config file reads as minutes to almost everyone who writes one, and a timeout
    /// that silently meant days instead would hang an unattended overnight run rather
    /// than ending it. Requiring an explicit unit removes the ambiguity entirely.
    /// </summary>
    public static TimeSpan ParseDuration(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("timeout must not be empty");

        var matches = DurationPart.Matches(text);
        if (matches.Count == 0 || string.Concat(matches.Select(m => m.Value)) != text.Trim())
            throw new InvalidOperationException(
                $"timeout \"{text}\" is not a duration — use a form like 15m, 90s or 2h30m");

        var total = TimeSpan.Zero;
        foreach (Match m in matches)
        {
            var value = double.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture);
            total += m.Groups["unit"].Value switch
            {
                "ms" => TimeSpan.FromMilliseconds(value),
                "s" => TimeSpan.FromSeconds(value),
                "m" => TimeSpan.FromMinutes(value),
                "h" => TimeSpan.FromHours(value),
                _ => throw new InvalidOperationException($"unknown duration unit in \"{text}\""),
            };
        }

        return total;
    }
}
