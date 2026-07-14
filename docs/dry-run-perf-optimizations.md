# Dry-run performance optimizations — implementation handoff

**Audience:** the coding agent implementing this. You are expected to read the referenced files
before touching them; every file/line reference below was accurate at authoring time but confirm it.

**Scope:** six independent-ish optimizations to the dry-run pipeline (`DryRunEngine` and its
collaborators). This plan covers items **2, 3, 5, 6, 7, 8** from a prior analysis. (Items 1 — a
persisted hash cache — and 4 — streaming early-exit compare — are intentionally **out of scope**.)

---

## Ground rules (apply to every item)

These are load-bearing invariants of the dry-run engine. A change that breaks any of them is wrong
even if it's faster.

1. **Read-only (`I-DRYRUN-RO`, spec §8).** Every collaborator here only enumerates, stats, hashes,
   and probes existence. No mutation, ever. Do not introduce any write/create/delete.
2. **Deterministic output ordering.** The report is a bipartite graph where operations reference
   files by integer index. A source file's index is its position after candidates are **sorted by
   source path** (`OrdinalIgnoreCase`); destination sweep output is sorted by path in a serial merge.
   A truncated report must be a valid **prefix** — no retained operation may reference a dropped file.
   Any restructuring must preserve "sort, then assign indices."
3. **Cancellation semantics.** A cancelled run resolves to `Result.Canceled()` (batched) or throws
   `OperationCanceledException` from the async enumerator (streaming) — never a misleading partial
   success. See `DryRunEngine.cs:148` and `:195`.
4. **Fault semantics.** A **Fatal** enumeration fault aborts the run (`"scan failed: …"`); a
   **Warning** is logged and skipped.
5. **AOT-clean.** `FileSystemService` and the serialization surface must stay reflection-free
   (see the note at `FileSystemService.cs:9-14`). No `JsonSerializer` reflection overloads; use the
   source-generated `FileManagerJsonContext`.
6. **Validate with the existing benchmark harness.** There are BenchmarkDotNet projects:
   `benchmarks/FileManager.Core.Benchmarks/DryRun/DryRunEngineBenchmarks.cs`,
   `…/Placement/FileHasherBenchmarks.cs`, `…/Watching/SourceScannerBenchmarks.cs`. **Every item below
   must land with before/after numbers from the relevant benchmark**, plus the two workload shapes
   that stress different code (see "Measure two scenarios").

### Measure two scenarios

The dominant cost flips depending on state, so benchmark both:

- **First preview** — target trees empty. No hashing; cost is enumeration + existence probes.
- **Re-preview** — target trees already populated with same-size files. Cost is hashing (source +
  target read in full per file).

The engine already logs a per-phase breakdown (`scan ms / eval ms / probes / stats / files+bytes
hashed`) at `DryRunEngine.cs:232` — use it to confirm which phase your change actually moved.

### Suggested sequencing / dependencies

```
#6 (collapse stats)      ── safe, do first, no dependencies
#5 (concurrent hashing)  ── small, independent; benchmark-gated
#2 (pipeline scan→eval)  ── architectural; do before #3
#3 (overlap sweep)       ── architectural; builds on #2's structure; riskiest; do LAST; benchmark-gated
#7 (zero-alloc enum)     ── independent (FileSystemService); medium
#8 (serialization)       ── independent (ReportBuilder); lowest priority
```

Land each as its own commit/PR so wins are attributable and any regression is easy to bisect.
(Repo convention for multi-line commit messages: write the message to a file and
`git commit -F <path>` — do not use inline `-m` or shell heredocs.)

---

## Item 6 — Collapse redundant stats on existing targets  *(do first; low risk)*

### Problem
For an existing overwrite target, the pipeline stats the same file up to 3–4 times:
- `EvaluateTargetAsync`: `File.Exists(prospectivePath)` (`DryRunEngine.cs:832`) **+**
  `FileMetadataReader.Read(prospectivePath)` → `new FileInfo(...)` (`:840`). Two stats.
