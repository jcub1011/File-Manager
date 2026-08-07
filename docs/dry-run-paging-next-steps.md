# Dry-run paging — handoff

**Status: the service side and the client-side machinery are done, tested and committed. What remains is
wiring `DryRunViewModel` to use them, then deleting the path they replace.**

Nothing in the shipping UI calls the new code yet, so app behaviour is currently unchanged. That is
deliberate — the wiring is one indivisible change and half of it is worse than none.

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

## What is already done

### Removing the cap (committed)

- `DryRunEngine` — `MaxStreamedFiles`, `MaxScannedCandidates` and the candidate check in
  `ScanAndEvaluateAsync` are gone; `candidateCap` is now `int?`, null on the streamed path. The BATCHED
  path keeps `MaxReportedFiles` — that one is a response-frame limit, not a policy.
- `ProfilePlanner` — `PlanState.MaxFiles` and both count bounds gone.
- `DryRunChunk` — `ScanTruncated` and `SweepCapped` removed. **`SweepFaulted` is now the only producer of
  a truncated plan**, which is the honest one: a tree that could not be *read*.
- Replaced by resource guards that FAIL a plan instead of shortening it: `PlanMemoryGuard` (requires both
  the runtime's high-memory-load line *and* our own heap past `EngineConfig.PlanProcessHeapFloorBytes`,
  so another process hogging the box cannot kill a plan we would have finished) and a free-space check in
  `RunSnapshotWriter` against `PlanSnapshotDiskReserveBytes`.

### Mirror deletion (committed)

- `EngineConfig.MirrorMaxOrphans` deleted. The count is on the preview footer before approval, so an
  absolute cap only refused work the user had read the number for.
- **`MirrorMaxDeleteFraction` / `MirrorRatioFloor` kept deliberately.** A proportion is the thing approval
  cannot check: a `TargetLayout` flip produces a plausible count where every orphan is correctly
  classified, and only the shape gives it away.
- `MirrorDeletionRequest.Orphans` is `IEnumerable`, walked exactly once. Count, bytes and the per-root
  tally come off the snapshot header, so every gate decides before a single orphan is read.

### Execution back-pressure (committed)

`TriggerQueue.WaitForRoomAsync` + `RunCoordinator.EnqueueCopiesAsync`. Hysteresis at half the high-water
mark; released by dequeue, `DropRun`, or cancellation. Cancellation is load-bearing — a run PAUSED with a
full queue of its own payloads cannot drain itself.

### Paging, service side (committed)

| Piece | Where |
|---|---|
| Byte offset of every 512th row | `RunSnapshotBlockIndex` → `sources.idx`, `destinations.idx`, `deletes.idx` |
| Display position → row ordinal | `RunSnapshotOrder` → `sources.ord`, `destinations.ord` |
| Filtered orders | `RunSnapshotView` → `view-<hash>.ord`, LRU-capped at `MaxCachedViews` |
| Seeking row reader | `RunSnapshotReader.ReadByOrdinal`, with `ScanByOrdinal` as the unindexed fallback |
| `get-run-plan-page` | `GetRunPlanPageHandler` — a window, in the preview's own chunk shape |
| `get-run-plan-view` | `GetRunPlanViewHandler` — a filter, answering `(viewId, rowCount)` |
| Whole-plan facet + status totals | `RunSnapshotHeader.SourceRowsByRoot`, `DestinationRowsByRoot`, `UntouchedCount`, `ProcessedCount` |

Every sidecar is optional at read time. A snapshot written before paging existed, or one whose sidecar
write failed, degrades to plan order and a sequential scan — never to a broken preview.

### Paging, client machinery (committed)

- `IIpcGateway.GetRunPlanViewAsync` / `GetRunPlanPageAsync`.
- `PagedDryRunRowStore` — LRU of per-page `DryRunRowStore`s. Pinned by a test that scrolls 200,000 rows
  end to end and asserts ≤128 pages resident.
- `PagedDryRunRowList<T>` — placeholders plus range-scoped `Replace` notifications.
- `FakeIpcGateway` has `Pages`, `PageRequests`, `ViewResult`, `ViewRequests`, `PageGate`.

---

## What is left

### 1. Wire `DryRunViewModel` (the main job)

Both tabs — `DryRunSourcesTab` and `DryRunDestinationsTab` — have the same shape, so do one, then the
other. All anchors below are in `src/FileManager.UI/ViewModels/DryRunViewModel.cs`.

