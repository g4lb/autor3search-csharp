namespace Autor3Search.Core.RunState;

/// <summary>
/// The reference point a run scores against.
///
/// Two commits are pinned and they are deliberately NOT the same thing.
///
/// <see cref="Commit"/> is the FROZEN anchor. It never moves. The scope gate and the
/// frozen-file set always compare against it, so an agent cannot widen what it may
/// edit by banking experiments: the full accumulated diff is re-validated every eval.
///
/// <see cref="MeasureCommit"/> is what timings are compared against. It starts equal
/// to <see cref="Commit"/> and advances to the candidate's own commit after every
/// KEEP. Without that, once one real improvement was kept, every later experiment —
/// however useless — would keep comparing against the same stale starting point, and
/// a no-op could coast to KEEP on an earlier win it did not contribute to.
/// </summary>
public sealed record Baseline
{
    /// <summary>The run tag, e.g. "sep8".</summary>
    public required string Tag { get; init; }

    /// <summary>The frozen anchor commit. Never moves.</summary>
    public required string Commit { get; init; }

    /// <summary>The commit timings are measured against. Advances on every KEEP.</summary>
    public required string MeasureCommit { get; init; }

    /// <summary>SHA-256 of config.yaml at baseline. Any change fails the run.</summary>
    public required string ConfigSha256 { get; init; }

    /// <summary>When the baseline was taken.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Repo-relative project paths whose files are frozen.</summary>
    public required List<string> FrozenProjects { get; init; }

    /// <summary>Repo-relative path to the benchmark project.</summary>
    public required string BenchmarkProject { get; init; }
}