- `ConflictResolver.Probe` then re-stats: `File.Exists(desiredFinalPath)` (`ConflictResolver.cs:143`)
  **+**, for `OverwriteIfNewer`, `File.GetLastWriteTimeUtc(desiredFinalPath)` (`:152`). The metadata
  it needs was already read upstream.

Each stat is a syscall; on a network share it's a round-trip. This is pure waste.

### Change (two parts)

**Part A (definite win, fully safe): stop `Probe` re-stat'ing.**
Change the `Probe` signature to accept the existence + mtime the caller already knows:

```csharp
// ConflictResolver.cs  (IConflictResolver.cs too)
public Result<ConflictOutcome, JobError> Probe(
    string desiredFinalPath, ConflictResolution policy, DateTimeOffset incomingLastWriteUtc,
    bool desiredFinalExists, DateTimeOffset existingLastWriteUtc)
```
- Replace `File.Exists(desiredFinalPath)` at `:143` with the `desiredFinalExists` parameter.
- Replace `File.GetLastWriteTimeUtc(desiredFinalPath)` at `:152` with `existingLastWriteUtc`.
- **Do NOT touch `ProbeRenameSuffix`** (`:194`): its `File.Exists(candidate)` calls probe *suffixed*
  candidate paths that the caller has not stat'd — those are genuinely required. Leave them.
- Caller (`DryRunEngine.EvaluateTargetAsync`, around `:905`) passes `finalExists` and
  `existingMeta?.LastWritten ?? default`.

**Part B (stretch: one stat instead of two in `EvaluateTargetAsync`).**
Only do this if you preserve the "existing-but-unreadable target" behavior. Today: `File.Exists`
returns true, `FileMetadataReader.Read` fails → `existingMeta` null, and the code still treats the
file as existing (falls through to the conflict probe → Overwrite). If you drop `File.Exists` and
infer existence from `Read`, a read failure must NOT be silently reclassified as "doesn't exist →
New" (that would flip an Overwrite preview to New).

Prerequisite: extend `FileMetadataReader.Read` to distinguish **not-found** from **unreadable**
(e.g., return a nullable / a typed result where `null` means not-found and an error value means
exists-but-unreadable). Then in `EvaluateTargetAsync`:
- one `Read`; not-found → `finalExists = false`; unreadable → `finalExists = true`,
  `existingFile = null` (current behavior); success → `finalExists = true` with metadata.
- Note `FileInfo.Exists` is `false` for a directory at that path, matching `File.Exists` — no
  behavior change there.

If Part B's edge handling looks risky in review, **ship Part A alone** — it's the bulk of the win.

### Tests / validation
- Keep `DryRunEngineTests` green (Overwrite / OverwriteIfNewer / New / Skip paths).
- Add a test asserting an `OverwriteIfNewer` decision is driven by the passed-in mtime (no reliance
  on a live re-stat).
- Benchmark on a network-like path if available; otherwise count stats via a fake `IFileSystemService`
  / instrumented `FileMetadataReader` in a unit test.

---

## Item 5 — Hash source and target concurrently  *(small; benchmark-gated)*

### Problem
`EvaluateTargetAsync` awaits the source hash, then the target hash, sequentially
(`DryRunEngine.cs:873` then `:884`). For the first hashing target of a file, these two full-file
reads could overlap.

### Change
Only relevant when `cachedSourceHash is null` (first target that triggers hashing; subsequent targets
reuse the cache — do **not** re-read the source). When the source hash is not yet cached, start both
hashes and await together:

```csharp
if (cachedSourceHash is null)
{
    var srcTask = hasher.HashFileToBytesAsync(sourcePath, method, ct);
    var tgtTask = hasher.HashFileToBytesAsync(prospectivePath, method, ct);
    await Task.WhenAll(srcTask, tgtTask).ConfigureAwait(false);  // guard: WhenAll rethrows first fault
    // handle IsCanceled / TryGetError on each exactly as the current sequential code does
}
else
{
    // existing single target-hash path, reusing cachedSourceHash
}
```
Preserve every existing branch: `IsCanceled` → `Unknown "canceled"`; source hash error → `Unknown
"could not hash the source"`; target hash error → `Unknown "could not hash the existing target"`.
Update `RunCounters` (`CountHash`) for both reads as today.

