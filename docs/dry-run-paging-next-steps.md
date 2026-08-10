# Dry-run paging

**Status: done. The preview reads a window of the plan from the service; the whole-plan path is deleted.**

Measured by `DryRunViewModelMemoryTests`: retained heap with a preview open is **17.7 MB at 500,000 rows
and 19.6 MB at 5,000,000** — flat, which was the whole point. Before this it was 72.1 MB at 500,000 and
linear, plus a ~147 MB transient sort.

---

## Why any of this exists

`DryRunEngine.MaxStreamedFiles = 500_000` used to bound a plan. Because the dry run **is** the work list
that gets executed — `RunSnapshotWriter` freezes it to disk and `RunCoordinator.ExecuteAsync` enqueues
from that file — a truncated plan was not a degraded preview but a wrong job. `MirrorDeletionPass`
already refused to act on one, so "too many files" silently became "this profile cannot be mirrored".

The cap is gone. What made it removable is that the preview no longer has to hold the plan: the service
sorts and filters where the rows are, and the client reads a window.

Read `docs/dry-run-service-memory.md` and `docs/dry-run-ui-memory-next-steps.md` for the memory work this
builds on.

---

## What shipped

### Removing the cap

- `DryRunEngine` — `MaxStreamedFiles`, `MaxScannedCandidates` and the candidate check in
  `ScanAndEvaluateAsync` are gone; `candidateCap` is now `int?`, null on the streamed path. The BATCHED
  path keeps `MaxReportedFiles` — that one is a response-frame limit, not a policy.
- `ProfilePlanner` — `PlanState.MaxFiles` and both count bounds gone.
- `DryRunChunk` — `ScanTruncated` and `SweepCapped` removed. **`SweepFaulted` is now the only producer of
  a truncated plan**, which is the honest one: a tree that could not be *read*.
- Replaced by resource guards that FAIL a plan instead of shortening it: `PlanMemoryGuard` and a
  free-space check in `RunSnapshotWriter` against `PlanSnapshotDiskReserveBytes`.

### Mirror deletion

- `EngineConfig.MirrorMaxOrphans` deleted. The count is on the preview footer before approval.
- **`MirrorMaxDeleteFraction` / `MirrorRatioFloor` kept deliberately.** A proportion is the thing approval
  cannot check: a `TargetLayout` flip produces a plausible count where every orphan is correctly
  classified, and only the shape gives it away.
- `MirrorDeletionRequest.Orphans` is `IEnumerable`, walked exactly once.

### Execution back-pressure

`TriggerQueue.WaitForRoomAsync` + `RunCoordinator.EnqueueCopiesAsync`. Hysteresis at half the high-water
mark; released by dequeue, `DropRun`, or cancellation.

### Paging, service side

| Piece | Where |
|---|---|
| Byte offset of every 512th row | `RunSnapshotBlockIndex` → `sources.idx`, `destinations.idx`, `deletes.idx` |
| Display position → row ordinal | `RunSnapshotOrder` → `sources.ord`, `destinations.ord` |
| Filtered orders | `RunSnapshotView` → `view-<hash>.ord`, LRU-capped at `MaxCachedViews` |
| Seeking row reader | `RunSnapshotReader.ReadByOrdinal`, with `ScanByOrdinal` as the unindexed fallback |
| `get-run-plan-page` | `GetRunPlanPageHandler` — a window, in the preview's own chunk shape |
| `get-run-plan-view` | `GetRunPlanViewHandler` — a filter, answering `(viewId, rowCount)` |
| Whole-plan aggregates | `RunSnapshotHeader.SourceRowsByRoot`, `DestinationRowsByRoot`, `DestinationRowsByKind`, `UntouchedCount`, `ProcessedCount` |
| Per-source target kinds | `RunSourceItem.TargetKinds`, an `OperationKindMask` |
| Per-destination result size | `RunDestinationItem.SizeBytes` |

Every sidecar is optional at read time. A snapshot written before paging existed, or one whose sidecar
write failed, degrades to plan order and a sequential scan — never to a broken preview.

### Paging, client side

