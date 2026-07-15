# Dry-run preview: memory optimization plan

**Audience:** a coding agent picking this up cold. Everything needed is below; file
references use `path:line` against the current tree.

## Context

After a dry run completes, `DryRunView` holds a large amount of memory even before
the tree view is shown. Investigation established:

- **The UI is already virtualized.** The flat list is an Avalonia `ListBox` whose
  `ListBox.rows` style (`src/FileManager.UI/Views/DryRunView.axaml:171`) does *not*
  override `ItemsPanel`, so it keeps the default `VirtualizingStackPanel`; it sits in
  a star-sized grid row (`RowDefinitions="…,*"`, `DryRunView.axaml:282` / `:401`), so
  the viewport is bounded and only visible `ListBoxItem`s are realized. The new
  `TreeDataGrid` flattens the hierarchy into a single virtualized row list. Neither
  view is the source of the memory.
- **The cost is the materialized data model, not UI containers.**
  `DryRunViewModel.ApplyReport` (`DryRunViewModel.cs:1058`) eagerly projects the whole
  report into two full lists of row records that are retained in each tab's `_all`
  field (`:480`, `:663`) for the lifetime of the preview:
  - `DryRunFileRow` per source file — path/root/detail strings, a `List<DryRunTargetRow>`,
    and a **precomputed `ParentDisplay`** string (`:40-48`).
  - `DryRunDestinationRow` per source with destination ops, each holding a
    `List<DryRunDestinationEntry>`; every entry stores **precomputed `ParentDisplay`
    and `RelativeDisplay`** strings (`:117-124`), and the row stores a precomputed
    `SourceDisplay` (`:154-158`).
  - Sources and Destinations build **separate** row sets from the same report.
  - Root strings (`SourceRoot`, `TargetRoot`, `TargetRoot` on entries) repeat across
    nearly every row and are distinct string instances (JSON deserialization does not
    intern), so they are duplicated many times over on the retained heap.
- The `DryRunReport` itself is **not** retained — it is a local in `RunAsync`
  (`DryRunViewModel.cs:1041`) passed to `ApplyReport` and GC-eligible afterward. So the
  retained footprint is the row records, not the report. (Peak *during* `ApplyReport`
  briefly holds both.)
- The source-file count is capped at the engine's `MaxStreamedFiles` (**500,000**,
  `DryRunEngine.cs:68`) — the bound on the streamed path the UI actually uses
  (`IpcGateway.cs:85-90`). Do not confuse it with `MaxReportedFiles` (50,000,
  `DryRunEngine.cs:53`), which only bounds the legacy single-frame batched path (it is
  an IPC frame-size guard, not the display cap). So the footprint is bounded, but at up
  to 500k files × multiple strings/row it is potentially hundreds of MB — the
  "considerable memory" the user observed.

Goal: cut the **retained** heap of a populated dry-run preview, with no behavior or
visual change. **Hard constraint: the app publishes with `PublishAot` — every change
must stay AOT/trim-clean** (no runtime reflection/serialization outside the
source-generated `FileManagerJsonContext`; the acceptance section has the publish
check). Everything proposed below satisfies this: computed record properties, a local
intern dictionary, and contract types registered with the JSON source generator.

## Measure first (mandatory, before and after each optimization)

Two different things matter; measure both.

1. **Allocations during projection** — the existing benchmark already covers this:
   `benchmarks/FileManager.UI.Benchmarks/ViewModels/DryRunViewModelBenchmarks.cs`
   (`[MemoryDiagnoser]`, params 1k/10k/50k). Its params (and the comment claiming 50k
   is the cap) predate streaming — add a **500_000** param so the gauge covers the real
   `MaxStreamedFiles` ceiling, and fix that comment. Run:
   ```
   dotnet run -c Release --project benchmarks/FileManager.UI.Benchmarks
   ```
   Record the **Allocated** column per `FileCount`. Re-run after each change.