### The catch (this is why it's benchmark-gated)
The evaluation phase already runs `EvaluationBatchSize`/`maxConcurrency` files concurrently via
`Parallel.ForEachAsync` (`DryRunEngine.cs:176`). If the disk is already saturated by cross-file
parallelism, adding intra-file concurrency buys nothing and may add seek contention on spinning
disks. **Measure re-preview throughput on your real target medium before keeping this.** If it
doesn't move (or regresses), revert — the diff is intentionally tiny and isolated so that's cheap.

---

## Item 2 — Pipeline scan → evaluation (remove the phase barrier)  *(architectural)*

### Problem
Phase 1 fully drains **and sorts** the scan into `List<Payload> candidates` before Phase 2 begins
evaluating (`DryRunEngine.cs:117-193`, mirrored in the stream path at `:295-366`). So **no hashing
starts until the entire tree is enumerated** — the two dominant I/O costs run back-to-back instead of
overlapping. On a network source this is very visible.

The sort exists only to assign deterministic indices, and that must happen only **before final
assembly**, not before evaluation. Evaluation (`EvaluateFileAsync`) is already stateless and
read-only, so it can run on payloads as they stream in, unordered, then be ordered at the end.

### Change — producer/consumer pipeline
Restructure both `SimulateAsync` and `SimulateStreamAsync` so scan and evaluation overlap:

1. **Producer:** the existing `scanner.Scan(...)` (already a streaming, multi-threaded source).
2. **Bounded work buffer:** feed payloads into a bounded `Channel<Payload>` (or reuse the
   `BlockingCollection` pattern already established in `SourceScanner`). Bound it so a huge tree
   can't balloon memory ahead of evaluation.
3. **Consumers:** a fixed set of evaluation workers (`ResolveWorkers(profile)` count) each pulling
   payloads and running `EvaluateFileAsync`, collecting `FileEvaluation` results into a concurrent
   collection (or per-worker lists, mirroring `DestinationProjector`'s per-worker sinks to avoid
   contention).
4. **Barrier → sort → assemble:** once the scan completes and the buffer drains, **sort the
   evaluated results by `SourcePath`** and feed them into `ReportBuilder` (batched) /
   `StreamAccumulator` (streaming) exactly as today. Index assignment stays here, unchanged.

### Invariants to preserve (critical)
- **File cap = the count bound.** Keep accepting at most `MaxReportedFiles` (batched) /
  `MaxScannedCandidates` (streaming) payloads; when the scan yields more, set `truncated` and stop
  feeding. NB: scan emission order is *already* non-deterministic (the scanner is parallelized), so a
  truncated run *already* keeps a nondeterministic subset that is then sorted — you are not
  regressing determinism, you're preserving today's exact semantics.
- **Fatal fault** dequeued from the scan → abort (batched: return `"scan failed"`; streaming:
  `yield return` the failure then `yield break`). **Warning** → log + skip.
- **Cancellation** → `Canceled` (batched) / OCE from the enumerator (streaming). Ensure a cancelled
  scan tears the consumers down and does not deadlock the buffer (cancel, drain, then dispose — the
  `SourceScanner.WalkParallel` teardown is the reference pattern).
- **`RunCounters`** stays interlocked (already is).

### Known tradeoff (document it in the PR)
The batched path currently stops evaluating once the **byte budget** trips (`!builder.Truncated`
loop guard at `:172`), capping wasted eval to one batch. A pipeline evaluates everything up to the
**file cap** before assembly, so a report that truncates on *bytes* (≈24k files at ~500 B each, below
the 50k file cap) will over-evaluate the dropped tail — bounded, but real. The **streaming path has
no byte budget** (it chunks), so pipelining it is pure win with no waste. Options:
- Accept the bounded batched over-eval (simplest; the byte-truncated case is an edge case).
- Or interleave a lightweight "assembly caught up and truncated → stop feeding" signal back to the
  producer. Only add this if benchmarks show the over-eval matters.

