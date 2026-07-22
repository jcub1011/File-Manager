# Dry-run streaming: entry-object pooling (implementation plan)

> Hand-off spec for a fresh coding agent. Self-contained — assumes no prior conversation.
> Baseline: branch `feature/foundation`, at/after commit **`78646d3`**
> ("Stream dry-run findings in discovery order via a disk-backed spool").

## 1. Goal

Reduce GC pressure on the **dry-run streaming path** of the file-manager service by **pooling the
per-file "entry" objects** that flow through the disk-backed snapshot round-trip, instead of allocating
a fresh set for every file on read-back. The win targets **large scans that spill to disk** (hundreds of
thousands of files); small scans already avoid the cost (see §3) and must not regress.

This is a follow-up to work that is already merged. **Read §2 to understand what already exists before
changing anything.**

## 2. Current architecture (already implemented — do not redo)

The service (`FileManager.Service`, backed by `FileManager.Core`) previews a profile and streams the
findings to an Avalonia UI (`FileManager.UI`) over a named-pipe IPC channel. Only `FileManager.Contracts`
crosses the process boundary.

### The streaming pipeline (server side)

`DryRunEngine.SimulateStreamAsync` (`src/FileManager.Core/DryRun/DryRunEngine.cs`) does:

1. A fused scan+evaluate pipeline (`ScanAndEvaluateAsync`) runs a bounded channel of scanned payloads
   through a `Parallel.ForEachAsync` worker pool. Each worker calls `EvaluateFileAsync`, producing one
   **`FileEvaluation`** per source file, and hands it to a **sink** callback
   (`Func<FileEvaluation, CancellationToken, ValueTask>`).
2. **Streamed path sink = a spool.** Findings are written **in discovery/completion order — no global
   sort** — to an `IDryRunSpool`. After the pipeline completes, the engine calls
   `spool.CompleteWritingAsync()` then replays with `spool.ReadAllAsync(ct)`, feeding a
   `StreamAccumulator` that buffers records and flushes a `DryRunChunk` each time the running upper-bound
   size crosses `ChunkByteThreshold`.
3. **Batched path** (`SimulateAsync`, used by the CLI) collects the same `FileEvaluation`s into a
   `ConcurrentQueue`, **sorts them by source path**, and builds one `DryRunReport`. This path is
   unchanged and MUST stay sorted (its file index == sorted position). Do not touch it.

The client (`IpcClient.DryRunStreamAsync` → `IpcGateway` → `DryRunViewModel`) reassembles the chunks and
**sorts them for display** itself (`DryRunViewModel` tab `ComputeLoad`), so discovery-order arrival is
fine.

### The spool (already implemented)

`src/FileManager.Core/DryRun/IDryRunSpool.cs`:

```csharp
internal interface IDryRunSpool : IAsyncDisposable
{
    ValueTask WriteAsync(FileEvaluation evaluation, CancellationToken ct);   // called concurrently
    ValueTask CompleteWritingAsync();
    IAsyncEnumerable<FileEvaluation> ReadAllAsync(CancellationToken ct);
}
internal interface IDryRunSpoolFactory { IDryRunSpool Create(); }
```

- `FileDryRunSpool` (`FileDryRunSpool.cs`): a single writer task drains a bounded channel. Findings stay
  in an **in-memory `List<FileEvaluation>`** until their estimated size exceeds a **spill threshold**
  (`ChunkByteThreshold`, 1 MiB); at that point everything buffered — and every later finding — is
  written append-only to a per-run `dryrun-*.snapshot` file (length-prefixed JSON via
  `DryRunSnapshotJsonContext`, source-generated / AOT-safe). Framing already uses `ArrayPool<byte>` on
  read and a **reused `Utf8JsonWriter` + `ArrayBufferWriter`** on write, so byte buffers are already
  pooled. The file is deleted on `DisposeAsync`.
- `InMemoryDryRunSpool` (`InMemoryDryRunSpool.cs`): a plain `List`, for tests.
- The engine picks the spool via `DryRunEngine.CreateSpool()`: an internal test seam `SpoolFactory`
  (null in production → builds a `FileDryRunSpool` from `settings.Current.ScratchDirectory` and
  `SpillThresholdBytes`). Tests set `SpoolFactory = new InMemoryDryRunSpoolFactory()`.
- `EngineHost` purges leftover `*.snapshot` on startup; `ScratchDirectory` is a `GlobalSettings` field
  (defaults to `scratch/` in the working directory) with a Settings UI folder picker.

### The entry types

