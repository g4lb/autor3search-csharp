# autor3search-c#

[![ci](https://github.com/autor3search/csharp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/autor3search/csharp/actions/workflows/ci.yml?query=branch%3Amain)
[![nuget](https://img.shields.io/nuget/v/Autor3Search?label=nuget)](https://www.nuget.org/packages/Autor3Search)
[![downloads](https://img.shields.io/nuget/dt/Autor3Search?label=downloads)](https://www.nuget.org/packages/Autor3Search)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**Autonomous AI-driven performance optimization for any .NET repository.**

Point your coding agent at your repo and go to sleep. It proposes an optimization, runs
it through a frozen measurement harness, and the harness decides: **KEEP** or
**DISCARD**. You wake up to a log of experiments and faster code.

Inspired by [karpathy/autoresearch](https://github.com/karpathy/autoresearch), which does
this for a single-GPU LLM training loop. This does it for .NET — where the metric comes
from BenchmarkDotNet, and where **correctness is not optional**.

> **Status: early but working.** The [worked example](#worked-example) below is a real
> run: a quadratic string concatenation replaced with a `StringBuilder`, measured at
> **−25.9 %** with allocation halved and `p = 1.08e-05`, verdict **KEEP**. Every number
> in this README is a real measurement, never an illustration.
>
> **The display name is `autor3search-c#`; the command is `autor3search-csharp`.** `#` is
> illegal in a NuGet package id and starts a comment in most shells — an agent pasting
> `autor3search-c# eval` would run `autor3search-c` and silently drop the rest of the
> line. So `#` appears only in prose; every command, path and identifier here uses
> `autor3search-csharp`.
>
> Where the .NET toolchain constrains what the harness can do, [Limitations](#limitations)
> says so plainly rather than leaving you to find out.
---

## Start here

Open your coding agent inside the .NET repository you want to make faster, and paste
this:

```text
Install and run autor3search-csharp on this repository, then optimize it.

Setup:
1. dotnet tool install -g Autor3Search
   Make sure ~/.dotnet/tools is on PATH (on Windows, %USERPROFILE%\.dotnet\tools).
   If the tool installs but then exits with "You must install .NET to run this
   application", see "If the tool will not start" in the README and tell me.
2. autor3search-csharp init
   Show me the benchmarks it discovered. If it reports none, STOP and tell me:
   this tool can only optimize what it can measure.
3. git add -A && git commit -m "autor3search-csharp init"
4. autor3search-csharp doctor
   Show me any warnings. If the machine looks unfit to measure, stop and ask me
   before continuing.
5. autor3search-csharp baseline -tag <today, e.g. sep9>
   This pins a second worktree and builds it, so build time and disk use both
   roughly double. That is expected.

Then:
6. Read program.md in this repository, in full. It is your instruction set for
   the rest of this run. Follow it exactly.

Rules for the whole run:
- Never edit program.md, .autor3search/config.yaml, results.tsv, run.log, or any
  test or benchmark project. They are not yours. Test and benchmark files are
  restored from frozen hashes before every eval, so editing them does not fail
  loudly -- it is silently undone, and costs you the experiment.
- Never pass -force to any autor3search-csharp command. (I may run
  `autor3search-csharp stop -force` myself; that one is mine, not yours.)
- One idea per experiment. Commit before each eval.
- KEEP means the commit stays. Anything else (DISCARD, FAIL, CRASH) means
  git reset --hard HEAD~1.
- Print one context line before each experiment, so I can see where you are:
  [exp <n> | <branch> | vs <measure_commit> | stop: autor3search-csharp stop]

Run the loop until I stop you. I stop you by running `autor3search-csharp stop` in
my own terminal -- you will see it as "stop_requested": true in a verdict. When
you do: apply that verdict, do not start another experiment, run
`autor3search-csharp report`, summarize what you tried, and exit the loop.
```

That's the whole handoff. The agent installs the tool, sets the run up, and then
follows `program.md` -- which `init` generated for your repository, with your
discovered benchmarks filled in. It names the benchmarks in scope, spells out the
KEEP/DISCARD/FAIL/CRASH contract, and lists everything the agent must never touch.
You do not have to write those instructions.

What you get back: one commit per accepted change on a branch named
`autor3search-csharp/<tag>`, and a `results.tsv` recording every experiment that was
tried, including the ones that failed. `autor3search-csharp report` summarizes it.

Two things worth knowing before you start it:

- **It needs benchmarks.** The tool optimizes what it can measure, and refuses to
  guess: `init` will not write a config for a repository with no `[Benchmark]`
  methods. See [Repositories with no benchmarks](#repositories-with-no-benchmarks).
- **Numbers are only as good as the machine.** Run `doctor` and read it. A laptop on
  battery, a busy CPU or a running debugger all widen the noise the harness has to
  see through, and it will tell you so rather than quietly returning worse verdicts.

### If the tool will not start

Two failures are common enough to name, and neither is specific to this tool.

If `autor3search-csharp` is "command not found", `~/.dotnet/tools` is not on your
`PATH`. It was not on this machine's `PATH` by default. Add it:

```bash
export PATH="$PATH:$HOME/.dotnet/tools"        # bash / zsh
fish_add_path $HOME/.dotnet/tools              # fish
setx PATH "%PATH%;%USERPROFILE%\.dotnet\tools" # Windows
```

If it is instead found but exits with `You must install .NET to run this
application`, .NET is installed somewhere the tool's launcher does not look. Homebrew
puts it under `/opt/homebrew`, and a global tool's launcher only searches
`/usr/local/share/dotnet`, `DOTNET_ROOT` and `/etc/dotnet/install_location*` -- so the
`dotnet` command works while every global tool fails. Point `DOTNET_ROOT` at it:

```bash
export DOTNET_ROOT="$(dotnet --list-runtimes | grep -m1 Microsoft.NETCore.App \
  | sed 's/.*\[\(.*\)\]/\1/; s|/shared/Microsoft.NETCore.App$||')"
```

It is derived from `--list-runtimes` rather than from the path of the `dotnet` binary
because those are not the same place: Homebrew keeps the binary in `bin/` and the
runtime in the sibling `libexec/`, so walking up from the executable lands one
directory too high. This affects every .NET global tool, not this one. The official
.NET installer puts the runtime where the launcher looks and needs none of this.

### Or build it from source

```bash
git clone https://github.com/autor3search/csharp
cd csharp
dotnet build -c Release
dotnet test -c Release
```

307 tests, none skipped, no warnings. CI runs the full suite on `ubuntu-latest`,
`macos-latest` and `windows-latest`, and a failure on any one of them is a failure. The
integration and end-to-end tests drive real builds and real benchmarks, which is the point
of them and also why the suite is not quick.

---

## The idea

An agent optimizing its own scoreboard will optimize the scoreboard. So the scoreboard is
not something the agent can reach.

| Owner | Artifact |
|---|---|
| You | `.autor3search/config.yaml` — the scoring rules, hashed at `baseline` |
| You | the tests, and the benchmark project |
| The agent | source files inside `scope` |
| The harness | the frozen copies, the baseline record, the pinned worktree — all outside the repository |
| The harness | `results.tsv` and `run.log` |

The harness is a compiled binary installed as a global .NET tool. It is not a prompt, not
a checklist and not a convention the agent is asked to respect. Test and benchmark files
are hashed at `baseline` and **restored from those hashes before every evaluation**:
editing them is not forbidden, it is undone mechanically, and the attempt costs the agent
an experiment. Everything the metric depends on lives outside the repository the agent
edits.

---

## Quick start

```bash
autor3search-csharp init
```

Roslyn-scans the repository for `[Benchmark]` methods, the BenchmarkDotNet project, and
the test projects. Writes `.autor3search/config.yaml`, `program.md`, and the `.gitignore`
entries for the harness's own output. It refuses to overwrite an existing config without
`-force`, because the config is yours. It refuses to write one at all when it finds no
benchmarks — see [Repositories with no benchmarks](#repositories-with-no-benchmarks).

```bash
git add -A && git commit -m "autor3search-csharp init"
```

`config.yaml` is the one file under `.autor3search/` that belongs in version control. The
next step hashes it, and a dirty tree is refused, so commit before continuing.

```bash
autor3search-csharp doctor
```

Reports whether this machine can measure anything worth believing. Informational — it
never decides whether a run may proceed — and it always exits 0. On the machine that
produced every number in this README:

```
ok    disk space                661.6 GiB free
ok    cpu count                 10 logical processors
ok    operating system          macOS 26.6.2 / Arm64
ok    .NET SDK                  10.0.400
ok    load average              4.52
n/a   cpu frequency scaling     macOS exposes no governor to read, and Apple Silicon
                                schedules across P and E cores at its own discretion —
                                the single largest source of measurement noise on this
                                platform. Interleaving mitigates it; nothing eliminates it.
ok    power source              on AC power

no warnings — this machine looks fit to measure.
1 check(s) could not be made on this platform. They are listed as n/a rather than
omitted, because silence would read as a pass.
```

Seven checks: six passed, one could not be made. That last line is the point of the
command. A check that cannot run on this platform is printed as `n/a` with its reason
rather than omitted, because a shorter list of green ticks reads as a better machine.

```bash
autor3search-csharp baseline -tag 2026-09-08
```

Creates the run branch `autor3search-csharp/<tag>`, hashes and copies every file in the
test and benchmark projects into the out-of-tree store, pins a detached worktree at the
baseline commit, and records the config hash. It refuses a dirty working tree — a
baseline that depended on unversioned edits could never be reproduced — and it refuses a
tag that has already been used, because reusing one would score a new run against another
run's frozen tests.

On the demo repository this froze 5 files across 2 projects and pinned baseline `db3c29e`.

Then start the agent. Each experiment is one commit followed by one
`autor3search-csharp eval --json`.

---

## Watching a run, and stopping it

`autor3search-csharp status` is read-only: checking on a run cannot change it. Run it from
any branch and any terminal, with `-tag <tag>` if you are not on the run branch. It prints
one line each for:

- **run tag** and **branch**, and whether that branch is currently checked out
- **baseline** — the frozen anchor commit the run started from
- **measuring vs** — the measurement commit, and whether it has advanced past the baseline
  through earlier KEEPs
- **worktree** — the pinned worktree path, and a warning if it has gone missing
- **experiments** — the total, split into keep / discard / fail / crash, and which number
  is next
- **eval** — running with its pid, not running, or not running but holding a stale claim
  left by an eval that did not exit cleanly
- **stop** — not requested, requested, or requested with the running experiment asked to
  abandon

There are three ways to end a run.

| | What it does |
|---|---|
| `autor3search-csharp stop` | Graceful. The in-flight experiment finishes, is scored, and its verdict is applied. `eval --json` sets `"stop_requested": true`, which `program.md` tells the agent to treat as its signal to run `report` and exit the loop. Nothing is thrown away. |
| `autor3search-csharp stop -force` | The running `eval` abandons what it is measuring within about half a second, tears down the benchmark process tree, and exits without writing a `results.tsv` row — nothing was measured, so nothing is recorded. Commits kept before it are untouched. The command prints the `git reset --hard HEAD~1` you may want, and does not run it for you. |
| Ctrl+C, `SIGINT` or `SIGTERM` on the running `eval` | The same abandonment path as `-force`, from the terminal the eval is in. |

`autor3search-csharp stop -clear` cancels a pending stop. The force marker is sticky: it
aborts every later eval on that tag until cleared.

**All three behave identically on macOS, Linux and Windows**, by design. `stop` and
`stop -force` write marker files into the run's state directory rather than sending
signals. The running `eval` polls the force marker every 500 ms and cancels its own token
when it appears, and that cancellation is what tears the benchmark process tree down
cleanly; the graceful marker is read once the experiment completes, and reported in the
verdict. A signal-based design could not behave the same way everywhere — Windows offers
no process-to-process `SIGTERM`, so a forced stop there would end `eval` outright and it
would never get to record what it abandoned. Nothing is signalled between processes here,
so there is no platform asymmetry to document.

---

## Commands

Every command accepts `-C <dir>` to operate on a repository other than the current one,
without changing the process working directory.

| Command | What it does |
|---|---|
| `init` | Scans for `[Benchmark]` methods and test/benchmark projects; writes `config.yaml`, `program.md` and `.gitignore` entries. Refuses to overwrite an existing config without `-force`, and refuses to write one at all with no benchmarks. |
| `doctor` | Seven checks on whether this machine can measure reliably. Names the checks it cannot make on this platform instead of omitting them. Always exits 0. |
| `baseline -tag <tag>` | Creates the run branch, freezes the test and benchmark projects, pins a detached worktree. Refuses a dirty tree and a reused tag. |
| `profile` | Runs the declared benchmarks under BenchmarkDotNet's EventPipe profiler, writing `.nettrace` output under `.autor3search/profiles/`. Open it in speedscope, PerfView or Visual Studio. Real profile data beats an agent guessing at hot spots from reading source. |
| `eval` | One experiment: gates, interleaved measurement, score, a `results.tsv` row, and exit `0` KEEP / `1` DISCARD / `2` FAIL / `3` CRASH. `--json` prints exactly one JSON object and nothing else. |
| `status` | Where a run is. Read-only. |
| `stop` | Ask the run to end. `-clear` cancels a pending stop; `-force` abandons the in-flight experiment. |
| `report` | Summarizes `results.tsv`: counts by status, cumulative speedup, biggest individual wins. |
| `version` | Which build produced a given `results.tsv` row. It prints the assembly's informational version, which carries the source revision the build was stamped with — the same string every `results.tsv` row records, so a row is always traceable to the binary that wrote it. |

`eval`'s exit codes are the contract the agent loop is written against, and `--json`
emitting one object and nothing else is absolute: subprocess output goes to `run.log`,
never to stdout, because a single stray line breaks every caller.

---

## Where run state lives

Frozen copies, the baseline record and the pinned worktree live outside the repository,
because the agent edits the repository.

| OS | Path |
|---|---|
| macOS | `~/Library/Caches/autor3search-csharp/` |
| Linux | `$XDG_CACHE_HOME/autor3search-csharp/`, else `~/.cache/autor3search-csharp/` |
| Windows | `%LOCALAPPDATA%\autor3search-csharp\` |

Under that, one directory per repository hash, and one per run tag.

`AUTOR3SEARCH_CSHARP_STATE_HOME` overrides the root. It must be a fully qualified path; a
relative one is refused. On Windows that means `\foo` is refused too — it looks rooted but
resolves against whichever drive the process happens to be on, which is exactly the class
of bug an absolute-path requirement exists to prevent.

---

## Worked example

The demo repository ships a deliberately quadratic word counter. Every number below is
from one real run on Apple Silicon, macOS 26.6.2 / Arm64, 10 logical processors, .NET SDK
10.0.400, on AC power, at load average 4.52, with exactly the configuration `init`
generates: `job: short`, `count: 10`, `warmup: true`, `min_effect_pct: 1.0`,
`max_regress_pct: 5.0`.

Before — the string is rebuilt from scratch on every character:

```csharp
var word = "";
foreach (var r in field)
{
    var c = char.ToLowerInvariant(r);
    if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
    {
        word += c;   // quadratic: rebuilds the string every character
    }
}
```

After — one `StringBuilder`, reused across fields, and a pre-sized dictionary:

```csharp
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
    ...
}
```

`Demo.Benchmarks.WordCountBench.CountWords`, 10 interleaved rounds per side:

| | baseline | candidate | change |
|---|---|---|---|
| time | 62,045.9 ns | 45,968.1 ns | **-25.9%** |
| allocated | 290,648 B | 143,728 B | -50.5% |

The verdict `eval` returned for it:

```
status      KEEP
reason      improved
score       0.7409   (-25.91%)
warnings    none
p           1.08e-05  significant
```

`p = 1.08e-05` is exactly `2/C(20,10)` — the smallest p-value the Mann-Whitney U test can
produce at 10 rounds per side. It means the two sets of ten observations were completely
separated, with no overlap between them. It is not evidence of a 25.9% effect being 25.9%
accurate; it is evidence that the candidate was faster than the baseline in every
pairwise comparison the test could make.

The 50.5% drop in allocation is reported as a hint and is **never scored**. Scoring it
would let an allocation-only change with no latency win pass as a KEEP.

### How that measurement was taken

The two sides are never measured in blocks. Each side is built once — `dotnet build -c
Release` into its own output directory, so MSBuild is off the clock and out of the
variance — and then the two alternate round by round, **swapping order within each round**:
`base, cand` then `cand, base`. A leading round is measured and discarded to absorb cold
caches and tiered JIT.

That ordering is the single most important property of the tool. A laptop drifts:
thermals rise, another process wakes up, the scheduler migrates work between P and E
cores. Drift that is monotonic across a round lands on both sides equally when the order
alternates, and only on one side when it does not. Without interleaving, "the second thing
we measured was slower" is indistinguishable from "the second thing we measured was
measured later".

---

## What the harness enforces

Gates run in this order, and the first one to fail is the verdict. Scope is checked before
anything is restored or built, so an out-of-scope edit is reported as itself rather than as
a confusing build error.

| What an agent might do | What happens | `reason` |
|---|---|---|
| Weaken an assertion in a test | The file is restored from the frozen store before the evaluation. The edit never runs. | — |
| Add a new test or benchmark file | FAIL — the test and benchmark set is fixed at baseline | `new_frozen_file` |
| Hide or move a frozen file out of the walk | FAIL — restored, but it would not run | `missing_frozen_file` |
| Replace a frozen file with a symlink | FAIL — the harness never freezes or restores through a link | `symlink_swap` |
| Edit the frozen store directly | FAIL — the reference itself is lost, so a fresh `baseline` is the only honest fix | `frozen_store_tampered` |
| Edit a file outside `scope` | FAIL | `scope_violation` |
| Raise `max_regress_pct`, drop a benchmark, or otherwise edit `config.yaml` | FAIL — the scoring rules are fixed for the whole run | `config_changed` |
| Change a `PackageReference`, `Directory.Packages.props`, `Directory.Build.props`, `packages.lock.json`, `nuget.config`, or any non-frozen `.csproj` | FAIL — a dependency swap is a human supply-chain decision, and it changes *what* is measured rather than how fast it runs | `dependency_changed` |
| Move the pinned baseline worktree off its recorded commit, to make the baseline look slow | FAIL | `baseline_tampered` |
| Make the code faster by making it wrong | FAIL — `dotnet test` must pass | `tests_failed` |
| Ship something that does not compile | CRASH | `build_failed` |
| Commit again while an eval is in flight | FAIL — the commit that was gated, built, tested and measured is no longer the one that would be accepted. Nothing is changed; re-run. | `candidate_moved` |

Three things are specific to .NET and worth calling out.

**Dependency files are refused regardless of `scope`.** Any `.csproj` that is not part of
a frozen project still carries `PackageReference` elements, so it is treated as a
dependency surface and refused on the same grounds as `packages.lock.json`.

**Freezing is by project, not by file.** C# has no filename convention for tests, so every
`.cs` file in a test project — helpers and fixtures included — is frozen along with the
assertions, and so is the whole benchmark project. Freezing more than strictly necessary
is the deliberate trade: the guarantee that matters is that the agent cannot weaken an
assertion or add an easier benchmark. `unfreeze` in the config
exempts named files when a project genuinely mixes the two.

**Symlinks are refused on all three platforms.** Reparse points and junctions on Windows
count, not just Unix symlinks.

Two honest caveats about the gates themselves. The baseline-worktree check is a
**detection, not a prevention**: the state directory is outside the repository, but the
agent runs as the same OS user, so nothing physically stops it editing that worktree — the
check catches the case where HEAD moves. Treat it as catching accidental clobbering and a
careless tamper, not as a guarantee. And all freeze I/O is byte-exact, read and written as
bytes with no newline translation, so `core.autocrlf` and a `.gitattributes` in the
optimized repository cannot desynchronize the hash recorded at `baseline` from the bytes
restored before each `eval`.

---

## Scoring

```
score = geomean(candidate_mean / baseline_mean)   over the declared benchmark set
```

Below 1 is faster. A KEEP requires **all three** of:

1. **`score < 1 - min_effect_pct/100`** (default 1.0%). Not merely below 1. A statistically
   real win smaller than the machine's own noise floor is still not worth a commit, and a
   run that banks them accumulates risk without accumulating speed.
2. **At least one benchmark significant at `alpha / k`**, where `alpha` is 0.05 and `k` is
   the number of benchmarks compared — the Bonferroni correction. Testing four benchmarks
   against one uncorrected threshold gives roughly an 18% chance that one of them looks
   significant when nothing changed at all.
3. **No significant regression larger than `max_regress_pct`** (default 5.0%), judged at
   the **raw, uncorrected** `alpha`.

The asymmetry between rules 2 and 3 is the point, not an oversight. The Bonferroni
correction only ever makes significance *harder* to reach. Applying it to rule 3 would
make real regressions harder to notice — it would take the same statistical caution that
protects you from a false win and turn it into protection for a false all-clear. So the
correction is applied where a false positive means accepting a change that did nothing,
and withheld where a false negative means accepting a change that did harm. Conservative
about accepting a win, liberal about catching harm.

A benchmark's reported `significant` flag stays the raw, honest statistic. The correction
is a decision threshold layered on top; the output calls out a benchmark that cleared
`alpha` but not `alpha/k` rather than silently relabelling it insignificant.

A DISCARD distinguishes two cases the agent should treat differently.
`improvement_below_min_effect` means the idea **worked** and was too small to bank — keep
going in that direction. `no_significant_improvement` means nothing measurably moved.

### The measurement baseline advances

After every KEEP, the measurement commit moves to the kept commit and the pinned worktree
follows it. The frozen anchor does not move: scope is still diffed against the original
baseline commit, so the full accumulated diff is re-validated on every eval rather than
each violation getting exactly one eval in which to be caught.

The advance matters because without it, once one real improvement was banked, every later
experiment would keep comparing against the same stale starting point — and a no-op could
coast to KEEP on an earlier win it did not contribute to.

It also changes what `report`'s cumulative figure means. Each kept score is measured
against the previous kept commit, so each is its own incremental contribution and
successive wins **compound**. The cumulative figure is the **product** of every kept
score, not the latest one. Three experiments at 0.90 each leave you at 0.729, not 0.90.

---

## Limitations

Read this section before trusting a result.

**A KEEP is evidence, not proof.** Any fixed significance threshold admits false positives
by construction: at `alpha = 0.05`, one comparison in twenty of two identical things
clears the bar. The minimum-effect floor and the Bonferroni correction reduce that rate
substantially. Neither eliminates it, and nothing can. A long enough run will contain some
KEEPs that measured noise. Read `results.tsv`, and re-measure anything surprising.

**Laptops are noisy.** Thermal throttling, background processes, and — on Apple Silicon —
the scheduler's freedom to move work between performance and efficiency cores are all real
and none is under the harness's control. `doctor` reports what it can check and explicitly
names what it cannot, which on macOS includes CPU frequency scaling. Interleaved,
order-swapped rounds mitigate drift. They do not remove it.

**Microbenchmarks are not your application.** The harness optimizes exactly what you
declared, with the inputs you declared. A benchmark of a cold path produces numbers that
are entirely real and entirely useless.

**`count` below 4 can never produce a KEEP.** The exact Mann-Whitney test cannot reach
`p < 0.05` at that sample size no matter how large the improvement, so every experiment
would discard on a technicality. The config refuses it. With several benchmarks the
corrected threshold `alpha/k` can be unreachable at higher counts too; when no benchmark in
the set can clear it, `eval` says so in `warnings` and tells you what `count` would be
enough, rather than letting a run spend a night producing guaranteed discards.

**Warnings are never ignorable.** When `eval` reports a warning, the measurement does not
support reading the result at face value. That is the difference between a number and a
fact.

### Constraints the .NET toolchain imposes

None of these is cosmetic, and none of them is going to change soon.

**There is no allocation count.** BenchmarkDotNet reports allocated *bytes* and Gen0/1/2
collection counts; it does not report how many allocations happened. Bytes and collection
counts are what `program.md` points an agent at when it is choosing what to try next. A
count of allocations is often the sharper lead — "this loop allocates 400 times per call"
localizes a problem that "this loop allocates 12 KB per call" does not — and it is simply
not available.

**Static analysis is not a separate gate.** The pipeline builds, then tests. There is no
distinct analysis stage between the two, because .NET analyzers run inside the build,
every repository configures their severity differently, and building an arbitrary
repository with `-warnaserror` fails on pre-existing warnings that have nothing to do with
the agent's change — a gate that fires on things the agent did not do is noise, not a
gate. What exists instead is `warnings_as_errors`, off by default and opt-in, which is
useful only in a repository that is already warning-clean. Correctness here rests on your
tests, and rests on them entirely.

**There is no race detector.** .NET ships nothing equivalent, and nothing weaker was
substituted to fill the gap. If your optimization introduces a data race, the harness will
not catch it; your tests are the only thing standing between that change and a KEEP.

**An experiment is slow.** The KEEP above took roughly 8 minutes of wall clock at
`job: short` and `count: 10` — 20 measured rounds plus a discarded warmup, on the machine
described earlier. It is dominated by BenchmarkDotNet generating and compiling a fresh
project for every run, which is also what makes its numbers trustworthy. Acceptable for an
unattended overnight loop; stated here rather than discovered at 2am. `in_process: true`
in the config skips that generation and is much faster, at the cost of the process
isolation BenchmarkDotNet gives you by default. It is a config key rather than a CLI flag
on purpose: it changes what the numbers mean, so it belongs in the file that is hashed at
baseline, not in an argument the agent could pass on its own.

**Freezing is by project, not by file.** Whole test projects are frozen rather than
individual test files, because C# has no filename convention that identifies a test. See
[What the harness enforces](#what-the-harness-enforces).

**A benchmark project must exist.** .NET has no ambient benchmark discovery: benchmarks
live in a project with a `Main` that calls `BenchmarkSwitcher`. A repository without one
has nothing for the harness to measure, and `init` will say so rather than guess. See
below.

---

## Repositories with no benchmarks

`init` refuses, and says why:

> no benchmarks found. This tool optimizes what it can measure, and it has no other notion
> of "faster" — every verdict is a function of the declared benchmark timings.

That is not a limitation to work around. A harness with nothing to measure has no basis on
which to keep or discard anything, and a version of this tool that guessed would be worse
than no tool.

To use it here, add a BenchmarkDotNet project with at least one `[Benchmark]` method
covering the code you want made faster:

```bash
dotnet new console -o benchmarks
dotnet add benchmarks package BenchmarkDotNet
```

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

[MemoryDiagnoser]
public class MyBench
{
    [Benchmark] public void Thing() => Subject.Thing();
}

public static class Program
{
    // BenchmarkSwitcher is what makes --filter work, and the harness relies on --filter.
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
```

Then run `init` again. Benchmark the path that actually dominates your workload: a
benchmark of a cold path produces numbers that are entirely real and entirely useless.

`init` also refuses when it finds `[Benchmark]` methods but no project referencing
BenchmarkDotNet, when it finds more than one BenchmarkDotNet project (it measures one per
run and will not guess which you meant), and when every project in the repository is a
test or benchmark project, leaving nothing for an agent to change. It warns loudly, rather
than proceeding quietly, when no test project exists — without tests, correctness cannot be
gated, and an agent is free to make code faster by making it wrong.

---

## License

MIT. Copyright (c) 2026 Gal Be.

The Mann-Whitney U test and the median confidence-interval estimator are hand-written C#
ports of third-party implementations licensed under BSD 3-Clause. [NOTICE](NOTICE) carries
their copyright notices in full, as that license requires.