### Tests / validation
- `DryRunEngineTests` must stay green, including truncation tests (verify a truncated report is still
  a valid path-ordered prefix and no op references a dropped file).
- Add a test that a slow scanner (fake with injected delay per payload) overlaps with evaluation —
  e.g., assert total time < scan-time + eval-time (loosely), or assert evaluation started before the
  scan finished via instrumentation.
- Benchmark **first-preview on a high-latency source** (this is where the win concentrates).

---

## Item 3 — Overlap the destination sweep with the source pass  *(architectural; riskiest; do last)*

### Problem
`DestinationProjector.Sweep` runs strictly **after** evaluation (`DryRunEngine.cs:215`, and in the
streaming path the `DryRunStreamHandler` runs it after consuming the engine stream). Yet the
expensive part of the sweep — **enumerating the target tree** — does not depend on the survivor set.
Survivors (destination paths a source writes to) only *filter* which enumerated files get reported.

### What the sweep needs, and when
Known **upfront** (no evaluation needed): the target roots, the source roots (for `IsUnderAnySource`,
`DestinationProjector.cs:332`), the `mirror` flag, and the infra/temp/reparse exclusions. The **only**
input that comes from evaluation is `survivors` (the set of destination-op paths, gathered via
`AccumulateSurvivors`).

### Change — concurrent enumeration, deferred survivor filter
Start the target-tree walk concurrently with scan+eval, applying every filter that is knowable
upfront, and defer only the survivor-membership test to the end:

1. Kick off the projector walk as a `Task` alongside Phase 1/2. It runs the existing work-stealing
   walk but, per file, applies infra/temp/reparse exclusion **and `IsUnderAnySource`** live (all
   upfront-knowable), buffering the survivors-candidates it cannot yet classify.
2. When evaluation completes, the survivor set is final. Apply `survivors.Contains` + the sort +
   merge (`DestinationProjector.Merge`) to the buffered candidates to produce the final result.
3. Wire the concurrent result into `SimulateAsync` where `Project(...)` is called today, and into the
   streaming handler analogously.

### Hard constraints — read carefully
- **Suppression on source truncation.** If the source pass truncated, the sweep result **must be
  discarded** (`Sweep` already early-returns empty when `truncated`, `DestinationProjector.cs:85`).
  Overlapping means you may have done the walk before knowing truncation happened → **wasted work.**
  This is the primary risk. Mitigate by only overlapping when truncation is unlikely, or accept the
  wasted walk as the cost of the overlap and document it.
- **Memory bound.** In a tight full-sync, survivors ≈ most of the destination tree, so the deferred
  buffer holds nearly the whole tree until the final filter. **Cap the buffer** (reuse / extend the
  `maxEntries`-style budget in `SweepState`); if the cap is exceeded, fall back to running the sweep
  sequentially after eval (the current behavior). Never allow unbounded buffering.
- **Determinism** of the merged output is unchanged (still a sorted serial merge).
- **Streaming path:** the sweep is owned by `DryRunStreamHandler`, which accumulates survivors as it
  consumes engine chunks and cannot finalize until the stream ends (it needs the complete survivor
  set + the `ScanTruncated` flag). You can *pre-warm* the enumeration there, but the same
  truncation-waste and memory caveats apply.

### Recommendation
Implement this **after #2** (it reuses the same "overlap I/O, order at the end" machinery), put it
behind the buffer-cap fallback, and **gate shipping it on a benchmark** showing the overlap beats the
wasted-walk + buffering cost on a realistic tree. If it doesn't clearly win, leave the sweep
sequential — this is the one item where "correct but slower" (the status quo) may beat "faster but
wasteful."

### Tests / validation
- `DestinationProjectorTests` green (orphan classification, overlapping roots dedup, budget,
  `FromCanonical` equivalence).
- New test: overlapped sweep produces byte-identical result to the sequential sweep for the same
  tree + survivor set.