2. **Retained heap after projection** — BenchmarkDotNet's `Allocated` is per-op
   allocation, not what stays alive. Add a small retained-size probe (a benchmark is
   the wrong tool). Add an internal test in `FileManager.UI.Tests` that builds a
   **500k** report — the real cap — (reuse the benchmark's `BuildReport` shape) and
   measures what `ApplyReport` leaves behind. Two traps to avoid: nulling a local does **not** guarantee the JIT
   treats the report as unreachable, and the measurement is meaningless unless the
   report is provably collected. Shape it like this:
   ```csharp
   [MethodImpl(MethodImplOptions.NoInlining)]   // report ref must die with this frame
   static (DryRunViewModel Vm, WeakReference Report) BuildAndApply()
   {
       DryRunReport report = BuildReport(500_000);
       DryRunViewModel vm = new(gateway: null!);
       vm.ApplyReport(report);
       return (vm, new WeakReference(report));
   }

   long before = GC.GetTotalMemory(forceFullCollection: true);
   (DryRunViewModel vm, WeakReference report) = BuildAndApply();
   long after = GC.GetTotalMemory(forceFullCollection: true);
   Assert.False(report.IsAlive);        // proves the delta is rows-only, not report + rows
   long retained = after - before;      // ≈ retained row footprint; assert under a budget
   GC.KeepAlive(vm);                    // rows must outlive the second measurement
   ```
   This is the number the user cares about. Capture it before starting so each
   optimization can be quantified. (Keep the test tagged/skippable if it is slow.)

## Optimization 1 — compute display strings lazily (primary, high ROI)

Drop the stored relative-directory strings; recompute them on demand. Because the UI
is virtualized, only the handful of visible rows ever compute them. Each row instead
holds one **shared** reference to its tab's common-root string (a pointer, not a copy).

Existing helper to reuse: `DryRunPaths.SplitForDisplay(absolutePath, commonRoot)`
returns `(FileName, ParentDisplay)` (`DryRunPaths.cs:58`). It is pure — safe to call
from a getter.

Changes in `DryRunViewModel.cs`:

- **`DryRunFileRow`** (`:40`): remove the stored `string ParentDisplay = ""` positional
  member; add a `string? SourceCommonRoot` member. Replace with:
  ```csharp
  public string ParentDisplay => DryRunPaths.SplitForDisplay(SourcePath, SourceCommonRoot).ParentDisplay;
  ```
  `FileName` already computes on demand (`:51`) — leave it.
- **`DryRunDestinationEntry`** (`:117`): remove stored `ParentDisplay` and
  `RelativeDisplay`; add `string? CommonRoot`. Replace with:
  ```csharp
  public string ParentDisplay  => DryRunPaths.SplitForDisplay(TargetPath, CommonRoot).ParentDisplay;
  public string RelativeDisplay => ParentDisplay + FileName;
  ```
- **`DryRunDestinationRow`** (`:154`): remove stored `SourceDisplay`; add
  `string? SourceCommonRoot`. Replace with:
  ```csharp
  public string SourceDisplay =>
      SourcePath is null ? "" :
      DryRunPaths.SplitForDisplay(SourcePath, SourceCommonRoot).ParentDisplay + System.IO.Path.GetFileName(SourcePath);
  ```
  Also remove the stored positional `FileName` — it is derivable, identically, from
  members the row already carries (construction stores `GetFileName(file.Path)` at
  `:1127` and `GetFileName(o.Path)` at `:1135`, and `Primary.TargetPath == o.Path` for
  no-source rows):
  ```csharp
  public string FileName =>
      SourcePath is null ? Primary.FileName : System.IO.Path.GetFileName(SourcePath);
  ```
- **`ApplyReport`** (`:1080-1135`): stop precomputing these strings; pass the single
  `sourceCommonRoot` / `destCommonRoot` (already computed at `:1075-1078`) into the row
  and entry constructors. All rows in a tab share the same commonRoot instance.

Consumers to leave working (they bind the same property names, now computed):
`DryRunView.axaml:381` (`ParentDisplay`), `:487` (`Primary.ParentDisplay`), `:503`
(`SourceDisplay`), `:519` (`RelativeDisplay`). No XAML change required.

Notes / caveats for the implementer:
- These properties are read once per container realization/recycle (Avalonia bindings
  don't re-read every layout pass), and only for visible rows. That is fine (dozens of
  rows), but do **not** reintroduce eager computation to "optimize CPU" — that defeats
  the purpose.
- Filtering, sorting, search, and the tree build never touch these properties
  (`Rebuild` sorts via `DryRunSort.RelativeKey`, `Matches` uses the raw paths, trees
  use `RelativeForTree`), so the getters really only run for realized rows — verified
  against the current tree.
- Records with added members: update the positional constructor call sites (all inside
  `DryRunViewModel.ApplyReport` — see Test impact).

## Optimization 2 — intern repeated root strings (cheap, stacks with #1)

Roots repeat across almost every row and are duplicated instances. In `ApplyReport`,
canonicalize them through a local dictionary so the retained rows share one instance
per distinct root:
```csharp
Dictionary<string, string> rootPool = new(StringComparer.Ordinal);
string Intern(string? s) => s is null ? null! : rootPool.TryGetValue(s, out var v) ? v : (rootPool[s] = s);
```
Apply `Intern(...)` to `file.Root`, `op.Root`/`o.Root`, and each target/entry `Root`
as rows are built (`:1085-1135`). Also intern the shared `SourceCommonRoot` /
`CommonRoot` passed to rows from #1 (they are already single instances, so this is
just for consistency). Do **not** intern full paths — they are unique, so pooling them
only adds a dictionary with no payoff.