- `IIpcGateway.GetRunPlanViewAsync` / `GetRunPlanPageAsync`, and `GetRunDetailAsync` for the aggregates.
- `PagedDryRunRowStore` — LRU of per-page `DryRunRowStore`s, `MaxResidentPages` deep.
- `PagedDryRunRowList<T>` — placeholders plus range-scoped `Replace` notifications.
- `DryRunViewModel.LoadPlanAsync` opens a plan in three O(1) calls: the header, then an unfiltered view
  over each half. `RebuildAsync` issues a filtered view; the debounce and supersede gate are unchanged.
- Deleted with the whole-plan path: both `ComputeLoad`s and their sorts, `PrepareReport`'s
  `Parallel.Invoke`, `ApplyReport`, both `ComputeRebuild`s, `DryRunRowList<T>`, `DryRunSort`,
  `DryRunIngestMeter`.

---

## Three things the previous handoff got wrong

Recorded because each cost real work, and because the same assumptions are easy to make again.

**1. The whole-plan aggregates did not cross the wire.** The doc said facet and status counts were
"already on `RunPlannedEvent`'s run detail path". They were on `RunSnapshotHeader` and nowhere else —
`RunProfileHandler.Detail` dropped them. They now ride on `RunDetailDto.Preview`
(`RunPlanPreviewAggregates`). Not on `RunPlannedEvent`, deliberately: that is a lossy broadcast, and
`RestoreAsync` replays a STORED event, so an event-borne copy would report whatever was true when it was
captured rather than what the snapshot says now.

**2. "The row handles need no changes" held only after two service additions.** A source page carries no
destination operations, so `DryRunFileRow.PrimaryTargetKind` had nothing to read and the New/Overwrite/
Rename/Skip glyph went blank on every row; a destination page carries neither the source nor a subject
file for a New write, so every new row read 0 B. Fixed by recording `RunSourceItem.TargetKinds` and
`RunDestinationItem.SizeBytes` during the plan's own walk. The ranking of kinds stays client-side — the
service ships the SET, never the choice.

**3. The Destinations tab could not keep its shape.** Three consequences, all accepted:
- **Fan-out grouping is gone.** A source replicating to three targets is three rows. Forced, not chosen:
  the view's index space is per operation, and a group cannot straddle a page boundary.
- **The hover tooltip shows the destination path**, not the originating source. Buying it back costs a
  full source path per destination row on the wire for one tooltip line.
- **The Destinations tab's Source facet is dropped.** `RunSnapshotView.KeepDestinations` filters by target
  root, kind and search only — a destination row's source root is not something it can select on.

---

## Follow-ups, in the order they are worth doing

**1. Tree mode.** `BuildTree` needed the directory structure of the whole view, which a paged client does
not have, so the toggle and both tabs' tree plumbing are removed. `DryRunTreeNode.BuildForest` and its
tests are intact and are what a restoration would reuse. Cheapest route: a `get-run-plan-tree` verb
serving children per directory, which is the genuinely flat answer. (Fetching the whole directory table on
toggle is O(directories), which the plan already accepts as its one non-flat term, and would also work.)

**2. The Destinations tab's Source facet.** Needs `KeepDestinations` to resolve each row's `SourceOrdinal`
to a root — one pass over `sources.ndjsonl` per view build, or an ordinal→root sidecar — plus a
`DestinationRowsBySourceRoot` tally on the header for the counts.

**3. The source path on a destination row.** Only worth doing alongside (2), which already builds the map
it needs.

---

## Traps — each of these cost a debugging cycle

**Do not sort the display files in place.** `RunDestinationItem.SourceOrdinal` is *defined* as a position
in `sources.ndjsonl`, so rewriting that file repoints every destination row's back-reference to its
source. The ordering lives in separate order files for exactly this reason.

**A page store must be sized for a page.** `ColumnBuffer` allocates fixed segments, and its default (8192
elements) is sized for a store whose length is unknown until the terminator arrives. A page holds 512
rows, so the default left 94% of every column empty — times 128 resident pages times two tabs, that was
59 MB of nothing, and it looked exactly like a retention bug. `DryRunRowStore.CreateForIngest` takes the
expected row count for this reason.

**The common root does not come free.** Rows read it off their store, and a page's own root is the root of
that page's rows — and pages are sorted by relative path, so a page usually holds one directory. Left
alone, every path renders relative to a root that shifts as the user scrolls. `PagedDryRunRowStore` stamps
plan-wide values via `DryRunRowStore.UseCommonRoots` after each page completes. The failure is silent and
cosmetic enough to ship unnoticed.