- New test: source truncation discards the sweep even when the concurrent walk already ran.
- New test: buffer-cap exceeded → falls back to sequential, still correct.
- Benchmark: full-tree preview where target has many genuine orphans (overlap should win) vs. a tight
  sync where it mostly buffers-then-discards (watch for regression).

---

## Item 7 — Zero-allocation directory enumeration  *(independent; medium effort)*

### Problem
`FileSystemService.EnumerateEntries` uses `DirectoryInfo.EnumerateFileSystemInfos()`
(`FileSystemService.cs:29`), which allocates a `FileInfo`/`DirectoryInfo` **per entry**. On a
multi-million-file tree that's heavy Gen0/Gen2 pressure and throughput loss.

### Change
Rewrite `EnumerateEntries` over `System.IO.Enumeration.FileSystemEnumerable<TResult>` (or a
`FileSystemEnumerator<TResult>` subclass), whose transform receives a
`ref System.IO.Enumeration.FileSystemEntry` — a ref struct read straight from the OS find-data with
**zero per-entry allocation**, exposing everything the domain `FileSystemEntry` needs:
`FileName` (span), `IsDirectory`, `Length`, `LastWriteTimeUtc`, `CreationTimeUtc`, `Attributes`, and
`ToFullPath()`.

- **Name collision:** the domain type `FileManager.Core.Files.FileSystemEntry`
  (`Files/FileSystemEntry.cs:20`) collides with `System.IO.Enumeration.FileSystemEntry`. Alias one,
  e.g. `using SysEntry = System.IO.Enumeration.FileSystemEntry;`.
- **Single level only.** The caller (both `SourceScanner` and `DestinationProjector`) does its own
  recursion, so set `RecurseSubdirectories = false`. Do **not** recurse inside the enumerator.
- **AOT-clean** — this API is reflection-free, keep it that way.

### Semantics you MUST preserve (landmines)
1. **Time zone.** The current code fills `FileSystemEntry.Modified` from `current.LastWriteTime`
   (**local**), and `Created` from `current.CreationTimeUtc` (**UTC**). The BCL ref struct exposes
   `LastWriteTimeUtc`/`CreationTimeUtc`. Reproduce the existing field values **exactly** (convert UTC
   → local for `Modified` if you keep the current contract), or you'll shift timestamps downstream in
   `SourceScanner.MetadataFrom` and the filter input. Confirm what `MetadataFrom` expects before
   changing anything; if the local-time `Modified` is actually a latent bug, fix it deliberately in a
   separate commit with its own test — not silently here.
2. **Fault mapping** — this is the tricky part. The current contract:
   - Directory cannot be opened (missing / access denied on the root) → a **single Fatal** fault,
     then stop (`:26-27`, `:59`).
   - An error mid-enumeration → **Fatal**, terminal (a throwing enumerator is spent, `:55-59`).
   - A per-entry mapping issue → **Warning**, continue (`:87-97`).

   `FileSystemEnumerable` swallows per-entry errors by default and surfaces open/iteration errors via
   an error handler. To keep the exact contract you will likely need to **subclass
   `FileSystemEnumerator<TResult>` and override `OnError(int error)`** to translate Win32 error codes
   into the Fatal-vs-Warning distinction, and yield the fault through your `TResult`
   (`Result<FileSystemEntry, EnumerationFault>`). Map "directory not found / access denied at open" →
   Fatal; keep the "warning, continue" path for recoverable per-entry conditions.
3. **`yield` + `try/catch`** — the current manual-enumerator dance exists because you can't `yield`
   inside `try/catch`. Preserve non-throwing behavior: the method must never throw to its caller; all
   errors become yielded faults.

### Tests / validation
- `FileSystemServiceTests` is the contract — **keep it green**. If it doesn't already cover the three
  fault cases above, add tests for them *before* rewriting (characterization tests), then make them
  pass against the new implementation.
- Benchmark `SourceScannerBenchmarks` on a large tree; report allocations (BenchmarkDotNet
  `[MemoryDiagnoser]`) before/after — the win is primarily allocation/GC, secondarily throughput.