This is worth more than it looks: rows retain one duplicated root instance per contract
record they were built from — `file.Root` per file row (up to 500k at the cap) plus
`op.Root` per destination op (the `DryRunTargetRow` and `DryRunDestinationEntry` built
from the same op share one instance). At the benchmark shape scaled to the 500k cap
that is ~1.3M duplicate ~20-char strings — tens of MB of retained heap for a dozen
lines of code.

## Optimization 3 — normalize the wire contract into a directory table (optional, larger)

Only pursue if #1 + #2 do not hit the retained-memory budget — though at the real 500k
cap this is likely to be needed for worst-case runs, not just a contingency. It attacks
what #1/#2 cannot: the absolute path strings the rows retain — at the cap, ~1.3M of them
(~250 B each ≈ **~300 MB**). Paths are highly redundant — siblings repeat their entire
directory chain — so the fix is structural sharing: represent the tree **in the
contract**, not as flat strings.

(An earlier draft of this section proposed retaining the report and projecting rows
lazily. That is superseded: it keeps every absolute path string alive, and a lazy
`IReadOnlyList` that materializes a fresh row per indexer access has a row-identity
hazard — record equality compares the `Targets`/`Destinations` lists **by reference**,
so two reads of the same index are never `Equals`, which breaks anything relying on
item equality: ListBox selection, `IndexOf`, container recycling.)

Sketch:
- **Contract** (`src/FileManager.Contracts/DryRun/DryRunReport.cs`): add a
  parent-indexed directory table — `DirectoryEntry(string Name, int ParentIndex)` with
  `-1` for a drive/UNC root. `PhysicalFile` and `VirtualFileOperation` replace
  `Path`/`Root` (absolute strings) with `(int DirIndex, string FileName)` +
  `int RootDirIndex`. Register new types in
  `src/FileManager.Contracts/FileManagerJsonContext.cs` (AOT source-gen).
- **Engine** (`src/FileManager.Core/DryRun/DryRunEngine.cs:570`, `ReportBuilder`): emit
  each directory entry once, on first use, with a path→index map held only during the
  build.
- **Streaming composes cleanly** — the dry-run already streams:
  `IDryRunEngine.SimulateStreamAsync` (`IDryRunEngine.cs:22`) yields `DryRunChunk`
  slices with globally-assigned indices, and the IPC server writes one frame per chunk
  (`IpcServer.cs:229`; the 16 MiB frame cap is per chunk, not per report — the UI takes
  this path, `IpcGateway.cs:85-90`). Add a `Directories` slice to `DryRunChunk`
  (`IDryRunEngine.cs:31`): each chunk carries the dir entries that first appear in it,
  before the files/ops that reference them, using the same globally-assigned-index
  convention `SourceIndex`/`SubjectIndex` already follow across chunks. The dir table is
  never one monolithic message. Note streaming alone does **not** reduce retained
  memory — the UI keeps every row regardless of how they arrived; the normalized form is
  what shrinks both the retained rows and the peak (today the client reassembles the
  full stringy report before `ApplyReport`).