**Preview open — `LoadPlanAsync` (~:3106).** Today it streams the whole plan into a `DryRunRowStore`,
then `PrepareReport` (~:3441) sorts it and `ApplyPrepared` (~:3490) publishes. Instead:
1. `get-run-plan-view` with no filter → `(viewId: null, rowCount)`.
2. Facet and status counts from the snapshot header (already on `RunPlannedEvent`'s run detail path).
3. Build a `PagedDryRunRowStore` per tab and bind a `PagedDryRunRowList`.

**Filtering — `RebuildAsync` (~:1692 and ~:2289).** Today it snapshots the filter state and runs
`ComputeRebuild` (~:1777 / ~:2373) over the resident rows. Instead, issue a `get-run-plan-view` and
rebuild the paged store against the returned handle. **Keep the debounce, the supersede gate and the
publish-on-UI-thread sequencing exactly as they are** — they are correct and independent of where the
filtering happens.

**Then delete**, in the same change: `ComputeLoad` (~:1477 / ~:2029) including its sort and key array,
`PrepareReport`'s `Parallel.Invoke`, and `RebuildInput`/`ComputeRebuild`'s filter pass. These are the
~147 MB transient and the linear retained cost. Leaving them alive beside the new path is the
two-implementations failure this codebase's snapshot design exists to prevent — it is how the destination
panel once came up empty.

### 2. Tree mode (`BuildTree`, ~:1846)

Deferred on purpose. The forest needs the directory structure of the whole view, which a paged client
does not have. Options, cheapest first: fetch the directory table once on tree toggle (O(directories),
which the plan already accepts as the one non-flat term); or add a `get-run-plan-tree` verb serving
children per directory, which is the genuinely flat answer.

### 3. Docs

`docs/dry-run-service-memory.md` §11 lists external merge sort under "considered and rejected" — it is
now **accepted and implemented**; move it and say why. Add measured before/after to
`docs/dry-run-ui-memory-next-steps.md`.

---

## Traps — each of these cost a debugging cycle

**Do not sort the display files in place.** `RunDestinationItem.SourceOrdinal` is *defined* as a position
in `sources.ndjsonl`, so rewriting that file repoints every destination row's back-reference to its
source. The ordering lives in separate order files for exactly this reason.

**The row handles need no changes.** `DryRunFileRow`, `DryRunDestinationRow` and `DryRunDestinationEntry`
are records over `(DryRunRowStore Store, int Index)`, and a page store plus an index *within that page*
is that shape. A row's targets resolve inside its own page. No row type, XAML binding or converter moves.

**But the common root does not come free.** Rows read it off their store, and a page's own root is the
root of that page's rows — and pages are sorted by relative path, so a page usually holds one directory.
Left alone, every path renders relative to a root that shifts as the user scrolls. Pass plan-wide values
into `PagedDryRunRowStore`; it stamps them via `DryRunRowStore.UseCommonRoots` after each page completes.
The failure is silent and cosmetic enough to ship unnoticed.

**Derive the common root client-side**, from the facet keys (`DryRunPaths.CommonRoot`, O(roots)). The
header deliberately does not carry it: `DryRunPaths` lives in the UI and Core cannot reference it, so a
service-side copy would be a second implementation free to disagree about UNC shares.

**`PagedDryRunRowStore` is locked, not thread-affine.** Fetch continuations resume on the UI thread in the
app (there is a synchronization context) and on a pool thread in a test host (there is not). An unlocked
cache corrupts under test.

**`FetchAsync` yields before doing anything.** Without it a synchronously-completing gateway inserts a
page and raises `PageArrived` from inside the list's indexer — i.e. during Avalonia's layout pass.

**Keep the non-generic `IList` implementation on both row lists.** Avalonia's `ItemsSourceView` falls back
to copying the whole source into a list without it, which for a paged list would try to materialize every
row of a plan that deliberately is not resident. Pinned by
`DryRunRowStoreTests.The_bound_row_list_is_an_IList` and
`PagedDryRunRowStoreTests.The_list_implements_the_non_generic_IList`.

**Relative-key order and full-path order differ across ROOTS, not on the separator.** A full path carries
its root as a prefix, so it groups by root; the relative key strips it so the same relative file from two
roots sorts adjacently — which is what lines the two tabs up. (The `0x20 < 0x5C` trap in
`docs/dry-run-service-memory.md:1033-1040` is a *different* one, about `(dir, name)` tuple order.)

**Statuses cross the wire as `OperationKind`, never as the UI's chip names.** The chip → kind mapping
(`DestinationKindMap.Map`) is a display decision and stays client-side. The one chip that is not a kind —
a destructive source disposition — rides as its own flag, ORed, because the tab treats chips as
alternatives.

**A null root/kind list means "keep everything"; an EMPTY list means "keep nothing".** Deselecting every
facet is a filter the user can express, and widening it back would show rows they just excluded.

**Write tool NUL hazard.** `RunSnapshotView.Filter.Key` was committed with four literal NUL bytes where
`.Append(' ')` was intended — a valid `char` literal that worked as a hash separator, compiled clean and
passed the whole suite, but made the file *binary* to git so no diff or blame could read it. If a source
file will not open, check `git diff --numstat` for `-` and scan for `\0`. Worth adding a test that scans
the source tree for NUL bytes; nothing else catches this.

---

## Verifying

1. `dotnet build File-Manager.slnx` — clean. There are 19 pre-existing warnings (CS9107 in
   `FileSystemService`, CA1416 in the Windows tests, two xUnit analyzer ones); no new ones.
2. `dotnet test File-Manager.slnx --filter "Category!=Memory"` — 1,835 passing at handoff. The Memory
   category builds 500k-row reports and is slow; run it before calling the wiring done, because those are
   the assertions that would catch a regression back to holding everything.
3. Suites that guard this work: `RunSnapshotPagingTests`, `GetRunPlanPageHandlerTests`,
   `GetRunPlanViewHandlerTests`, `PagedDryRunRowStoreTests`, `DryRunRowStoreTests`,
   `DryRunViewModelMemoryTests`, `MirrorDeletionGateTests`, `TriggerQueueTests`, `RunSnapshotTests`.
4. `dotnet publish src/FileManager.Service -r win-x64 -c Release` from a VS Developer environment — no new
   IL trim warnings. AOT/trim-clean is a standing requirement.

### The end-to-end check, which needs a human

**Jacob drives the UI — do not attempt it from the agent.** Build, run the tests, then hand over and wait.

Ask him to: plan a profile well past 500,000 files; confirm no truncation banner; scroll the preview end
to end and confirm no placeholder rows during ordinary scrolling; type in the search box and toggle
facets and status chips; switch to tree mode and expand deep; then approve and confirm execution.

The numbers that decide whether this worked are private bytes at idle, with the preview open, and after
clearing it — `docs/dry-run-ui-memory-next-steps.md:298-305` defines exactly those three.
**The wiring is only done if the with-preview figure is flat between a 500k plan and a 5M one.** That is
the whole point of the exercise; a linear number means something still holds every row.