---

## Item 8 — Avoid double-serializing report records near the byte budget  *(lowest priority)*

### Problem
`ReportBuilder` (`DryRunEngine.cs:502-606`) enforces the 12 MiB frame budget with a two-mode scheme:
a cheap **upper-bound estimate** (fast path, `StringUpperBound` = 6 bytes/char, `:667`), falling back
to **exact JSON serialization** of each record once the upper bound could cross the budget
(`TryReserve` → `ExactBytes`, `:544-603`). Then the whole `DryRunReport` is serialized **again** by
`IpcSerializer.SerializeResponse` (`IpcSerializer.cs:16`, via `DryRunHandler.cs:34`) to go on the
wire. Records near the cap are serialized twice, plus a one-time O(n) re-measure at the mode switch
(`ExactTotalOfKept`, `:571`).

This only bites for reports that approach 12 MiB (most never do), which is why it's lowest priority.

### Recommended change — single tight upper-bound estimator (eliminates the exact path)
Replace the "6×/char upper bound → exact serialization fallback" with **one tight upper-bound
estimator** so the exact-serialization path (and thus the double serialize and the O(n) switch)
disappears entirely:

- For each string, compute `Encoding.UTF8.GetByteCount(s)` (exact UTF-8 size, no allocation) plus a
  **bounded JSON-escape allowance** that keeps it a true upper bound. A correct tight bound:
  `utf8Bytes + escapableCharCount * 5 + 2` where `escapableCharCount` is counted in the same single
  pass (chars `"`, `\`, and control chars `< 0x20` are the only ones JSON escapes). This is O(len)
  and far cheaper than JSON-serializing the record.
- Delete `ExactBytes`, `ExactTotalOfKept`, `_exact`, the `ArrayBufferWriter`/`Utf8JsonWriter` fields,
  and the mode switch. `TryReserve` becomes a single `_total + estimate <= budget` check.

**Tradeoff to document:** a tight *upper* bound still slightly over-estimates (the escape allowance),
so a near-cap report may truncate a handful of records **earlier** than the current exact scheme —
never later, so the frame-fit guarantee holds. If a test asserts an exact truncation count, it will
need updating to the new boundary; that's expected, not a regression.

### Alternative (only if exact truncation fill must be preserved)
Have `ReportBuilder` retain the bytes it serializes and hand a **pre-serialized payload** to the IPC
layer so the records aren't serialized twice. This is more invasive — the wire format wraps everything
in the polymorphic `IpcResponse` envelope with a `"type"` discriminator (`IpcSerializer.cs:10-11`), so
you'd need a response variant that carries raw pre-serialized JSON and splices it into the envelope
without re-encoding. Only pursue this if the tight-estimator's earlier truncation is unacceptable.

### Reality check
Prefer the **streaming path** (`SimulateStreamAsync`) for large results in general — it has no byte
budget and never hits this code. If most large dry runs already go through streaming, #8's practical
value is small; size the effort accordingly.

### Tests / validation
- `DryRunEngineTests` truncation tests green (update the exact boundary count if you take the
  tight-estimator route — verify the report still fits the frame cap, which is the real guarantee).
- Add a test with multi-byte / escape-heavy paths (non-ASCII, embedded quotes/backslashes) asserting
  the estimate remains ≥ the actual serialized size (upper-bound property).
- Benchmark a near-cap batched report before/after.

---

## Definition of done (per item)

- [ ] Builds clean (`dotnet build`), no new warnings.
- [ ] All existing tests green; new tests added per the item's "Tests / validation".
- [ ] Before/after benchmark numbers in the PR description, for **both** workload scenarios where
      relevant.
- [ ] Ground-rule invariants (read-only, determinism, cancellation, faults, AOT) preserved —
      call out explicitly in the PR how each was checked.
- [ ] Benchmark-gated items (#5, #3, and the #7 alloc claim) include the measurement that justifies
      keeping the change; if it doesn't win, revert rather than ship a neutral/negative diff.
