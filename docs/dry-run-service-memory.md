# Service dry-run memory reduction — implementation handoff

> **Status: IMPLEMENTED AND MEASURED (2026-08-01).**
>
> **The reported defect is fixed: post-run footprint 348 MB → 12 MB (−97%)** at the reported workload
> (33,500 source / 357,000 destination files), against an 8 MB idle floor, and the run is 2.2× faster.
> The baseline row in §5.5 reproduces the original complaint exactly, so this is a before/after on the
> real bug rather than an inferred improvement.
>
> **Implemented:** Stage 0 (§5.1–§5.4); Stage 1 (§6.1–§6.3); Stage 2 (§7.1 trim coordinator + setting;
> §7.2 `ConcurrentGarbageCollection=false`); Stage 3 (streamed sweep — see the deviation note at §8);
> Stage 4 (§9.1, §9.2).
>
> **Reverted after measurement:** `System.GC.ConserveMemory=5` — a measured no-op (§5.5).
>
> **Skipped:** §5.6's in-process retention test. `MemoryProbe` measures the shipped artifact's
> committed memory, which is what the complaint was about; a managed-heap proxy would need its own
> doc comment warning it is not that.
>
> **Still open — peak.** Peak fell only 10% (363 → 325 MB) and is now dominated by GC burst policy
> rather than by live bytes (§5.5 explains why, including the ~90 MB of peak deliberately traded for
> the residual fix). That is the remaining lever if peak matters.
>
> **Not done — the human end-to-end check in §12**, in particular re-running a Mirror profile and
> confirming the `Deleted` orphan set is identical to a pre-change run. Stage 3 changed how orphans are
> enumerated and deduped; a wrong answer there is a destructive-plan bug, and no automated test
> substitutes for looking at it.
>
> **Workload caveat, from the one real measurement taken so far (§5.5).** The machine this was
> implemented against runs a *source-heavy* profile — 71,923 source files but only 4,735 swept
> destination files, a 40 ms sweep — which is the inverse of the 33.5k/357k report this plan was
> written for. Stage 3's peak reduction scales with sweep size, so it is close to a no-op there and
> worth ~145 MB on the reported workload. Do not conclude from a source-heavy run that Stage 3 did
> nothing; measure on a sweep-heavy one.

**Audience:** the coding agent implementing this. You are expected to read every referenced file before
touching it. Every file/line reference was accurate at authoring time (2026-07-31, branch
`feature/live-single-job-vertical`, at/after commit `3115925`) — **confirm each one before relying on
it.**

**Nothing in this document was measured.** Every byte figure is a static estimate from object-layout
arithmetic over the cited code. Stage 0 exists to replace those estimates with real numbers, and the
ranking of Stages 1–4 may change once it does.

---

## 1. The problem

`FileManager.Service` is a background service intended to be always running. Reported behaviour:

- **~7 MB private commit when idle** — acceptable.
- One dry run of a "copy Downloads → Documents" profile takes it to **~340 MB**.
- **After the run completes it stays at ~340 MB.**

Workload shape, measured by the reporter: **33,500 source files, 357,000 destination files scanned.**
That ratio is the key to the whole diagnosis — the cost is concentrated in the *destination sweep*, not
the source scan.

Goal: post-run footprint back near the idle floor, and in-run peak as low as practical. A slower dry run
is an accepted trade (the work is IO-bound); aggressive spill thresholds, tighter buffers and bounded
pools are all explicitly in scope.

### 1.1 Two different problems with disjoint fixes — establish which one you have first

"Stays at ~340 MB" and "peaks at ~340 MB" are not the same defect:

- If the residual is **committed-but-free GC heap**, Stages 1–2 fix the reported complaint and Stage 3 is
  merely correct rather than urgent.
- If the residual is **live retained bytes**, Stage 3 is the fix and Stages 1–2 are cosmetic.

After the stream ends, everything the sweep built is unreachable and `EvaluationCarrierPool` is a per-run
local that is dropped, so committed-but-free is the leading hypothesis — **but it is a hypothesis.**
Three numbers side by side settle it: `GC.GetTotalMemory(true)` (live),
`GC.GetGCMemoryInfo().TotalCommittedBytes` (committed), and `Process.PrivateMemorySize64` (what the
reporter is looking at). Stage 0.1 makes the service print all three after every run.

---

## 2. Current architecture — what already exists, do not redo

The service previews a profile and streams findings to an Avalonia UI over a named-pipe IPC channel.
Only `FileManager.Contracts` crosses the process boundary. `PublishAot=true` on both executables.

A streamed dry run has three phases:

1. **Source scan + evaluate** — `DryRunEngine.SimulateStreamAsync`
   (`src/FileManager.Core/DryRun/DryRunEngine.cs`) runs a fused scan+evaluate pipeline: a bounded
   `Channel<Payload>` (1024) feeding `Parallel.ForEachAsync` workers that call `EvaluateFileAsync`,
   producing one `FileEvaluation` bundle per source file into a **sink** callback.
2. **Spool + chunk** — the sink writes to an `IDryRunSpool`; after the pipeline drains, the engine
   replays it through a `StreamAccumulator` that flushes a `DryRunChunk` each time a running upper-bound
   size crosses `ChunkByteThreshold`.
3. **Destination sweep** — `DryRunStreamHandler`
   (`src/FileManager.Core/IPC/Handlers/DryRunStreamHandler.cs`) accumulates a survivor set from the
   streamed chunks, then calls `DestinationProjector.Sweep` to find pre-existing destination files no
   source writes to, and emits those as additional chunks.

**Three prior optimization passes have already landed. Read them before changing anything; do not
repeat their work.**

- `docs/dry-run-memory-optimization.md` — **implemented.** Cut the *UI's* retained heap from 519 MB to
  219 MB (lazy display strings, root interning, a normalized directory-table wire contract with IPC
  protocol v2, plus a `BuildForest` rewrite). Different process; out of scope here.
- `dry-run-pooling-plan.md` (repo root) — **implemented.** Pooled the per-file carrier objects on the
  spool read-back path. `src/FileManager.Core/DryRun/PooledEvaluation.cs` exists, and
  `tests/FileManager.Core.Tests/DryRun/DryRunEngineTests.cs:771-787`
  (`Spilled_stream_returns_every_rented_carrier_to_the_pool`) guards borrow == return.
- `docs/dry-run-perf-optimizations.md` — a CPU/throughput plan (fused pipeline, collapsed stats,
  concurrent hashing). Its **item 3** (overlap the sweep with the source pass) is the *wrong direction*
  for this work — it trades memory for latency. Do not implement it here.

Also read `docs/decisions/0001-aot-vs-jit-for-executables.md`. It decided to keep both executables on
NativeAOT (`:32`) — **not revisited here** — and recorded a baseline of idle WS 19.1 MB / idle private
commit 7.5 MB / peak WS 250 MB at 24,059 files (`:57-63`). See §3.5 for why that 250 MB is **not**
comparable to the 340 MB in this report.

---

## 3. Diagnosis

### 3.1 The source phase and the destination sweep are the same operation with two different pipelines

They already share the walk — both go through `IScanScheduler` / `FileSystemService` with
`ScanSessionOptions` policy callbacks (`OnFile` / `OnSubdirectory` / `OnFault`). What they do **not**
share is the output pipeline, and that is where all the memory efficiency lives:

| | Source phase | Destination sweep |
|---|---|---|
| Hand-off | bounded `Channel<Payload>` (1024) | `session.Consume()` direct |
| Buffering | `IDryRunSpool`, spills to disk at 1 MiB (`FileDryRunSpool.cs:130-143`) | `List<Candidate>` held to completion (`DestinationProjector.cs:142`) |
| Ordering | discovery order, **no global sort** | global `Sort` (`DestinationProjector.cs:200`) |
| Chunking | `StreamAccumulator`, byte-budgeted, fresh lists per flush (`DryRunEngine.cs:775-847`) | hand-rolled 4096 loop + `with {}` copy (`DryRunStreamHandler.cs:238-249`) |
| Per-entry objects | pooled carriers, recycled per chunk (`PooledEvaluation.cs`) | fresh `PhysicalFile` + **two** `VirtualFileOperation`, unpooled |

The gap is mostly **historical**: the source path was the subject of both prior optimization passes and
the sweep was in scope for neither.

But one **real asymmetry** enabled the source path's design, and it is the crux of Stage 3: the streamed
source path was *relieved of global sorting*. It writes findings to the spool in discovery order because
the client sorts for display (`dry-run-pooling-plan.md` §2, `DryRunEngine.cs:36-39`,
`IDryRunEngine.cs:32-36`). The sweep never got that memo. **A global sort is exactly what forces holding
every entry at once**, and it is why the sweep cannot use the spool as-is.

### 3.2 The streamed sweep's sort is dead weight — verified, and this is what Stage 3 rests on

