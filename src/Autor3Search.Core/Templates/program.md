# program.md — autor3search-c# run instructions

You are optimizing this repository for speed. A compiled harness decides whether each
change is kept. You cannot reach the harness, and you must not try.

## What you may and may not touch

| You may edit | You may not edit |
|---|---|
| source files inside `scope` in `.autor3search/config.yaml` | anything in a test project |
| | anything in `{{BENCHMARK_PROJECT}}` |
| | `.autor3search/config.yaml` |
| | `results.tsv`, `run.log` |
| | any `.csproj` `PackageReference`, `Directory.Packages.props`, `packages.lock.json`, `nuget.config` |

Test and benchmark files are hashed at baseline and **restored before every
evaluation**. Editing them is not forbidden by convention — it is undone mechanically,
and the attempt costs you an experiment.

## The loop

Repeat until told to stop:

1. Print one context line so a human reading the log can see where you are:
   `[exp <n> | <branch> | vs <measure_commit> | stop: autor3search-csharp stop]`
2. Pick ONE optimization idea. One idea per experiment — a commit that changes three
   things cannot tell you which of them worked.
3. Edit only in-scope source.
4. `git add -A && git commit -m "exp <n>: <one line describing the idea>"`
5. `autor3search-csharp eval --json`
6. Apply the verdict:
   - **KEEP** (exit 0) — the commit stays. The measurement baseline has advanced to it.
   - **DISCARD** (exit 1) — `git reset --hard HEAD~1`
   - **FAIL** (exit 2) — `git reset --hard HEAD~1`, read the reason, do not repeat it.
   - **CRASH** (exit 3) — `git reset --hard HEAD~1`. The candidate did not build or timed out.
7. If the verdict JSON has `"stop_requested": true`, stop. Apply that verdict, run
   `autor3search-csharp report`, summarize what you tried, and exit the loop.

## Reading a verdict

`eval --json` prints exactly one JSON object and nothing else. The fields that matter:

- `status` — KEEP / DISCARD / FAIL / CRASH
- `reason` — why, machine-readable
- `score` — geomean of per-benchmark time ratios. Below 1 is faster.
- `deltas` — per benchmark: `pct_change`, `p`, `significant`, `base_ns`, `cand_ns`, `base_bytes`, `cand_bytes`
- `warnings` — when present, the measurement does not support reading the result at
  face value. Never ignore these; they are the difference between a number and a fact.
- `stop_requested` — your signal to end the loop.

`reason: "improvement_below_min_effect"` means your idea **worked** but by less than
the minimum effect size. That is different from `no_significant_improvement`, which
means nothing measurably moved. The first says keep going in that direction.

## Declared benchmarks

{{BENCHMARKS}}

## Idea bank

Allocation is the usual lead. `B/op` and the Gen0/Gen1 collection counts in the verdict
tell you where to look — a change that removes allocations very often removes time with
them.

**Note:** unlike the Go sibling of this tool, there is no `allocs/op` count here.
BenchmarkDotNet reports allocated *bytes* and GC collection counts, not an allocation
count, so reason about `B/op` and Gen0 collections instead.

Ideas, roughly in order of how often they pay:

1. **Stop building strings in a loop.** `s += c` reallocates every iteration. Use
   `StringBuilder`, or `string.Create`, or write into a `Span<char>`.
2. **Pre-size collections.** `new Dictionary<K,V>(capacity)` and `new List<T>(capacity)`
   avoid the repeated grow-and-copy.
3. **Avoid LINQ on hot paths.** Each operator allocates an enumerator and a closure. A
   `for` loop over a `Span<T>` allocates neither.
4. **Use `Span<T>` / `ReadOnlySpan<T>`.** `AsSpan().Slice()` does not copy where
   `Substring` does. `string.Split` allocates an array plus every element.
5. **Cache repeated work.** A value computed inside a loop that does not depend on the
   loop variable belongs outside it.
6. **Prefer `TryGetValue` over `ContainsKey` + indexer.** Two lookups become one.
7. **Use the right comparer.** `StringComparer.Ordinal` is markedly faster than the
   culture-aware default, when ordinal semantics are correct.
8. **Avoid boxing.** A struct passed as `object` or through a non-generic interface
   allocates. Generic constraints avoid it.
9. **Reuse buffers.** `ArrayPool<T>.Shared` for large short-lived arrays.
10. **Skip redundant bounds checks.** Iterating with a local length, or over a span,
    lets the JIT elide them.

## Rules

- One idea per experiment. Commit before every eval.
- Never pass `-force` to any command.
- Never edit `program.md`, `.autor3search/config.yaml`, `results.tsv` or `run.log`.
- If `eval` reports a `scope_violation`, you edited something you may not. Revert it.
- If `eval` reports `config_changed`, revert `.autor3search/config.yaml` immediately.
  The scoring rules are fixed for the whole run.