- **`FileEvaluation`** (`src/FileManager.Core/DryRun/FileEvaluation.cs`, `internal sealed record`):
  ```csharp
  internal sealed record FileEvaluation(
      PhysicalFile SourceFile,
      VirtualFileOperation SourceOp,
      IReadOnlyList<PhysicalFile> DestinationFiles,
      IReadOnlyList<VirtualFileOperation> DestinationOps);
  ```
- **`PhysicalFile`** and **`VirtualFileOperation`** (`src/FileManager.Contracts/DryRun/DryRunReport.cs`,
  `public sealed record`s, immutable init-only props). `PhysicalFile` = { Path, Root, Length,
  LastWritten, IsReparsePoint }. `VirtualFileOperation` = { Path, Root, Kind, SourceIndex=-1,
  SubjectIndex=-1, SourceDisposition?, Detail? }. These are the engine's absolute-path "currency"; they
  never cross the wire (the normalized `DryRunFile`/`DryRunOperation` do).
- **`DryRunChunk`** (`src/FileManager.Core/DryRun/IDryRunEngine.cs`, `public sealed record`):
  ```csharp
  public sealed record DryRunChunk(
      IReadOnlyList<PhysicalFile> SourceFiles,
      IReadOnlyList<PhysicalFile> DestinationFiles,
      IReadOnlyList<VirtualFileOperation> SourceOperations,
      IReadOnlyList<VirtualFileOperation> DestinationOperations,
      bool ScanTruncated = false);
  ```

### Who consumes a `DryRunChunk` (all read-only; none retain the objects past the chunk)

`DryRunStreamHandler` (`src/FileManager.Core/IPC/Handlers/DryRunStreamHandler.cs`), per yielded chunk:
- `DryRunSpaceEstimator.Accumulate(sourceFiles, destinationFiles, sourceOps, destinationOps, sourceBase, destBase, stageOverwrites)` — `src/FileManager.Core/DryRun/DryRunSpaceEstimator.cs:43`. Reads props only.
- `DestinationProjector.AccumulateSurvivors(ISet<NormalizedPath> survivors, IReadOnlyList<VirtualFileOperation> destinationOperations)` — `src/FileManager.Core/DryRun/DestinationProjector.cs:41`. Copies path strings out.
- `WireChunkConverter.Convert(slice)` (nested in the handler) → `DryRunDirectoryTableBuilder.Convert(PhysicalFile)` / `.Convert(VirtualFileOperation)` — `src/FileManager.Contracts/DryRun/DryRunDirectoryTable.cs:67,81`. Builds fresh wire objects.

Because the server serializes each yielded frame synchronously before pulling the next
(`IpcServer.ServeStreamAsync`), and streaming enumeration is sequential, **the handler fully finishes
chunk N before the engine's iterator resumes past its `yield return`.** This is the safety property the
whole pooling scheme rests on.

## 3. Key facts that shape the design (read before coding)