- **The client re-sorts, by a different key.** `src/FileManager.UI/ViewModels/DryRunViewModel.cs:1610` —
  the Destinations tab's `_all` is *"presorted by `DryRunSort` at load"*.
  `DryRunDestinationsTab.ComputeLoad` (`:1715-1799`) sorts an index array by
  `(RelativeKey OrdinalIgnoreCase, root, original index)`. `DryRunSort` keys on the path **relative to
  its root**, deliberately, per the comment at `:1017-1019`: absolute root prefixes *"scatter the same
  logical file to different rows and defeat side-by-side comparison."* Swept ops (`SourceIndex == -1`)
  collect into `noSource` (`:2492-2493`), append at `:2630-2634`, and run through that same sort.
- **Service order survives only as a stability tiebreak** (`:1784-1793`), reachable only when two rows
  share both relative key and root — for swept rows that means two distinct files differing only by
  case, impossible under the Windows `OrdinalIgnoreCase` comparison (`Jobs/Job.cs:38-39`).
- **Every other consumer is order-free.** The handler's frame loop needs only `Ops[i]` ↔ `Files[i]`
  pairing (`DryRunStreamHandler.cs:238-249`); `DryRunSpaceEstimator.Accumulate` resolves subjects by
  index; the tree view sorts itself (`DryRunViewModel.cs:407`, `:972`) and the grid declares
  `CanUserSortColumn = false` with its own comparers (`:785-809`).
- **Test blast radius is one method.** Of 15 tests in `DestinationProjectorTests.cs`, only
  `Result_is_identical_across_worker_counts` (`:210`, three `Assert.Equal` at `:221-224`) asserts order.
  The 5 sweep tests in `DryRunStreamHandlerTests.cs:264-424` are order-insensitive
  (`Assert.Contains` / kind counts / `.OrderBy` before comparing).
- **The batched path is different and must stay sorted.** `ReportBuilder.TryAddSweepEntry` drops the tail
  when the 12 MiB frame budget trips (`DryRunEngine.cs:223-231`), so sortedness is what makes the kept
  set reproducible there. Its peak is bounded anyway (≤ 50,000 entries ≈ 17 MB).

**Confirm §3.2 yourself before implementing Stage 3.** It is the single load-bearing claim in this
document. If it does not hold, fall back to the note in §11 on external merge sort.

> **Re-verified 2026-08-01 — it holds.** `DryRunViewModel`'s `_all` is "presorted by `DryRunSort` at
> load"; `DryRunDestinationsTab.ComputeLoad` sorts an index array by `(RelativeKey OrdinalIgnoreCase,
> root OrdinalIgnoreCase, original index)`, so the service's absolute-path order is only the final
> tiebreak and is reachable only when relative key **and** root both match — for swept rows that means
> two distinct files differing only by case, impossible under the Windows `OrdinalIgnoreCase`
> comparison. Swept ops (`SourceIndex < 0`) collect into `noSource`, get their own single-entry rows,
> and run through that same sort. Test blast radius was as predicted: one order-asserting test
> (`Result_is_identical_across_worker_counts`), and it needed no change because the batched path keeps
> its sort.

### 3.3 Where the peak comes from

Sweep peak live set at 357,000 entries with ~96-char paths (216 B/string, `PhysicalFile` 64 B,
`VirtualFileOperation` 64 B, `Candidate` 32 B):

| Item | MB |
|---|---|
| `List<Candidate>` array (524,288 capacity) | 16.8 |
| `PhysicalFile` × 357,000 (`DestinationProjector.cs:169-176`) | 22.9 |
| full-path strings × 357,000 (`FileSystemService.cs:143` `entry.ToFullPath()`) | 77.1 |
| `VirtualFileOperation` × 357,000 (`DestinationProjector.cs:221-229`) | 22.9 |
| `Merge`'s two full-size list arrays (`:203-204`) | 5.7 |
| **peak live (inside `Merge`, with `collected` still rooted)** | **145.4** |

128.6 MB of that stays rooted from `DryRunStreamHandler.cs:216` until the method ends (`:263` still reads
`sweep.Files.Count`). Legitimately live alongside: the survivor set ≈ 8 MB, the wire directory table
≈ 5 MB (scales with directory count, not file count — rule it out, don't chase it).

**Second-largest peak item, and it is not the sweep.** `ScanScheduler.cs:197-200` materializes an entire
directory level per in-flight worker, and a **parked** directory keeps that queue alive in the unbounded
`_parked` list:

```csharp
work.Remaining = new Queue<Result<FileSystemEntry, EnumerationFault>>(_fileSystem.EnumerateEntries(work.Directory));
```

At ~320 B per `FileSystemEntry` (record + `FullPath` + `FileName`), a 1,000-entry parked directory is
~320 KB, and up to `min(ProcessorCount * 8, 256)` can be materialized or parked at once — **up to
~80 MB**. The materialization is deliberate (the comment says it exists so no OS directory handle is held
across a park), so **do not undo it**; lower the concurrency instead (Stage 4).

**Churn that drives commit growth rather than live bytes:** `FileSystemEntry` + name strings 48.6 MB; the
handler's second op set (`DryRunStreamHandler.cs:246`) 22.9 MB; wire records 45.7 MB; and **~88 sweep
frames × ~1.1 MB ≈ 97 MB, every one a Large Object Heap allocation** (`IpcSerializer.cs:16-17` →
`JsonSerializer.SerializeToUtf8Bytes` returns an exact-size `byte[]`, used at `IpcServer.cs:370`).

### 3.4 Why it stays at 340 MB

Nothing rooted explains it — the pool, spool and accumulator are per-run locals dropped when the iterator
dies (`IpcServer.cs:315-318`). Three compounding runtime-level causes:

1. **No GC or runtime configuration exists anywhere in the repo.** `FileManager.Service.csproj` sets only
   `OutputType`, `TargetFramework`, `ImplicitUsings`, `Nullable`, `PublishAot`, `InvariantGlobalization`.
   There is no `Directory.Build.props`, no `runtimeconfig.template.json`, no `global.json`, no
   `AppContext.SetSwitch`. (Workstation GC confirmed — `obj/Release/net10.0/win-x64/native/link.rsp:10`
   links `Runtime.WorkstationGC.lib`.)
2. **The process never asks for the memory back** — repo-wide, zero `GC.Collect`,
   `GCSettings.LargeObjectHeapCompactionMode`, or `GCSettings.LatencyMode` in `src/`.
3. **Nearly every IPC frame lands on an uncompacted LOH, unpooled** — see the churn figures above.

### 3.5 Two corrections to beliefs you may arrive with

- **Scan-thread stacks are *not* a material share of the 340 MB.** The shipped
  `publish/FileManager.Service.exe` PE optional header has `SizeOfStackReserve = 1,572,864` (1.5 MB) and
  `SizeOfStackCommit = 4,096` (4 KB). Windows reserves address space and commits lazily; the walk is
  iterative (`ScanWorkState` queues, not recursion), so realistic private commit is ~16–48 KB/thread →
  **~4–12 MB total**, released 15 s after the scan (`ScanScheduler.cs:32`). The comment at
  `ScanThreadResolver.cs:16` saying "~1 MB of stack each" describes *reserve* and will send you
  optimizing the wrong thing — fix it (Stage 4.2).
- **The ADR's 250 MB peak is not comparable to 340 MB.**
  `docs/decisions/0001-aot-vs-jit-for-executables.md:51-63` states the dry run ran **against an empty
  target**, so it performed essentially no destination sweep. The 340 MB is mostly *new* cost from the
  357k-entry sweep, not a regression against that baseline. Once Stage 0 produces numbers, revise that
  ADR (or add a sibling) to record the sweep-heavy workload shape so the two are never conflated again.

---

## 4. Ground rules — a change that breaks any of these is wrong even if it is smaller

1. **Read-only (`I-DRYRUN-RO`, spec §8).** Every collaborator here only enumerates, stats, hashes and
   probes existence. No mutation, ever.
2. **`Ops[i]` references `Files[i]`.** `SubjectIndex` is a position into the destination file list,
   globalised by the handler's running `destinationCount`.
3. **Source truncation suppresses the sweep entirely** (`DestinationProjector.cs:79-80`). An incomplete
   survivor set makes every orphan judgement unsound, so it emits nothing rather than fabricate
   deletions or Untouched entries.
4. **Truncation happens at whole (file, op) pair boundaries** so no retained operation references a
   dropped file (`DryRunReport.cs:19-22`).
5. **The batched path stays sorted and behaviourally unchanged** — `DryRunEngine.SimulateAsync`,
   `DestinationProjector.Project` (`:51-57`), used by the CLI and most unit tests.
6. **Cancellation and fault semantics.** A cancelled run resolves to `Result.Canceled()` (batched) or
   throws `OperationCanceledException` from the async enumerator (streamed) — never a misleading partial
   success. A **Fatal** enumeration fault aborts the run; a **Warning** is logged and skipped.
7. **AOT / trim clean.** No reflection, no `JsonSerializer` reflection overloads — source-generated
   contexts only (`FileManagerJsonContext`, `DryRunSnapshotJsonContext`). Any new serialized type or DI
   registration must stay reflection-free.
8. **Land each stage as its own commit** so wins are attributable and a regression is easy to bisect.
   Repo convention for multi-line commit messages: write the message to a file and `git commit -F <path>`
   — never inline `-m` or a shell heredoc. Do **not** stage `third_party/TreeDataGrid` (a pre-existing
   submodule change).

---

## 5. Stage 0 — Make the numbers visible (do this first; nothing later is trustworthy without it)

### 5.1 Extend the existing per-run timings log line

`DryRunStreamHandler.cs:255-263` already logs a phase breakdown. Add `GC.GetTotalMemory(false)`,
`GC.GetGCMemoryInfo().HeapSizeBytes`, `.TotalCommittedBytes`, and
`Process.GetCurrentProcess().PrivateMemorySize64`. ~20 lines, and it puts the three-way split that
answers §1.1 into the real service log after every real run, permanently.

### 5.2 Log `GC.GetConfigurationVariables()` once at startup

Information level, in `EngineHost`. A ~40-entry dictionary, once per process
(`RhEnumerateConfigurationValues` is implemented in NativeAOT). **Without this, every claim in Stage 2 is
unfalsifiable in the shipped binary.**

### 5.3 The frame-size regression test — highest-priority test in this document

New in `tests/FileManager.Core.Tests/DryRun/`. Drive `DryRunStreamHandler` with a stub engine and stub
projector producing N entries, serialize every yielded `IpcResponse` via
`IpcSerializer.SerializeResponse`, and assert **every** frame is `< 85_000` bytes. No memory
measurement, fast, deterministic. This is what makes Stage 1.2 permanent rather than a constant someone
tunes back up in six months.

### 5.4 `tools/FileManager.MemoryProbe` — a standalone console harness

New project: `net10.0`, `Exe`, `PublishAot=false` (dev tool, keep it JIT so it builds fast), referencing
**only `FileManager.Contracts`**. Add it to `File-Manager.slnx`; no `src` project may reference it.

This commits the manual procedure already written up at
`docs/decisions/0001-aot-vs-jit-for-executables.md:76-88`, and it is the **only** thing that measures the
real shipped artifact. Everything in Stages 1–2 is invisible to an in-proc xUnit test, because xUnit runs
under **JIT** with the test host's `runtimeconfig.json`, not the service's ILC-embedded knobs.

- **One correction to the ADR procedure:** it drives `RequestAsync<DryRunResponse>`. That path would trip
  `IPC_RESPONSE_TOO_LARGE` at 357k entries. Use `IpcClient.DryRunStreamAsync` — the path the UI uses.
- **Isolation.** Launch the *published* exe via `ProcessStartInfo` with `UseShellExecute = false` and
  `Environment["LOCALAPPDATA"]` / `Environment["FILEMANAGER_PIPE_NAME"]` set, then connect with
  `IpcClient` on that pipe name. Do **not** route the launch through `ServiceLauncher.ConnectOrStartAsync`
  when you need controlled env. Note the single-instance mutex is per-user regardless of pipe name, so
  runs must be sequential.
- **Tree generation.** 33,500 source + 357,000 destination **0-byte** files, 200 per leaf directory,
  ~90-char paths (matching the UI probe's "realistic deep paths" shape). Zero-byte is safe — the sweep
  only stats, never reads content — and with an empty survivor set every destination file becomes a
  sweep entry, which is the exact case to stress. `Parallel.ForEach` over leaf directories, ~1–3 min on
  NVMe. **Cache it** behind a `.tree-manifest` sentinel recording (source-files, dest-files,
  files-per-dir) and skip regeneration when it matches — that caching is the difference between a harness
  someone uses and one they don't. Disk cost ~400 MB (0-byte NTFS files are MFT-resident, ~1 KB each);
  say so in `--help`.
- **Capturing peak — do not hand-wave this.** A background sampler thread ticks every 100 ms calling
  `process.Refresh()` and records `PeakWorkingSet64` (the OS's own high-water mark, free and race-free),
  `PrivateMemorySize64` (Windows has **no** per-process peak counter for this, which is precisely why the
  sampler exists), and `WorkingSet64`.
- **Report four phases:** `idle-before`, `peak-during`, `settle t+0s`, **`settle t+30s`**. The last row is
  essential — Stage 2's debounced trim fires *after* the run, so a t+0 reading would show no improvement
  and make the trim look broken. Correlate the curve against `IProgress<DryRunProgress>` phase
  transitions (`ScanningSources` → `SweepingDestinations` → `BuildingLists`). `--csv <path>` makes runs
  diffable; `--budget-private-mb N` plus a nonzero exit code makes it a gate.
- **Harness trap.** The harness itself receives the client-side reassembled 357k-entry `DryRunReport`,
  which is hundreds of MB in the *harness* process. Build and drop it inside a
  `[MethodImpl(MethodImplOptions.NoInlining)]` helper so the harness doesn't OOM and so nobody confuses
  the two processes' numbers.

```powershell
# 1. Publish the real artifact (native link needs the MSVC/C++ toolchain; run from vcvars64)
dotnet publish src/FileManager.Service -c Release -r win-x64