**Derive the common root client-side**, from the facet keys (`DryRunPaths.CommonRoot`, O(roots)). The
header deliberately does not carry it: `DryRunPaths` lives in the UI and Core cannot reference it, so a
service-side copy would be a second implementation free to disagree about UNC shares.

**`PagedDryRunRowStore` is locked, not thread-affine.** Fetch continuations resume on the UI thread in the
app (there is a synchronization context) and on a pool thread in a test host (there is not). An unlocked
cache corrupts under test — and so does a test that enumerates a `List` an arriving page is still
appending to.

**`FetchAsync` yields before doing anything.** Without it a synchronously-completing gateway inserts a
page and raises `PageArrived` from inside the list's indexer — i.e. during Avalonia's layout pass.

**Keep the non-generic `IList` implementation on the paged row list.** Avalonia's `ItemsSourceView` falls
back to copying the whole source into a list without it, which for a paged list would try to materialize
every row of a plan that deliberately is not resident. Pinned by
`DryRunRowStoreTests.The_bound_row_list_is_an_IList` and
`PagedDryRunRowStoreTests.The_list_implements_the_non_generic_IList`.

**A placeholder is a real row, and there is more than one.** Row handles are values, so every placeholder
built at the same index is `Equals` — and the bound list's `IndexOf`, which ListBox selection runs
through, would answer with the first of them. `DryRunRowStore.CreatePlaceholder` holds a page's worth and
the lists index it modulo that.

**Relative-key order and full-path order differ across ROOTS, not on the separator.** A full path carries
its root as a prefix, so it groups by root; the relative key strips it so the same relative file from two
roots sorts adjacently — which is what lines the two tabs up. (The `0x20 < 0x5C` trap in
`docs/dry-run-service-memory.md` is a *different* one, about `(dir, name)` tuple order.)

**Statuses cross the wire as `OperationKind`, never as the UI's chip names.** The chip → kind mapping
(`DestinationKindMap.Map` and its inverses in the two tabs' `SelectedKinds`) is a display decision and
stays client-side. The one chip that is not a kind — a destructive source disposition — rides as its own
flag, ORed, because the tab treats chips as alternatives.

**A null root/kind list means "keep everything"; an EMPTY list means "keep nothing".** Deselecting every
facet is a filter the user can express, and widening it back would show rows they just excluded.

**Write tool NUL hazard.** `RunSnapshotView.Filter.Key` was once committed with four literal NUL bytes
where `.Append(' ')` was intended — a valid `char` literal that worked as a hash separator, compiled clean
and passed the whole suite, but made the file *binary* to git so no diff or blame could read it. Now
pinned by `SourceTreeHygieneTests.No_source_file_contains_a_NUL_byte`.

---

## Verifying

1. `dotnet build File-Manager.slnx` — clean. The only warnings are pre-existing: CS9107 in
   `FileSystemService` and CA1416 in the Windows platform tests.
2. `dotnet test File-Manager.slnx --filter "Category!=Memory"` — 1,810 passing.
3. `dotnet test tests/FileManager.UI.Tests -c Release --filter "Category=Memory"` — 6 passing.
   `A_paged_preview_retains_the_same_heap_however_large_the_plan` is the one that decides this worked.
4. `dotnet publish src/FileManager.Service -r win-x64 -c Release` and the same for `src/FileManager.UI`,
   from a VS Developer environment — no new IL trim warnings. (One IL2104 comes from the Serilog package
   and predates this work.)

### The end-to-end check, which needs a human

**Jacob drives the UI — do not attempt it from the agent.**

Ask him to: plan a profile well past 500,000 files; confirm no truncation banner; scroll both previews end
to end and confirm no placeholder rows during ordinary scrolling (they are expected when dragging the
scrollbar thumb across a large plan); type in the search box and toggle facets and status chips on both
tabs; then approve and confirm execution runs the plan that was on screen.

The numbers that decide whether this worked in the shipped exe are private bytes at idle, with the preview
open, and after clearing it — `docs/dry-run-ui-memory-next-steps.md` defines exactly those three, and they
are the one part of that brief paging did not answer. **The with-preview figure should be flat between a
500k plan and a 5M one**; the headless probe says it is, and this confirms it with Avalonia's own
allocations in the picture.