- **UI** (`DryRunViewModel.ApplyReport` + both tabs): rows hold a directory-node
  reference (or index) + filename instead of path strings; absolute paths,
  `ParentDisplay`, and relatives are reconstructed on demand by walking parents — only
  ever for visible rows. Search (`Matches`) must handle terms spanning separators
  (reconstruct per row, or match segment-aware with a boundary check); sort keys
  materialize transiently per rebuild, which is what `Rebuild` already does today.
- **Compatibility**: both sides of the IPC boundary change — bump the protocol version
  so a mismatched service/UI pair fails loud instead of misparsing.

Why contract-level rather than rebuilding the same tree inside the ViewModel: the
normalized form also fixes **peak** memory (the fully-stringed report never exists on
either side), shrinks the JSON payload (paths are its bulk) and (de)serialization cost,
and the shared structure falls out of deserialization instead of being rebuilt from
strings that are then thrown away. If the contract must stay frozen, the cheap middle
rung is to store paths root-relative in the rows (absolute reconstructed on demand from
the root reference) — strictly less saving, strictly less churn.

Either way: **measure** with the retained-heap probe after #1 + #2 before committing.
This is a real cross-component refactor (engine, contracts, UI filtering/sorting/search/
tree build), so it carries the most regression risk.

### Optional micro-wins (only if already touching the records; do not mandate)

- ~2/3 of source rows (the skipped files) get a fresh empty `List<DryRunTargetRow>`
  from the `.ToList()` at `:1085-1087`; share a singleton empty list (~10 MB at the
  500k cap).
- `DryRunFileRow.TargetRoots` (`:93-97`) allocates a LINQ chain + list on every access
  and is hit per-row during `Load` and destination-facet filtering — allocation churn,
  not retained memory, so it is outside this doc's goal; noted only for awareness.

## Suggested order & risk

1. Add the retained-heap probe + baseline both metrics. (no risk)
2. Optimization 1 — lazy display strings. (low risk; pure getters, existing helper)
3. Optimization 2 — intern roots. (low risk)
4. Re-measure. If still over budget — at the 500k cap, expect this — Optimization 3:
   the directory-table contract. (higher risk; cross-component)

## Verification / acceptance

- `dotnet build File-Manager.slnx` clean; `dotnet test File-Manager.slnx` all green.
- Retained-heap probe shows a clear reduction at 500k files (set a concrete budget from
  the measured baseline, e.g. "≥40% lower than baseline" — pick the number after step 1).
- `DryRunViewModelBenchmarks` Allocated does not regress materially.
- No visual/behavioral change: run the app, dry-run a large profile, confirm the flat
  list and both `TreeDataGrid` tabs render identically (paths, parent folders, sizes,
  pills), search/facet filtering and header sort still work.
- Stays AOT/trim-clean: `dotnet publish src/FileManager.UI -r win-x64 -c Release`
  emits no new IL trim warnings (run from a VS Developer environment — the AOT native
  toolchain is required).
- Optimization 3 only: bump and verify the IPC protocol version (a mismatched
  service/UI pair must fail loud, not misparse), and re-run a streamed dry-run
  end-to-end against the real service.

## Test impact

Smaller than you'd expect: **nothing constructs these row records outside
`DryRunViewModel.cs` itself** (`:1086`, `:1088`, `:1114`, `:1126`, `:1134` are the only
construction sites in the repo). The test helpers in
`tests/FileManager.UI.Tests/DryRunViewModelTests.cs` / `DryRunViewSmokeTests.cs`
(`Pf`, `SrcOp`, etc.) build **contract** types (`PhysicalFile`,
`VirtualFileOperation`) and drive everything through `ApplyReport`, so Optimization 1's
record-shape changes touch no test code. Assertions on
`ParentDisplay`/`RelativeDisplay`/`SourceDisplay` keep passing because the computed
values are identical to the old stored ones.
`benchmarks/FileManager.UI.Benchmarks/ViewModels/DryRunViewModelBenchmarks.cs` likewise
constructs only contract types — unaffected, but keep it as the allocation gauge.
(Optimization 3 is different: the contract change rewrites those same helpers and the
benchmark's `BuildReport` — budget for that as part of its cost.)