# 2. Build the probe
dotnet build tools/FileManager.MemoryProbe -c Release

# 3. Generate + cache the trees (once)
FileManager.MemoryProbe.exe --tree D:\fm-memprobe --source-files 33500 --dest-files 357000 `
  --files-per-dir 200 --generate-only

# 4. Measure
FileManager.MemoryProbe.exe --tree D:\fm-memprobe `
  --service src\FileManager.Service\bin\Release\net10.0\win-x64\publish\FileManager.Service.exe `
  --settle-seconds 30 --csv memprobe.csv
```

### 5.5 Record the baseline before touching anything else

**Measured 2026-08-01** with `tools/FileManager.MemoryProbe` against the **published AOT binary**,
33,500 source / 357,000 destination files (all swept — Mirror, disjoint survivor set), sequential runs
on one machine, `--settle-seconds 35`:

| Build | idle | peak | t+0s | t+35s | wall time |
|---|---|---|---|---|---|
| **baseline** (`e45194d`, pre-change) | 8 MB | 363 MB | 357 MB | **348 MB** | 7,726 ms |
| **all stages** | 8 MB | 325 MB | 197 MB | **12 MB** | 3,506 ms |

- **The reported defect is fixed: 348 MB → 12 MB residual (−97%)**, against an 8 MB idle floor. The
  baseline row reproduces the complaint exactly — "stays at ~340 MB" — so the workload and the bug are
  confirmed, not inferred.
- **2.2× faster** (7,726 → 3,506 ms), which also satisfies §12's Stage 4 wall-time gate: the
  concurrency reduction cost nothing measurable, on this workload it went the other way.
- **Peak fell only 10%** (363 → 325 MB), far less than §8's estimate. Live bytes clearly did drop —
  t+0s fell 45% (357 → 197 MB) and the trim reaching 12 MB proves almost nothing is retained — but
  peak *commit* during a burst is a function of GC policy, not of live set, and the GC knob below
  deliberately raises it. **Peak is the remaining work if anyone wants it.**

#### Attribution: the two GC knobs, isolated

Three runs varying only `FileManager.Service.csproj`:

| GC configuration | peak | t+0s | t+35s |
|---|---|---|---|
| neither knob | 234 MB | 234 MB | **231 MB** |
| `ConcurrentGarbageCollection=false` only | 326 MB | 197 MB | **12 MB** |
| both knobs | 325 MB | 197 MB | **12 MB** |

Two conclusions, both acted on:

1. **`ConcurrentGarbageCollection=false` is what makes the trim work at all.** With background GC on,
   `MemoryTrimCoordinator`'s aggressive compacting collect reclaims essentially nothing (231 MB
   residual) — the non-compacting concurrent gen2 the docs describe. Without this knob, Stage 2.1 is
   inert. That was not predicted anywhere in this document.