1. **Only spilled runs benefit.** Below the spill threshold the `FileDryRunSpool` replays the *original*
   `FileEvaluation` objects from its in-memory list — no serialize/deserialize, so **no second
   allocation to pool away.** Pooling only pays off once a run spills. Keep the small-run path
   allocation-free of new carriers (don't force carriers where originals suffice), and **benchmark on a
   large spilled run** to prove the win.
2. **The batch path (`SimulateAsync`) must stay byte-for-byte unchanged** in behavior. It still sorts and
   uses `FileEvaluation` + `record with { }`.
3. **AOT / no reflection.** All JSON is source-generated (`DryRunSnapshotJsonContext`,
   `FileManagerJsonContext`); handler dispatch is a hand-written table. Any new serialized type or DI
   must stay reflection-free.
4. **`IReadOnlyList<T>` is covariant.** `IReadOnlyList<PhysicalFile>` is assignable to
   `IReadOnlyList<IPhysicalFileView>` when `PhysicalFile : IPhysicalFileView`. This lets you widen the
   consumer/`DryRunChunk` element types to interfaces **without changing existing callers** that pass
   concrete lists (e.g. the destination sweep, which produces real `PhysicalFile`s).
5. **`record with { }` needs the concrete type** — it cannot run through an interface. The index remap in
   `StreamAccumulator.AppendGlobalized` currently does `op with { SourceIndex = …, SubjectIndex = … }`.
   For pooled (mutable) carriers you instead **mutate in place**. This forces a small second code path
   (records for batch, carriers for the streamed replay). This is the main source of complexity.

## 4. Design

Widen the streamed chunk to a read-only **view interface**, back it with **pooled mutable carriers on
the file-spool read path**, and **recycle each chunk's carriers right after its `yield` resumes**.

### 4.1 View interfaces (in Contracts, next to the records)

In `src/FileManager.Contracts/DryRun/DryRunReport.cs`:

```csharp
public interface IPhysicalFileView
{
    string Path { get; } string Root { get; } long Length { get; }
    DateTimeOffset LastWritten { get; } bool IsReparsePoint { get; }
}
public interface IFileOperationView
{
    string Path { get; } string Root { get; } OperationKind Kind { get; }
    int SourceIndex { get; } int SubjectIndex { get; }
    OnSuccessAction? SourceDisposition { get; } string? Detail { get; }
}
```

Make `PhysicalFile : IPhysicalFileView` and `VirtualFileOperation : IFileOperationView` (they already
have every member — just add the base list; no body changes).

### 4.2 Widen the seams to the interfaces (mechanical, covariance makes callers compile)

- `DryRunChunk` element types → `IReadOnlyList<IPhysicalFileView>` / `IReadOnlyList<IFileOperationView>`.
- `DryRunSpaceEstimator.Accumulate(...)` params → the view interfaces.
- `DestinationProjector.AccumulateSurvivors(..., IReadOnlyList<IFileOperationView> ...)`.
- `DryRunDirectoryTableBuilder.Convert(IPhysicalFileView)` / `.Convert(IFileOperationView)`
  (`DryRunDirectoryTable.cs`). Read props only — no logic change.
- Confirm the handler and the batch `ReportBuilder` (in `DryRunEngine.cs`, which also calls
  `_dirs.Convert(...)`) still compile; the sweep path passes concrete `PhysicalFile` lists → OK by
  covariance.

### 4.3 Pooled carriers + pool

New file `src/FileManager.Core/DryRun/PooledEvaluation.cs`:

```csharp
internal sealed class PooledPhysicalFile : IPhysicalFileView { /* mutable fields + props; Reset() */ }
internal sealed class PooledFileOperation : IFileOperationView { /* mutable fields + props; Reset() */ }
```

Plus a **bounded per-run pool** (`Microsoft.Extensions.ObjectPool` if referenced, else a tiny
stack-backed pool — AOT-safe). Bound the retained count so idle memory stays flat.

### 4.4 File-spool read path fills carriers; recycle per chunk

Scope pooling to the **`FileDryRunSpool` read path only** (that is the sole place a second object set is
materialized). Recommended shape:

- The **per-run carrier pool** is created in `SimulateStreamAsync` and shared with the spool and the
  accumulator (thread it through `CreateSpool()` / a field on the spool + accumulator).
- `FileDryRunSpool.ReadAllAsync` deserializes each record into **rented carriers** (source file, source
  op, and pooled child lists of dest carriers) instead of fresh records. `InMemoryDryRunSpool` keeps
  yielding the original records — they already implement the view, and they are single-allocation, so
  **do not pool them**.
- `StreamAccumulator` moves each entry's carriers into the chunk's buffer lists, **mutating index fields
  in place** for the global remap (replacing the `with { }` copies). Keep the exact remap arithmetic
  from the current `AppendGlobalized` (source op `SourceIndex = globalSourceIndex`; dest op
  `SourceIndex = op.SourceIndex == 0 ? globalSourceIndex : -1`; `SubjectIndex = op.SubjectIndex >= 0 ?
  destBase + op.SubjectIndex : -1`). Verify indices against the batch path.
- **Recycle point (I-POOL-RECYCLE).** In `SimulateStreamAsync`, immediately **after each
  `yield return chunk` resumes** (and after the final chunk), return that chunk's pooled carriers +
  child lists to the pool. This is safe *only* because the handler consumed the chunk synchronously
  before requesting the next (§2). Do **not** recycle carriers that came from the in-memory spool (they
  are shared originals). Make the recycle a no-op for non-pooled instances (e.g. type-check, or route
  recycling through the spool with an in-memory no-op override).

Document the invariant on `DryRunChunk` and the spool: *a streamed chunk's entries are pool-owned and
valid only until the consumer requests the next chunk; no consumer may retain them.*

### 4.5 (Optional, Stage 2) write-side pooling

Only if a benchmark shows the *production* allocations (in `EvaluateFileAsync`) still dominate: let the
stream sink hand each eval worker a rented carrier to fill instead of a fresh `FileEvaluation`, and
serialize from it. This touches the shared eval path, so gate it behind the sink and keep the batch
path on records. **Skip unless profiling justifies it** — §3.1 says the read-back set is the regression
the snapshot introduced, and §4.4 already removes it.

## 5. Files to touch (checklist)

- [ ] `src/FileManager.Contracts/DryRun/DryRunReport.cs` — add view interfaces; records implement them.
- [ ] `src/FileManager.Contracts/DryRun/DryRunDirectoryTable.cs` — `Convert` overloads take views.
- [ ] `src/FileManager.Core/DryRun/IDryRunEngine.cs` — `DryRunChunk` element types → views; doc the invariant.
- [ ] `src/FileManager.Core/DryRun/PooledEvaluation.cs` — **new**: carriers + pool.
- [ ] `src/FileManager.Core/DryRun/DryRunSpaceEstimator.cs` — `Accumulate` params → views.
- [ ] `src/FileManager.Core/DryRun/DestinationProjector.cs` — `AccumulateSurvivors` param → view.
- [ ] `src/FileManager.Core/DryRun/FileDryRunSpool.cs` — read path fills rented carriers; share the pool.
- [ ] `src/FileManager.Core/DryRun/InMemoryDryRunSpool.cs` — yields originals; recycle no-op.
- [ ] `src/FileManager.Core/DryRun/IDryRunSpool.cs` — extend if a recycle hook lives here.
- [ ] `src/FileManager.Core/DryRun/DryRunEngine.cs` — `StreamAccumulator` in-place remap + recycle-after-yield in `SimulateStreamAsync`; create/thread the pool. **Leave `SimulateAsync`/`ReportBuilder` behavior unchanged.**
- [ ] `src/FileManager.Core/IPC/Handlers/DryRunStreamHandler.cs` — recompile against widened types (sweep path unaffected).

## 6. Verification

Build & test (Windows, PowerShell or Bash):

```
dotnet build src/FileManager.Core/FileManager.Core.csproj
dotnet test  tests/FileManager.Core.Tests/FileManager.Core.Tests.csproj
dotnet test  tests/FileManager.Service.Tests/FileManager.Service.Tests.csproj
dotnet test  tests/FileManager.UI.Tests/FileManager.UI.Tests.csproj
```

Existing suites must stay green (Core 275, Service 8, UI 188, Contracts 120 at baseline). Add to
`tests/FileManager.Core.Tests/DryRun/`:

1. **Byte-identical report, pooling on vs off.** Run a scan large enough to spill through
   `SimulateStreamAsync`, assemble the chunks into a report, and assert it equals the report from a run
   with `SpoolFactory = InMemoryDryRunSpoolFactory` (no pooling). Guards against cross-chunk aliasing /
   stale-carrier bugs and wrong index remaps. Compare source/destination files and ops element-wise
   (records have value equality; sort both by path first — streamed order is discovery order).
2. **Carriers returned to the pool.** After a spilled run, assert borrowed == returned (instrument the
   pool with counters, or assert the pool's retained count is bounded, not proportional to file count).
3. **No aliasing across chunks.** With a tiny `ChunkByteBudget` (force many chunks) and a tiny
   `SpillThresholdBytes` (force spill), collect all chunks into a list *without* copying, then assert the
   fully assembled report is still correct — proving a later chunk's carrier reuse didn't corrupt an
   earlier chunk. (If chunks are consumed lazily this already holds; if a test buffers them, it must
   copy — document which.)
4. **Existing** `FileDryRunSpoolTests` and `DryRunEngineTests` must still pass unchanged.

Benchmark (optional but recommended): use `benchmarks/FileManager.UI.Benchmarks` or a Core micro-bench to
confirm allocated bytes / Gen2 drop on a large spilled run.

## 7. Risks & gotchas

- **Wrong index remap → silent report corruption.** The in-place mutation must reproduce the exact
  arithmetic the `with { }` remap did. Test #1 is the guard — do not skip it.
- **Recycling too early.** Only recycle after the `yield` resumes (handler done with the chunk). Never
  recycle mid-accumulation of the next chunk in a way that hands a still-referenced carrier back out.
- **In-memory originals are not poolable.** Recycling them would corrupt the small-run path (and the
  batch path shares `FileEvaluation`). Keep the pooled vs original distinction crisp.
- **AOT.** Carriers are plain classes (fine). If you change the on-disk snapshot record shape, update
  `DryRunSnapshotJsonContext` accordingly (still source-gen).
- **Keep the win real.** If benchmarks show negligible gain (e.g. runs rarely spill in practice),
  consider whether Stage 1 alone is worth merging — report the numbers rather than shipping complexity
  blindly.

## 8. Suggested commit

One focused commit on `feature/foundation` (or a branch off it), message subject e.g.
`Pool dry-run entry objects on the spool read-back path`. Multi-line body via a file + `git commit -F`
(this repo's convention). Do **not** stage `third_party/TreeDataGrid` (a pre-existing submodule change).