2. **`System.GC.ConserveMemory=5` does nothing** once background GC is off — every column within
   noise. Matches the open no-op report (dotnet/runtime#93914) the plan flagged. **Reverted** per §12.

And the honest cost: turning background GC off **raises peak by ~90 MB** (234 → 326 MB), because
blocking gen2s let the heap grow further during the burst. Accepted deliberately — a peak during a run
the user asked for is not what gets reported as a leak; 231 MB retained forever by an always-running
service is. Anyone optimizing peak later should re-check this trade rather than assume it.

#### Reproducing

```powershell
# from a VS Developer PowerShell, or with vswhere on PATH:
#   $env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
# Without it the ILC native link fails and leaves the publish directory EMPTY.
dotnet publish src/FileManager.Service -c Release -r win-x64
dotnet build tools/FileManager.MemoryProbe -c Release
tools\FileManager.MemoryProbe\bin\Release\net10.0\FileManager.MemoryProbe.exe `
  --tree D:\fm-memprobe `
  --service src\FileManager.Service\bin\Release\net10.0\win-x64\publish\FileManager.Service.exe `
  --settle-seconds 35 --csv memprobe.csv --label current
```

Per-stage rows (Stage 1 alone, Stage 3 alone, …) were **not** measured — only the two endpoints and
the GC-knob isolation above. Anyone attributing a specific stage should measure at its commit.

#### Findings so far (2026-08-01)

**Frame size — measured, and worse than this document estimated.** `DryRunFrameSizeTests` (§5.3) drives
the handler over 20,000 swept entries with ~90-char paths and serializes every yielded frame:

| | largest frame | frames | on the LOH |
|---|---|---|---|
| before (`DestinationChunkSize = 4096`) | **1,446,714 B** | 8 | 5 |
| after (`WireChunkByteBudget = 48 KiB`) | **27,846 B** | 289 | 0 |

That is **~353 B per swept entry**, against the ~250–350 B this document assumed. Consequences:

- §3.3's "~88 sweep frames × ~1.1 MB ≈ 97 MB" of LOH churn is really **~87 × ~1.45 MB ≈ 126 MB** at
  357k entries — about 30% worse, and the single largest churn item in the diagnosis.
- §6.2's fallback advice is **wrong**: a fixed `DestinationChunkSize` of 256 lands at ~90 KB, over the
  85,000-byte line. Only the byte budget works. Do not reintroduce a fixed count.
- The directory-per-file worst case (every entry dragging a fresh `DryRunDirectory` into its frame,
  which the chunk estimate does not itself account for) measured **25,013 B** — comfortably inside the
  budget's headroom. Both shapes are pinned by the test.

**One real service run (2026-08-01), source-heavy profile.** 71,923 source files, **4,735** swept
destination files, 5,249 ms total (sweep 40 ms):

```
memory managed 40MB, GC heap 47MB, GC committed 70MB, process private 90MB
```

Read carefully:

- **Committed-but-free is ~23 MB here** (70 committed − 47 heap). Real, and Stage 2's trim targets
  exactly it — but it is not a 340 MB residual.
- **This is a NEAR-PEAK sample, not the residual.** §5.1's log fires inside `HandleStreamAsync` while
  the sweep result, estimator, survivor set and directory table are all still rooted. The code says so;
  do not quote it as the settled figure. A true post-run reading needs a sample after the iterator tears
  down — Stage 2's trim coordinator is the natural place.
- **The workload is the inverse of the report's.** 71.9k source / 4.7k swept, against 33.5k / 357k. The
  sweep is nearly free here, so the cost concentrates in the source phase, and §3.3's whole
  sweep-dominated cost table does not apply to this machine.

**Still unanswered — §1.1's question**, for the *reported* 340 MB case: nothing measured yet
distinguishes committed-but-free heap from live retained bytes on a sweep-heavy run. That needs either
§5.4's harness or one UI-driven run against a 357k-destination profile.

### 5.6 Optional companion — a Core-side retention test

If you want a CI guard on retention (not commit), add
`tests/FileManager.Core.Tests/DryRun/DryRunStreamMemoryTests.cs`, reusing the pattern the UI tests
already establish at `tests/FileManager.UI.Tests/DryRunViewModelMemoryTests.cs:119-135`: a
`[MethodImpl(MethodImplOptions.NoInlining)]` builder returning `(kept, new WeakReference(transient))`,
`GC.GetTotalMemory(forceFullCollection: true)` either side, and `Assert.False(transient.IsAlive)` **before
trusting the delta**. Copy `tests/FileManager.UI.Tests/MemoryCollection.cs` in and pin to it —
`FileManager.Core.Tests` parallelizes collections by default and `GC.GetTotalMemory` is a whole-process
reading. Measure `DestinationProjector.Sweep` at 357k entries with a **fake `IScanScheduler` yielding
synthetic `ScanResult`s** (no real files, which is what makes 357k affordable in a unit test).

Put in its doc comment that `GC.GetTotalMemory` measures the managed heap, **not** committed bytes, so a
green test does not mean the footprint is fixed. Do **not** use a `[MemoryDiagnoser]` benchmark as the
gauge — BenchmarkDotNet's `Allocated` is per-op allocation, not retention (the UI probe's own comment
says so). A `DestinationProjectorSweepBenchmarks` is still worth adding to track *allocation rate*, with
that caveat stated.

---

## 6. Stage 1 — Allocation and LOH (cheap, high confidence, no throughput risk)

### 6.1 Pool the serialization buffer — the single biggest allocation win here

One `ArrayBufferWriter<byte>` per connection, created in `IpcServer.ServeConnectionAsync`, threaded into
`TryWriteResponseFrameAsync`, `Clear()`ed per frame. ~97 MB of LOH churn becomes one ~100 KB gen2 array.

- Add writer-based overloads to `IpcSerializer`: `SerializeResponse(IpcResponse, IBufferWriter<byte>)`,
  implemented with a `Utf8JsonWriter` over the buffer plus
  `JsonSerializer.Serialize(writer, response, FileManagerJsonContext.Default.IpcResponse)` — the same
  source-generated discipline the class exists to enforce, AOT-safe.
- **Keep the `byte[]` overloads.** `IpcServer.Broadcast` hands one frame to N drop-oldest subscriber
  channels and has no safe return point; that path must keep allocating.
- Move the `MaxPayloadBytes` guard (`IpcServer.cs:370-385`) from `bytes.Length` to
  `writer.WrittenCount` — same logic, same `IPC_RESPONSE_TOO_LARGE` reply, no double serialization.
- `ArrayBufferWriter` never shrinks. Add a reset-if-grown-past-N step (or an `ArrayPool<byte>`
  rent/return behind a tiny `IBufferWriter` shim) so one large response doesn't pin megabytes for the
  connection's life.
- `IpcFrameCodec.WriteFrameAsync` already takes `ReadOnlyMemory<byte>`, so a sliced rented buffer works
  unchanged. Only the `new byte[4]` header (`IpcFrameCodec.cs:32`) needs a reusable per-connection array
  — **not** a static; the method is async and concurrent across connections.
- **Leave the read path alone** (`IpcFrameCodec.cs:78`, `new byte[length]`). Changing it alters the
  `Result<byte[], string>` return contract and ripples through every caller and test, and the service
  only reads small request frames.
- **Do not reach for `PipeWriter`.** `IpcFrameCodec` is shared by client and server over a raw `Stream`,
  and the framing is four bytes of `BinaryPrimitives`; `PipeWriter` adds its own pooled segments and a
  second flush model for no benefit.

### 6.2 Size chunks in the wire shape, not the engine's

`ChunkByteThreshold` is compared against a deliberately generous upper bound in the engine's
*pre-normalization* currency — `256 + StringUpperBound(Path) + StringUpperBound(Root)` at 6 bytes/char
(`DryRunEngine.cs:866-869`) — but the wire form carries only `FileName` plus two ints. For ~90-char paths
that over-counts by roughly 6–10×, so a "1 MiB" file-phase chunk is really ~100–170 KB on the wire: just
over the LOH line, not 1 MB. **The real offender is the sweep**: `DestinationChunkSize = 4096`
(`DryRunStreamHandler.cs:35`) × ~250–350 B/entry ≈ **~1.1 MB per frame × 88 frames**.

Give both phases one constant, measured with the estimators that already exist for exactly this purpose —
`UpperBoundBytes(DryRunFile)` / `UpperBoundBytes(DryRunOperation)` (`DryRunEngine.cs:872-875`, used by the
batched `ReportBuilder`):

```csharp
internal const int WireChunkByteBudget = 48 * 1024;   // wire-shape upper-bound estimate
```

Because the structural constants stay generous (`PhysicalFileStructuralBytes = 256` /
`OperationStructuralBytes = 320` vs ~140 B actual), 48 KB of *estimate* lands at ~20–25 KB *actual* —
comfortably sub-LOH even with long filenames, and it self-adjusts to path length instead of assuming it.
Bring `SpillThresholdBytes` (`DryRunEngine.cs:104`, which defaults to `ChunkByteThreshold`) down with it
so the spool spills sooner.

**If you keep a fixed count instead**, `DestinationChunkSize` must go 4096 → **256** (~77 KB worst case);
512 would be ~154 KB and over the line. This is exactly why the byte budget is preferred and why §5.3's
test matters more than the constant.

**Frame size is not a protocol contract — verified.** `IpcClient.DryRunStreamAsync` (`:186-300`) loops
`ReadFrameAsync` and `AddRange`s each `DryRunChunkResponse` until `DryRunCompleteResponse`, requiring only
payload ≤ `MaxPayloadBytes`, directory-table parents before children, and global indices.
`docs/specs/architecture-v1.md:247` documents only the 4-byte prefix and the 16 MiB sanity cap. **No
client change, no protocol version bump.** Both constants already have `internal … { get; init; }` test
seams.

Cost at 357k entries: ~88 → ~1,400 frames, ~3,900 extra named-pipe writes at ~5–20 µs each ≈ **~20–80 ms**
on a multi-second run.

### 6.3 Stop pooled carriers pinning strings, and tighten the bound

`PooledEvaluation.Return` (`:139-169`) nulls the evaluation's child references but not the carriers'
strings. Null them **before** the cap check so a *dropped* carrier also stops pinning while still
reachable from the caller's frame:

```csharp
private void ReturnFile(PooledPhysicalFile f)
{
    f.Path = ""; f.Root = "";
    if (_files.Count < MaxRetainedPerKind) _files.Push(f);
}

private void ReturnOp(PooledFileOperation o)
{
    o.Path = ""; o.Root = ""; o.Detail = null;
    if (_ops.Count < MaxRetainedPerKind) _ops.Push(o);
}
```

Worth up to ~6.5 MB held for the remainder of a run (16,384 file + 16,384 op carriers × ~200 B of
unshared path; roots are already interned by `RootInterner`). Also lower `MaxRetainedPerKind`
(`PooledEvaluation.cs:99`) **16,384 → 2,048** — its comment justifies 16,384 as "comfortably above one
~1 MiB chunk's carrier count", a premise §6.2 invalidates (a ~48 KB chunk holds a few hundred carriers).

**Be explicit in the commit that this is a peak fix only.** The pool is per-run and dropped with the run,
so it will not move the 340 MB residual.

### 6.4 Files to touch

- [ ] `src/FileManager.Contracts/IPC/IpcSerializer.cs` — writer-based overloads; keep `byte[]` ones.
- [ ] `src/FileManager.Contracts/IPC/IpcFrameCodec.cs` — reusable 4-byte header (per-connection).
- [ ] `src/FileManager.Core/IPC/IpcServer.cs` — per-connection `ArrayBufferWriter`; guard on
      `WrittenCount`.
- [ ] `src/FileManager.Core/DryRun/DryRunEngine.cs` — `WireChunkByteBudget`; retarget
      `ChunkByteThreshold` / `SpillThresholdBytes`.
- [ ] `src/FileManager.Core/IPC/Handlers/DryRunStreamHandler.cs` — replace `DestinationChunkSize` with
      the byte budget (deleted outright by Stage 3).
- [ ] `src/FileManager.Core/DryRun/PooledEvaluation.cs` — string nulling; `MaxRetainedPerKind`.

---

## 7. Stage 2 — Return the memory to the OS (fixes "stays at 340 MB")

Reducing peak does not reduce RSS on its own; the GC keeps the high-water commit.

### 7.1 A debounced trim coordinator

New `src/FileManager.Core/Observability/MemoryTrimCoordinator.cs` behind
`IMemoryTrimCoordinator { void NoteWork(long units); }`, registered as a singleton in
`EngineComposition.AddEngine`. `DryRunStreamHandler` calls `coordinator.NoteWork(emitted + sweptCount)`
inside a `try/finally` around its body. Trim only when **all** of:

1. accumulated units since the last trim ≥ ~25,000; **and**
2. no work noted for 20–30 s — use the injected `TimeProvider` (the codebase injects it everywhere) so
   it is testable; **and**
3. `GC.GetGCMemoryInfo().TotalCommittedBytes - HeapSizeBytes` ≥ ~32 MB — committed-but-not-live is the
   actual symptom, and it self-gates tiny runs better than any file count; **and**
4. no operation is in flight — expose an in-flight count from `IpcServer` (it already tracks
   `_connections`) or keep the coordinator's own counter incremented on stream start.

What to call — **verified AOT-available**:

```csharp
GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
```

- `GCSettings.LargeObjectHeapCompactionMode` is implemented in NativeAOT (`GCSettings.NativeAot.cs`
  forwards to `RuntimeImports.RhSetLohCompactionMode`).
- `GCCollectionMode.Aggressive` is implemented in `GC.NativeAot.cs` and **validates strictly**:
  `generation != MaxGeneration` → `ArgumentException`, `!blocking` → `ArgumentException`, `!compacting` →
  `ArgumentException`. The form above is the only legal one — use `GC.MaxGeneration`, not a literal `2`.
- **Do two passes.** dotnet/runtime issue #78679 (open) reports that with LOH allocations one aggressive
  collect does not decommit and two are needed; smaller allocations work in one. This codebase's frames
  are almost all LOH, so that is the expected case here. `LargeObjectHeapCompactionMode` reverts to
  `Default` after each blocking GC (documented), so set it again before the second pass.

**Why not a bare `GC.Collect` in `IpcServer.ServeStreamAsync`'s `finally` (`:315-318`):** it cannot
distinguish a 10-file run from a 357k one, it would fire for every future streaming handler, and if it
ever ran while an operation were live it would suspend every managed thread — including up to 256 scan
workers — with LOH compaction becoming O(live LOH) ≈ 100 MB mid-sweep. The debounce plus the in-flight
guard is the entire safety argument. After a run the live set is a few MB, so expect low-tens of ms per
pass.

**Make it a setting.** Add `bool ReleaseMemoryAfterLargeOperations { get; init; } = true;` to
`GlobalSettings`, following that file's established nullable-backing-field + collapse-to-default pattern
(as `ScanThreading` and `ScratchDirectory` do) so an existing `settings.json` stays value-equal under the
record's equality. Bump `SchemaVersion` 5 → 6 and document it in the versioning comment as the file
already does for v2–v5. Default **on** — footprint is the stated priority; someone running back-to-back
dry runs will want it off.

### 7.2 GC configuration — one variable per commit, each measured

**Mechanism, verified against upstream source.** NativeAOT contains no JSON parser for
`runtimeconfig.json`. `RhConfig` resolves config from exactly two sources: `DOTNET_`-prefixed environment
variables and two ILC-embedded blobs. The GC asks the host via `GCToEEInterface`
(`nativeaot/Runtime/gcenv.ee.cpp`), which tries the **private key** (`gcServer`, `GCConserveMemory`) via
env-then-settings-blob (`--runtimeopt:`), then the **public key** (`System.GC.Server`, …) via the knobs
blob **only, no env fallback** (`--runtimeknob:`). `Microsoft.NET.Sdk.targets:572-586` converts four
MSBuild properties into `RuntimeHostConfigurationOption`, and `Microsoft.NETCore.Native.targets` emits
each as `--runtimeknob:<Identity>=<Value>` (the fix from dotnet/runtime #85961 / PR #86068, .NET 8+).

```xml
<PropertyGroup>
  <!-- Workstation GC: do NOT set ServerGarbageCollection. Per-core heaps plus a much larger gen0
       budget is exactly wrong for a mostly-idle service, and it is the only thing that enables DATAS.
       Background GC favours non-compacting concurrent gen2s which, per the docs, "don't reduce the
       total heap size by much"; off => gen2s are blocking + compacting => the heap actually shrinks.
       Cost is longer pauses during the burst, which is an accepted trade here. -->
  <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
</PropertyGroup>
<ItemGroup>
  <!-- No MSBuild property exists for this; the AOT toolchain turns it into
       --runtimeknob:System.GC.ConserveMemory=5. Also auto-compacts a fragmented LOH. -->
  <RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="5" />
</ItemGroup>
```

`publish.ps1` needs no edit — these bake at ILC time. Side benefit: the same
`RuntimeHostConfigurationOption` items also land in the generated `runtimeconfig.json`, so the JIT dev
loop (`dotnet run` / F5) and the shipped AOT binary agree.

**Confidence, stated honestly.** The *delivery mechanism* for both is verified. The *effect* of neither
is: `ConcurrentGarbageCollection=false` is a sound inference from documented behaviour, and
`System.GC.ConserveMemory` has an unresolved report (dotnet/runtime #93914) of a correctly-set value
producing no observable change. **Ship each behind §5.4's measurement and revert either if it doesn't
move the number.**

**Deliberately not set:**

- `RetainVM` — default is already `false` (release segments to the OS). Setting `false` is a no-op;
  `true` is actively wrong here.
- DATAS / `GarbageCollectionAdaptationMode` / `System.GC.DynamicAdaptationMode` — a no-op for Workstation
  GC; the docs scope it to Server GC. (It only becomes interesting as the opposite-direction experiment
  in §10.)
- `GCHeapHardLimit*` — converts a big scan into an OOM for a tool whose workload size the user chooses.
  If ever pursued, prefer `System.GC.HighMemoryPercent`, which makes the GC compact sooner with no hard
  failure mode.
- `GCgen0size` — its public name is `NULL` in `gcconfig.h`, so it is unreachable via any
  `runtimeconfig.json` / `RuntimeHostConfigurationOption` / `--runtimeknob` path on *any* runtime. Env
  var or `--runtimeopt` only, and undocumented.

**Two gotchas.** Booleans in the embedded blob parse as `strcmp(embeddedValue, "true") == 0` —
case-sensitive lowercase; a `True` silently reads as false. And **avoid env vars as the delivery
mechanism entirely**: the service starts from at least two places — `ServiceLauncher.ConnectOrStartAsync`
(`:54-60`, `UseShellExecute = false`, so it *could* inject env) **and** the HKCU `Run` key written by
`WindowsAutostartRegistrar` — so anything delivered by env var silently vanishes on the autostart path.
Bake at ILC time.

### 7.3 Files to touch

- [ ] `src/FileManager.Core/Observability/MemoryTrimCoordinator.cs` — **new**.
- [ ] `src/FileManager.Core/IPC/Handlers/DryRunStreamHandler.cs` — `NoteWork` in a `try/finally`.
- [ ] `src/FileManager.Core/IPC/IpcServer.cs` — expose an in-flight count.
- [ ] `src/FileManager.Contracts/Settings/GlobalSettings.cs` — new setting, `SchemaVersion` → 6.
- [ ] `src/FileManager.Service/EngineComposition.cs` — register the coordinator.
- [ ] `src/FileManager.Service/FileManager.Service.csproj` — the two GC knobs, **separate commits**.
- [ ] `src/FileManager.UI/…` settings surface — expose the new toggle if the settings UI enumerates
      `GlobalSettings` members explicitly rather than reflectively; check before assuming.

---

## 8. Stage 3 — Stream the sweep instead of collecting it (fixes the peak)

> **Implemented 2026-08-01, with a deliberate deviation from §8.1/§8.2 below.** §3.2 was re-verified
> first and holds (see the note at the end of §3.2).
>
> **What was built instead of the spool routing.** `DestinationProjector.SweepStreamAsync` emits
> byte-budgeted `DryRunChunk`s **directly from the walk**, over a 2-deep bounded `Channel<DryRunChunk>`,
> with global `SubjectIndex` values already applied. It does **not** go through `IDryRunSpool` /
> `StreamAccumulator` / `EvaluationCarrierPool`.
>
> **Why.** The spool routing buys three things — spill-to-disk, chunk-bounded memory, carrier recycling
> — and only the middle one is a memory win here. Chunk-bounded memory falls out of the bounded channel
> for free: a full channel parks the walk, which is the same back-pressure the spool would provide.
> Carrier recycling saves allocation *churn*, not live bytes, and §8 already conceded the 357k path
> strings are allocated either way ("they die with their chunk"). Spill-to-disk exists in the source
> phase because the parallel scan+evaluate pipeline must fully drain before chunking can start; the
> sweep has no evaluation phase and no such ordering constraint, so it has nothing to decouple.
>
> Taking the spool route would have meant widening `IEvaluationView.SourceFile`/`SourceOp` to nullable,
> a `SweptFinding` carrier, `IDryRunSpool.WriteAsync(IEvaluationView)`, an optional-source
> `DryRunSnapshotFormat` on both halves, `RentDestinationOnly` plus null-tolerant `Return`, and matching
> guards in `StreamAccumulator`/`BundleUpperBound`/`EstimateBytes` — touching the spilled-replay format
> and the carrier pool, both of which currently serve the source phase correctly — for **no additional
> memory reduction**. The direct-streaming version is ~1 file of new logic and leaves the spool,
> snapshot format and carrier pool untouched.
>
> **Result:** sweep live set is one chunk (~25–30 KB) plus a two-chunk buffer, independent of entry
> count — better than §8's estimated ~6 MB — and the handler's ~23 MB of `with { SubjectIndex = … }`
> copy churn is gone with it. The rest of §8 (nested-root pruning §8.4, exact `Capped` §8.5, handler
> collapse §8.3, the batched path staying sorted §8.8, the doc rewrites §8.7) was implemented as
> written. §8.9's behaviour change stands: a capped streamed sweep now keeps a non-deterministic subset.
>
> **Not done:** `DryRunChunk.SweepCapped` was added as specified, but as a **trailing empty marker
> chunk** rather than a flag on a data chunk — the cap is only known once the walk ends.

### The original plan (unify onto the source phase's pipeline)

Route the streamed sweep through the same `IDryRunSpool` + `StreamAccumulator` + `EvaluationCarrierPool`
the source phase already uses. It inherits spill-to-disk, chunk-bounded memory and carrier recycling, and
the duplication in §3.1's table collapses.

**Estimated effect: sweep live set 145 MB → ~6 MB, independent of entry count** (spool pre-spill buffer +
one accumulator chunk + bounded pool + walk transients), plus ~23 MB of copy churn removed. All 357k path
strings are still *allocated*, but they die with their chunk instead of surviving to gen2 — which is the
difference that shows up in RSS.

**Prerequisite: re-verify §3.2 before starting.**

### 8.1 The shared payload

A swept entry degenerates onto the existing bundle shape cleanly — one bundle, no source, one destination
file, one destination op with bundle-local `SubjectIndex = 0`. New
`src/FileManager.Core/DryRun/SweptFinding.cs`:

```csharp
internal sealed record SweptFinding(PhysicalFile File, VirtualFileOperation Op) : IEvaluationView
{
    IPhysicalFileView? IEvaluationView.SourceFile => null;
    IFileOperationView? IEvaluationView.SourceOp  => null;
    IReadOnlyList<IPhysicalFileView> IEvaluationView.DestinationFiles => [File];
    IReadOnlyList<IFileOperationView> IEvaluationView.DestinationOps  => [Op];
    void IEvaluationView.Recycle() { }
}
```

`StreamAccumulator.Add`'s existing remap then produces the correct **global** `SubjectIndex` for free
(`DryRunEngine.cs:807-811`): `SubjectIndex = op.SubjectIndex >= 0 ? destBase + op.SubjectIndex : -1`, and
`SourceIndex = op.SourceIndex == 0 ? sourceIndex : -1` maps the swept op's `-1` through unchanged.
**No new index arithmetic anywhere** — this is the strongest signal the unification is the right shape.

Plumbing (all `internal`, all in `FileManager.Core.DryRun`; **nothing in `FileManager.Contracts`
changes**):

- `IEvaluationView.SourceFile` / `SourceOp` become **nullable** (`PooledEvaluation.cs:52-56`).
  `FileEvaluation` keeps its non-null concrete members; only the view widens.
- `StreamAccumulator.Add` (`DryRunEngine.cs:789-830`) guards the source half:
  `if (bundle.SourceFile is { } sf) { _sourceFiles.Add(sf); … _globalSourceCount++; }`.
  `BundleUpperBound` (`:858-863`) guards the two source terms. `HasData` (`:787`) already handles
  destination-only chunks.
- `IDryRunSpool.WriteAsync` takes `IEvaluationView` instead of the concrete `FileEvaluation`
  (`IDryRunSpool.cs:19`); `FileDryRunSpool`'s channel element type follows; `EstimateBytes`
  (`FileDryRunSpool.cs:197-210`) guards the source terms.
- `DryRunSnapshotFormat` (`:57-82`, `:118-186`) writes `SourceFile` / `SourceOp` only when present —
  **absence is the discriminator**, so no version byte is needed (the reader already skips
  unknown/missing properties, `:159-163`). `Read`'s reconstruction `e.SourceOp.Path = e.SourceFile.Path`
  (`:169-170`) becomes conditional. The existing "omit a dest op's `Path` when `SubjectIndex >= 0`"
  optimization (`:78`, `:171-183`) applies to every swept record, so one on disk is ~200 B.
- `EvaluationCarrierPool.RentEvaluation` (`:125-134`) must not force a source carrier — add
  `RentDestinationOnly()` or rent the source carriers lazily — and `Return` must tolerate nulls.
- `DryRunChunk` gains an additive `bool SweepCapped = false` so the sweep can report its cap without
  abusing `ScanTruncated`, whose OR-in at `DryRunStreamHandler.cs:149` happens *before* the sweep and so
  cannot carry a sweep signal.

### 8.2 The new streamed entry point

Alongside `Project` / `Sweep` on `DestinationProjector`:

```csharp
public IAsyncEnumerable<Result<DryRunChunk, string>> SweepStreamAsync(
    Profile profile, ISet<NormalizedPath> survivors, bool truncated,
    int maxEntries, int destinationIndexBase,
    DryRunProgressCounters? progress, CancellationToken ct);
```

Internally: create a spool over the run's `EvaluationCarrierPool`; run the walk on a pool thread
(`Task.Run`) whose single `session.Consume()` loop awaits
`spool.WriteAsync(new SweptFinding(file, op), ct)`; `CompleteWritingAsync`; then replay through a
`StreamAccumulator` seeded with `_globalDestCount = destinationIndexBase`, flushing on the wire budget,
and recycling carriers after each `yield` resumes (**I-POOL-RECYCLE**, verbatim from
`DryRunEngine.cs:384-394`).

**All policy stays in `DestinationProjector` and must not move into the shared pipeline:** infra-directory
and temp-file exclusion, never-descend-a-reparse-point, `MaxDepth` deliberately not applied, survivor
membership, `IsUnderAnySource`, mirror→`Deleted` / additive→`Untouched` / reparse→`Unknown`, the
`OnFault`-swallowing policy, volume and drive-class resolution, and the `truncated` suppression gate
(`:79-80`). The existing `ScanSessionOptions` callback seam already enforces that separation — keep it.
Only the *output* pipeline is shared.

### 8.3 Handler collapse

`DryRunStreamHandler.cs:175-249` becomes the same loop body as the file phase (`:113-172`) — extract that
body once as a local function and call it from both phases. Deleted outright:

- `DestinationChunkSize` (`:35`) and its doc (`:33-35`)
- the `sliceFiles` / `sliceOps` / `with { SubjectIndex = … }` copy (`:238-249`)
- the single-shot `estimator.Accumulate([], sweep.Files, [], sweep.Ops, 0, 0, …)` (`:228`)
- the `sweepTask` / `Task.Run` polling block (`:192-216`)

Bonus UX: progress frames now interleave *with* sweep data frames instead of one opaque blocking call.

### 8.4 Nested-root pruning — replaces `Merge`'s dedup, and fixes a latent bug

Streaming cannot afford `Merge`'s `HashSet<NormalizedPath> reported` sized to the whole sweep
(`DestinationProjector.cs:207`; 357k paths ≈ 80 MB, which would defeat the entire exercise). Instead,
**before submitting the roots**, drop any target root equal to or `IsUnder` another
(`NormalizedPath.IsUnder`, `Jobs/Job.cs:101`). The parent's walk already covers the child's subtree, so
the same file set is reported and the duplicate enumeration I/O disappears. This also replaces the
`targetRoots.Count > 1` heuristic (`:185`, `:195-197`) with an exact predicate.

**The latent bug it fixes:** `Merge` dedups by keeping whichever duplicate the sort left first, but
`List<T>.Sort` is an unstable introsort and the comparison is on `Path` only (`:200-201`) — so **today the
`Root` reported for a file under two overlapping target roots is non-deterministic**, and
`Result_is_identical_across_worker_counts` cannot catch it because `:224` compares
`Files.Select(f => f.Path)` and never `Root`. After pruning it is deterministically the **outermost**
root. `Overlapping_target_roots_report_a_shared_file_once` (`:228`) still passes — its
`Assert.Single(result.Ops, o => o.Path == overlap)` *is* the pruning invariant.

### 8.5 `Capped` becomes exact

Keep `SweepBudget.TryReserve` (`:266-284`) as a walk-side early exit, but let the single serial emitter
apply the authoritative count as it writes to the spool (it is the only writer). `Capped` = "the emitter
reached the cap with work still arriving." Same split as today, minus the merge.

### 8.6 Tests

- Rewrite `Result_is_identical_across_worker_counts` (`DestinationProjectorTests.cs:210`) from an
  ordered-sequence comparison to **set equality** across worker counts, and add a sibling asserting the
  narrower guarantees that remain (§8.7).
- New: the streamed sweep produces the same *set* and the same per-entry classification as the batched
  sorted sweep.
- New: `Ops[i].SubjectIndex` resolves to the right global destination file across chunk boundaries.
- New: a spilled sweep returns every rented carrier and leaves the pool bounded — mirror
  `DryRunEngineTests.cs:771-787`.
- New: nested-root pruning reports the outermost `Root` deterministically.
- Keep green: the 5 sweep tests in `DryRunStreamHandlerTests.cs:264-424` (all order-insensitive already).

### 8.7 Documentation that becomes wrong — rewrite it

- `DestinationProjector.cs:26-28` currently says results are *"sorted by path in a final serial merge, so
  the output is deterministic regardless of how the walk interleaved."* Replace with: *`Sweep`/`Project`
  (batched) sorts by path; `SweepStreamAsync` emits in discovery order and the client sorts for display
  (`DryRunViewModel` `ComputeLoad`), matching the source phase. **Still deterministic on the streamed
  path:** the set of reported entries, each entry's classification, each op's `SubjectIndex` pointing at
  its own file's global position, and — new — the `Root` reported for a file under nested target roots
  (the outermost, because nested roots are pruned before the walk). **Explicitly not deterministic:** row
  order, and which subset survives a `Capped` sweep.*
- `DestinationProjector.cs:191-197` (`Merge` summary) — batched-only now; say so.
- `IDryRunEngine.cs:56-64` (`DestinationSweepResult`) — still the batched shape; note it is no longer the
  streamed path's currency.
- `dry-run-pooling-plan.md` §2 describes merged work; leave it as a historical baseline and put the new
  contract in the code.

### 8.8 The batched path is unchanged — and why that is right

`DryRunEngine.SimulateAsync` keeps calling `Project` → `Sweep` → the sorted in-memory `Merge`.

- Its footprint is bounded by construction (12 MiB frame budget, `MaxBatchCandidates = 50_000`,
  `sweepBudget = MaxBatchCandidates - DestinationFiles.Count` at `DryRunEngine.cs:210`) → ≤ ~17 MB. Not
  the reported problem.
- Its sort **is** load-bearing there: `TryAddSweepEntry` drops the tail when the byte budget trips
  (`:223-231`), so sortedness is what makes the kept set reproducible.
- Two paths is not a new asymmetry — it is the **same** split the source phase already has (sorted
  `ConcurrentQueue` + `List.Sort` for batched at `:178-180`, unsorted spool for streamed at `:307-309`),
  documented at `DryRunEngine.cs:36-39`. Making the sweep match that shape *reduces* the number of
  distinct patterns in the file from three to two.
- What stays genuinely shared: the walk seeding, all classification policy, and the budget — extract one
  private `RunWalkAsync(profile, survivors, budget, sink, ct)` parameterized by
  `Func<SweptFinding, CancellationToken, ValueTask>`, exactly the `sink` shape `ScanAndEvaluateAsync`
  already uses (`DryRunEngine.cs:475`). What stays duplicated: ~15 lines of "collect + sort + pair"
  (batched) against ~5 lines of "write to spool" (streamed). That is the right amount.

### 8.9 Behaviour change to call out in the commit message

A capped streamed sweep now keeps a **non-deterministic** 500k subset instead of the alphabetically-first
500k. A downgrade in reproducibility, not in soundness — `DryRunCompleteResponse.Truncated` →
`TruncationNotice` (`DryRunViewModel.cs:2678-2680`) already tells the user the set is partial and that
deletions are suppressed. The source scan's emission order is *already* non-deterministic, so this makes
the sweep consistent with it rather than introducing a new class of behaviour.

### 8.10 Files to touch

- [ ] `src/FileManager.Core/DryRun/SweptFinding.cs` — **new**.
- [ ] `src/FileManager.Core/DryRun/PooledEvaluation.cs` — nullable source on `IEvaluationView`;
      `RentDestinationOnly`; null-tolerant `Return`.
- [ ] `src/FileManager.Core/DryRun/IDryRunSpool.cs` — `WriteAsync(IEvaluationView, …)`.
- [ ] `src/FileManager.Core/DryRun/FileDryRunSpool.cs` — channel element type; `EstimateBytes` guards.
- [ ] `src/FileManager.Core/DryRun/InMemoryDryRunSpool.cs` — follow the interface change.
- [ ] `src/FileManager.Core/DryRun/DryRunSnapshotFormat.cs` — optional source halves; conditional
      reconstruction.
- [ ] `src/FileManager.Core/DryRun/DryRunEngine.cs` — `StreamAccumulator` source guards;
      `BundleUpperBound`; `DryRunChunk.SweepCapped`. **Leave `SimulateAsync`/`ReportBuilder` unchanged.**
- [ ] `src/FileManager.Core/DryRun/DestinationProjector.cs` — `SweepStreamAsync`; extract
      `RunWalkAsync`; nested-root pruning; exact `Capped`; rewrite the ordering docs.
- [ ] `src/FileManager.Core/IPC/Handlers/DryRunStreamHandler.cs` — collapse §8.3.
- [ ] `src/FileManager.Core/DryRun/IDryRunEngine.cs` — `DestinationSweepResult` doc.
- [ ] `tests/FileManager.Core.Tests/DryRun/DestinationProjectorTests.cs` — §8.6.

---

## 9. Stage 4 — Scan concurrency (real throughput risk; gate on a wall-time check)

**9.1** In `src/FileManager.Core/Scanning/ScanThreadResolver.cs`: auto `min(ProcessorCount * 8, 256)` →
**`min(ProcessorCount * 4, 64)`** (`:21-22`), and `ResolvePerDriveCap`'s auto `ProcessorCount * 4` →
**`ProcessorCount * 2`** (`:37`).

The win is **not** thread stacks (§3.5) — it is the up-to-~80 MB of parked/in-flight directory
materialization at `ScanScheduler.cs:197-200`. Enumeration returns scale per **physical device**, not per
core: a single NVMe saturates its queue well below 64 concurrent directory reads, and a spinning disk or
SMB share is actively worse at high concurrency.

Cost: on one fast local volume, roughly nothing measurable — the per-drive cap (`ProcessorCount * 4`) was
already the binding constraint, not the global cap. On a profile spanning >8 volumes the global cap could
now bind where it didn't. Mitigation already exists: `ScanThreadingSettings.MaxScanThreads` is
runtime-configurable and surfaced in the Settings UI, so a user with 12 drives can pin it back.
**Ship only alongside a wall-time comparison from §5.4.**

**9.2** Fix the comment at `ScanThreadResolver.cs:16` to say *reserve*, citing the measured
`SizeOfStackReserve = 1.5 MB` / `SizeOfStackCommit = 4 KB`.

---

## 10. Stage 5 — only if Stages 0–4 leave the number unacceptable

One experiment each, late, each measured independently: `System.GC.HighMemoryPercent` or
`System.GC.HeapHardLimitPercent`; `--runtimeopt:GCgen0size=…`; and Server GC + DATAS as the
opposite-direction experiment (its whole premise is heap size proportional to live data).

---

## 11. Considered and rejected — do not re-raise these

- **A hashed survivor set (`HashSet<UInt128>` of XxHash128; `System.IO.Hashing` is already referenced by
  `FileManager.Core`).** Saves ~7 MB, i.e. 2% of the problem. The reason to reject is the *failure mode*,
  not the probability (~3.7e-28 at 500k): a colliding pre-existing file reads as "a source writes here"
  and is therefore **omitted** from the sweep, so under `SyncMode.Mirror` the user approves a plan that
  deletes a file never shown to them. That is the one direction a deletion preview must not fail in. The
  zero-risk structural alternative, if you want it, is a `HashSet<string>` under the same comparer plus
  `GetAlternateLookup<ReadOnlySpan<char>>()` (net10.0) so `OnFile` can probe from a `stackalloc` buffer —
  exact, no collisions, but 0 MB saved by itself.
- **`AccumulateSurvivors` using `NormalizedPath.FromCanonical` instead of `Create` to avoid a
  `Path.GetFullPath` per destination op.** Destination op paths are built from the **raw profile string**,
  never a canonicalized one: `TargetPathLayout.Resolve` is `Path.Combine(targetRoot, leaf)`
  (`Profiles/TargetPathLayout.cs:37`) called as `layout.Resolve(target.Path)` (`DryRunEngine.cs:975`);
  the rename path is rooted in the same string (`ConflictResolver.cs:196-204`); `Root` is raw
  `target.Path` (`DryRunEngine.cs:1071`); `ProfileValidator.NormalizePaths` (`:119-156`) canonicalizes
  only to *validate* and never writes back; and `DryRunStreamHandler.cs:69` accepts an arbitrary client
  `InlineProfile` that never saw the validator. The sweep meanwhile probes with canonical strings
  (`DestinationProjector.cs:123`). So a target written as `D:\out\`, `d:/out`, `D:\out\.\`, or an 8.3
  short name would make **every file under that target a Mirror `Deleted` orphan**. `Create`'s
  `Path.GetFullPath` is exactly what repairs that. **Keep `Create`.**
- **Splitting `FileSystemEntry` into (directory, name) to drop the `ToFullPath()` allocation.** ~44 MB
  standalone, but Stage 3 leaves only ~6 MB live so there is nothing left to compact. It is also the
  riskiest item on the list: it changes a public record shape (`Files/FileSystemEntry.cs:20-27`) and ~14
  call sites and tests, and it cannot represent `EnumerateRoots`' synthetic entries, whose `FileName` is
  a *display name* ("Home", `FileSystemService.cs:172`) rather than the last path segment — so
  `FullPath => Path.Join(Directory, FileName)` would silently produce a wrong path there. Its saving also
  inverts below ~3 files per directory.
- **External merge sort of a spilled sweep.** This is Stage 3 plus the expensive half — a run-file
  protocol, a k-way merge, tens of MB of scratch I/O, new corruption and disk-full paths, ~400 lines. You
  only need it if the streamed sweep must come out sorted, and §3.2 says it must not. **If §3.2 turns out
  not to hold, this is the fallback** — and the cheaper knob for extreme trees is a configurable sweep
  bound below `MaxStreamedFiles` (`DryRunEngine.cs:73`), trading completeness for memory.
- **Sorting schemes that don't reproduce the comparator.** If a sorted stream is ever needed, note that
  `(directory, fileName)` tuple order is **not** `string.Compare(fullA, fullB, OrdinalIgnoreCase)`:
  full-path order puts `C:\a b\c.txt` before `C:\a\z.txt` (position 4, `0x20 < 0x5C`) while tuple order
  puts `C:\a` first as a prefix. Truncated fixed-width sort keys and per-directory-sort-plus-k-way-merge
  fail similarly (`C:\a\a.txt` < `C:\a\b\y.txt` < `C:\a\x.txt` breaks directory grouping). The only exact
  scheme is a fragment-wise comparator over the virtual sequence `dir + '\' + name` — exact *because*
  `OrdinalIgnoreCase` folds each UTF-16 code unit independently with no contextual collation — and it
  would need a property test against the reference `string.Compare`.
- **`[MemoryDiagnoser]` as the gauge.** BenchmarkDotNet's `Allocated` column is per-op allocation, not
  retention (`DryRunViewModelMemoryTests.cs` says so in its own comments, and
  `FileHasherBenchmarks.cs:162` carries the same caveat). Useful for allocation *rate*, not for this
  question.
- **Lowering `MaxStreamedFiles` or the sweep budget to cap memory.** Silently degrades the preview a user
  approves a destructive job from.
- **Overlapping the sweep with the source pass** (`docs/dry-run-perf-optimizations.md` item 3) — trades
  memory for latency, the wrong direction here.
- **Pooling `IpcFrameCodec`'s read path** (`:78`) — changes a public `Result<byte[], string>` contract for
  no service-side benefit.
- **Moving the sweep inside `SimulateStreamAsync` as an engine phase 3.** It would remove the
  `destinationIndexBase` hand-off and collapse two `AccumulateSurvivors` callers to one, but it changes
  `IDryRunEngine`'s contract (`IDryRunEngine.cs:23-24` explicitly makes the sweep the handler's job),
  drops a DI dependency, and relocates 5 tests. Stage 3 captures the whole memory win with roughly a
  third of the churn. Treat this as a separate follow-up refactor, not part of this work.

---

## 12. Definition of done (per stage)

- [ ] `dotnet build File-Manager.slnx` clean, no new warnings.
- [ ] `dotnet test File-Manager.slnx` all green. Suites that guard this code:
      `DryRunEngineTests` (including `Spilled_stream_returns_every_rented_carrier_to_the_pool`, `:771-787`),
      `DestinationProjectorTests`, `DryRunStreamHandlerTests`, `DryRunSpaceEstimatorTests`,
      `FileSystemServiceTests`, `ScanSchedulerTests`, `IpcServerStreamingTests`,
      `IpcServerOversizedResponseTests`.
- [ ] New tests added per the stage's own test section.
- [ ] **§5.5's table updated with before/after numbers from §5.4.** A stage with no numbers is not done.
      If a change doesn't move the number, revert it rather than ship a neutral diff.
- [ ] Ground rules (§4) preserved — state explicitly in the commit how each was checked.
- [ ] Stays AOT/trim-clean — no new IL trim warnings from
      `dotnet publish src/FileManager.Service -r win-x64 -c Release` (run from a VS Developer
      environment; the native toolchain is required).
- [ ] Stage 4 only: a wall-time comparison showing no material scan regression.

### End-to-end check (a human drives the UI)

Run the Downloads → Documents dry run and confirm the preview is unchanged — same source-file and orphan
counts, same kinds/pills, same space projection — and that search, facet filtering, header sort and both
`TreeDataGrid` tabs behave identically. Then re-run with a **Mirror** profile and confirm the `Deleted`
orphan set is identical to a pre-change run. **That is the assertion that matters most**, because Stage 3
changes how orphans are enumerated and deduped, and a wrong answer there is a destructive-plan bug rather
than a cosmetic one.
