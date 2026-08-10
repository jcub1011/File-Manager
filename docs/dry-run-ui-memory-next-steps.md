# Dry-run UI memory: is another pass worth it?

> **FINDINGS (2026-08-03), answering everything except Question 1.** Two new probes landed in
> `DryRunViewModelMemoryTests` plus memory self-instrumentation in the UI (`UiMemoryLog`). Question 1
> still needs one run of the published exe; the rest is measured.
>
> **1. The brief is aimed at the wrong number.** `PrepareReport` allocates **147 MB** and pushes the
> managed heap from 72.6 MB to a peak of **~220 MB** — a **+147 MB transient**, twice the entire
> retained heap, paid in ~0.9 s while the user watches the progress bar. None of the three existing
> probes could see it (they measure after a forced full collection, by design). This is candidate **C**,
> which the brief ranked last. Measured by
> `PrepareReport_transient_allocation_at_streamed_cap_with_realistic_deep_paths`.
>
> **2. Question 2's model is confirmed in shape, and its number was closer than a refinement of it.**
> Measured by diffing the deep store against an identical one with every `FileName` replaced by a
> single shared 26-char constant
> (`File_names_share_of_retained_store_at_streamed_cap_with_realistic_deep_paths`):
> store alone **69.1 MB**, of which the name **strings** are **34.4 MB (50%)**. The store plus both
> tabs' key arrays is the familiar 72.1 MB. Note what the diff does and does not include: the
> `_srcName`/`_opName` reference columns (6.7 MB) survive in both builds, so all-in names are
> **~41 MB (57%)**, not the ~38 MB/53% estimated — but an intermediate estimate of 46.7 MB here
> overshot, because a 26-char string measures ~69 B retained rather than the 80 B the layout arithmetic
> predicts.
>
> **3. Candidate A is worth ~23 MB, not ~28 MB.** From the measurement: 41 MB today → 18.3 MB as a
> UTF-8 blob (13.0 blob + 2.0 offset table + 2.0 `_srcNameId` + 1.3 `_opNameId`), so **72.1 → ~49 MB
> (−32%)**. That lands essentially on the brief's own ~22 MB estimate. **The remaining retained-heap
> opportunity is ~23 MB against a ~220 MB peak.**
>
> **4. The UTF-8/UTF-16 ordering hazard is avoidable, and does not need to be reasoned about.** Byte-wise
> UTF-8 comparison genuinely is not equivalent to `OrdinalIgnoreCase` over UTF-16 (UTF-8 byte order is
> code-point order, sorting U+10000+ *above* U+E000-U+FFFF where UTF-16 surrogates sort them *below*;
> and non-ASCII case folding changes byte length). But that is self-inflicted. Store only **pure-ASCII**
> names in the blob and send anything else to a `List<string>` overflow table addressed by a negative
> id; widen ASCII to a `stackalloc Span<char>` (`System.Text.Ascii.ToUtf16`, vectorized) and keep every
> comparison in UTF-16. Zero semantics change, and no unpaired-surrogate lossiness either (which
> `Encoding.UTF8.GetBytes` would introduce by substituting U+FFFD).
>
> **5. `DryRunTreeNode.BuildForest` is not a blocker for A**, contrary to a first read of its
> `Func<T, string> nameSelector`. Leaves are deferred behind `_leafFactory` and the forest retains only
> `List<T>` int buckets, so `nameSelector` runs for root-level files and the leaves of directories the
> user actually expanded — dozens, not 500k. It can keep allocating a string per call.
>
> **6. Candidate B: confirmed not worth it, do not re-derive.** All four items are ~3.3 MB combined
> against 72 MB. `_opSize` in particular is a wash — the columns it derives from are dropped at
> `Complete()`.
>
> **7. Candidate D: do not copy `ConcurrentGarbageCollection=false`.** Confirmed the UI csproj has no GC
> configuration at all, and `MemoryTrimCoordinator` lives in `FileManager.Core`, which the UI
> deliberately does not reference — there is nothing to reuse. Blocking compacting gen2s on a UI thread
> are the wrong trade for a process whose problem is a 147 MB burst.
>
> **8. Question 1, answered — and it says stop on A while pointing at two bigger problems.** Published
> AOT exe, real profile, two consecutive runs with a clear between (`ui-20260803.log`). Scale, from the
> service log for the same runs: **33,449 source files / 330,797 destination ops** — 6.7% of the source
> cap.
>
> | Sample | managed | GC heap | GC committed | **private** | gen2 | allocated/run |
> |---|---:|---:|---:|---:|---:|---:|
> | idle | 8 MB | 8 MB | 13 MB | **108 MB** | 0 | — |
> | run 1, preview loaded | 178 MB | 182 MB | 187 MB | **300 MB** | 4 | **605 MB** |
> | after clear, +10 s | 75 MB | 85 MB | 101 MB | **214 MB** | 5 | — |
> | run 2, preview loaded | 222 MB | 232 MB | 236 MB | **354 MB** | 7 | **604 MB** |
>
> Three readings, all decisive:
>
> - **The retained row store is not the problem.** A 33k-source run puts private bytes at 300 MB against
>   a 108 MB idle. Candidate A's ~23 MB is at the *500k cap*; at this scale it would save a small
>   fraction of that. It is noise here. **Stop on A.**
> - **Memory is never returned.** Ten seconds after the preview is cleared, private is 214 MB against
>   108 MB idle, and the gen2 counter did not move (5 → 5) — the "settled" label is generous: an idle UI
>   allocates nothing, so no gen2 runs, so nothing is returned until the *next* run's burst forces one.
>   That is why run 2 starts from a higher floor and peaks 44 MB above run 1 (private 300 → 354 MB). This
>   is precisely the "231 MB retained forever" shape the service fixed, and the service log for these
>   same runs shows what fixed looks like: `GC committed 91MB -> 2MB, heap 58MB -> 2MB` **in 14 ms**.
> - **The burst is 605 MB per run, and `PrepareReport` is a minority of it.** `PrepareReport` measures
>   147 MB at 500k/deep (finding 1); at this run's scale it is well under half that. So the large majority
>   of the 605 MB is the **IPC deserialize + ingest path**, which nothing measures. For comparison the
>   *service* produced and serialized the same run in 292 MB — the UI allocates roughly twice as much to
>   consume it as the service did to produce it.
>
> **Recommendation: yes to another pass, no to the pass this brief proposed.** Ranked:
> **(1)** return memory at preview-close — the app currently never does, it is the most user-visible
> number, the smallest change, and §13's standing preference explicitly sanctions a collect at that
> moment (the service's equivalent costs 14 ms, which is not a perceptible UI stall);
> **(2)** cut the ingest/deserialize allocation, after instrumenting it separately from `PrepareReport`;
> **(3)** candidate C (the ~147 MB sort-key arrays at the cap) — real, but third.
> Candidates A, B and D-as-a-csproj-knob are closed.
>
> **9. Item (1) implemented, and the ambiguity in finding 8 is resolved.** The log could not distinguish
> "the post-clear 214 MB is garbage nobody collected" from "it is still live and there is a lifetime
> bug" — no gen2 had run, so both stories fit. A `WeakReference` settles it deterministically:
> `Clearing_the_preview_releases_the_row_store` builds a preview, drops the only strong reference to the
> store into a weak one, calls `ClearProfile()`, collects, and asserts the store is dead. **It passes** —
> so the cleared preview's memory is genuinely garbage and a trim is the right treatment, not a lifetime
> fix. That test now guards the conclusion: if something ever re-roots the store, it fails and says so.
>
> `UiMemoryTrim.AfterPreviewClosed()` runs at the end of `ClearReport` (gated on a preview having
> actually been loaded) and logs committed/heap before → after plus elapsed. It reuses
> `MemoryTrimCoordinator`'s two-pass shape — a single aggressive collect is reported not to decommit with
> LOH allocations (dotnet/runtime#78679), and `LargeObjectHeapCompactionMode` reverts after every blocking
> GC so it is set inside the loop. It is a re-implementation, not reuse: that type lives in
> `FileManager.Core`, which the UI does not reference.
>
> Note the second trigger this picks up for free: `RunAsync` clears before it starts, so a *second*
> consecutive run now trims before allocating rather than starting on the previous run's uncollected
> floor — which is the mechanism behind the 300 → 354 MB climb in finding 8.
>
> **10. The trim verified on the published exe — it works, and there is no retention bug.**
> Second run of `ui-20260803.log` (15:07 onward), same profile:
>
> | Sample | managed | GC heap | GC committed | **private** |
> |---|---:|---:|---:|---:|
> | idle | 8 MB | 8 MB | 13 MB | **108 MB** |
> | run 1, preview loaded | 174 MB | 182 MB | 183 MB | **291 MB** |
> | at clear, before trim | 185 MB | 189 MB | 204 MB | **316 MB** |
> | **after trim (104 ms)** | — | **62 MB** | **62 MB** | **173 MB** |
> | +10 s, idle | 64 MB | 62 MB | 62 MB | 177 MB |
>
> **committed 204 → 62 MB, private 316 → 173 MB, in 104 ms, and it stays down.** Before the trim the
> process sat at 214 MB indefinitely. The stall is 104 ms rather than the service's 14 ms — a bigger heap
> and two compacting passes — which is perceptible but lands on a transition the user initiated. If it
> ever needs to be invisible, post it at `DispatcherPriority.Background` so the cleared state paints
> first.
>
> **Correction to a claim made mid-investigation.** The remaining 62 MB against an 8 MB startup idle was
> read as a ~54 MB retention bug, and an early version of
> `Clearing_the_preview_returns_the_retained_heap_at_streamed_cap` appeared to confirm it at 94 MB / 78%
> retained. That probe was measuring itself: it awaited the tree rebuilds in the test body, and an
> `async Task` method's state machine lives inside its Task object, so the test's own frame pinned the
> `RebuildInput` (store) and `RebuildResult` (forest). With the awaits moved into a helper frame that
> dies before the clear, the residual is **0.3 MB** — the view models release the entire preview, store
> and forest included, at the cap with both trees built. The probe now asserts reachability next to the
> byte figure so that failure mode is visible rather than convincing.
>
> So the shipped exe's 62 MB floor is **UI layer** — Avalonia realized containers, TreeDataGrid rows,
> text/glyph and font-atlas caches — none of which headless has and none of which is preview data. It
> also explains run 2's higher peak (218 vs 174 MB managed) with no retention at all: run 2 starts from
> that floor rather than from startup idle. Whether Avalonia's post-grid steady state is worth chasing is
> a separate question from this document's.
>
> Two things tried and reverted rather than shipped as neutral diffs: resetting `PendingRebuild` in
> `Clear()` (the completed Task does release its state machine) and a `GC.WaitForPendingFinalizers()`
> between the trim's two passes (nothing is blocked on finalization).
>
> **11. Item (2): the 605 MB decomposed, and the frame-buffer half fixed.** The "rest" is no longer a
> subtraction. `DryRunStreamAllocationTests` measures one realistic deep-shape chunk (20,000 files →
> 58,340 wire records, a **7.4 MB** frame):
>
> | Phase | Cost | Per wire record |
> |---|---:|---:|
> | Frame buffer (`ReadFrameAsync`) | 7.4 MB | **133 B** |
> | JSON deserialize (`DeserializeResponse`) | 19.7 MB | **355 B** |
> | Fold into the store (`OnChunk`) | 3.9 MB | **71 B** |
>
> **Decode is 6.9× the fold.** The store's columnar folding — the thing three passes of work went into —
> is already the cheap part; the wire decode above it costs seven times as much. Scaled to the measured
> run (~730k wire records) that is ~97 MB of frame buffers and ~260 MB of deserialization against ~52 MB
> of folding, which accounts for the bulk of the 605 MB.
>
> Note the frame size: at 7.4 MB **every chunk frame was a Large Object Heap allocation**, and the LOH is
> uncompacted by default and only collected on a gen2 — the churn that becomes lasting committed memory
> rather than a transient read, which is the same finding Stage 1 recorded service-side.
>
> **Fixed:** `IpcFrameCodec.ReadFrameAsync(Stream, ArrayBufferWriter<byte>, ct)` plus
> `IpcSerializer.DeserializeResponse(ReadOnlySpan<byte>)`, and the client's streamed loop now clears and
> reuses one buffer for the whole run instead of allocating an exact-size array per frame. Both are
> *additive* — the array overloads are untouched, so no existing caller moves. This is the read-side
> mirror of the server's existing `SerializeResponse(IpcResponse, IBufferWriter<byte>)`, which exists for
> exactly this reason. §11's rejection of pooling this path was scoped to the service ("for no
> *service*-side benefit"); the client is the consumer that benefits.
> `Reading_frames_into_a_reused_buffer_costs_one_buffer_not_one_per_frame` gates it, and asserts both
> paths decode byte-identical payloads — deliberately using *descending* frame sizes, because a stale tail
> from a longer previous frame is the failure mode a reused buffer invites.
>
> **Still open — the biggest single number left: JSON deserialization at ~360 B/record (~260 MB/run).**
> Every chunk materializes a `DryRunFile`/`DryRunOperation` per row, the `List<>`s holding them, and a
> string per `FileName`, all of which the store folds and drops immediately.
>
> **12. What the deserialization cost is actually made of — two corrections.**
>
> **(a) The wire types are not records, and converting them to value types is a bad trade.** They were
> described above as records; `DryRunFile` and `DryRunOperation` are `sealed class` with mutable `set`
> properties and hand-written `IEquatable`, deliberately, because `DryRunStreamHandler` pools them
> (`_filePool`, `_fileListPool`, `DryRunStreamHandler.cs:460-588`) and the batched builder remaps indices
> in place rather than via `with{}`. Only `DryRunDirectory` is a record. On the merits:
> - *Polymorphism is unaffected.* `[JsonPolymorphic]` + the 16 `[JsonDerivedType]` entries sit on
>   **`IpcResponse`** (`IpcResponse.cs:10-26`); the payload types inside `DryRunChunkResponse` carry no
>   discriminator and have no derived types. (This would be a hard blocker if the *discriminated* types
>   were structs — STJ cannot do polymorphic serialization of value types at all — but they can't be.)
> - *The saving is ~24 B of ~360 B/record ≈ 7%*, not the "perhaps a third" claimed above: a `DryRunFile`'s
>   fields are ~48 B, so a struct saves the 16 B header plus the 8 B array slot and nothing else. The
>   `FileName` string is untouched.
> - *It would make LOH churn worse.* `List<DryRunFile>` goes from 8 B/element (160 KB per 20k chunk) to
>   ~48 B/element (~960 KB), and STJ grows a `List<T>` by doubling — six times the large-array traffic to
>   save 7%, in the same heap region item (2) above just stopped churning.
> - *It is not client-only.* Structs cannot be pooled, so `_filePool`/`_fileListPool` would have to become
>   array reuse, deleting an optimization documented as load-bearing for in-run peak commit, and touching
>   the engine, service and CLI.
>
>   **Verdict: no.**
>
> **(b) Strings are a minority of deserialization, not the bulk.**
> `Object_materialization_not_strings_dominates_chunk_deserialization` deserializes the same chunk twice,
> the second time with every `FileName` emptied and `Detail` nulled (STJ returns the interned
> `string.Empty` for `""`, so the stripped pass allocates no name strings): strings are **15–20%**
> (~3–4 MB of ~20 MB; the figure is a difference of two allocation counters, so it moves a few points
> between runs), and object materialization plus lists is **~80%**. And that 80% is **per-record**, not a
> fixed whole-payload cost — 345 B/record at 30,674 records vs 375 at 58,340 — so it is not polymorphic
> buffering.
>
> This inverts the rationale for the reader-based fold rather than removing it. The case is *not* "avoid
> the per-name string" (worth 15–20%); it is "skip STJ's per-record object materialization entirely"
> (worth ~80%), which a `Utf8JsonReader` loop writing straight into the store's columns would capture.
> It also means **candidate A stays closed**: the earlier note that a reader fold "un-rejects" the UTF-8
> name blob by also cutting a large slice of the transient was wrong — the name strings are only 15–20% of
> decode, so A remains a ~23 MB retained-heap change on its own merits.
>
> **13. The columnar wire, implemented (protocol v8).** A hand-rolled reader was the wrong first move:
> it would have added a second definition of the wire format that the compiler cannot check against the
> property declarations, when the per-record cost could be removed with *one* source-generated definition
> by changing the arrangement instead of bypassing the serializer. `DryRunChunkResponse` now carries a
> column per field (`DirectoryName`/`DirectoryParentIndex`, and `DryRunFileColumns`/
> `DryRunOperationColumns` groups) rather than lists of `DryRunDirectory`/`DryRunFile`/`DryRunOperation`.
>
> Measured, same fixture as finding 12 (20,000 files / 58,340 wire records):
>
> | | record-wise | columnar | |
> |---|---:|---:|---|
> | Wire frame | 7.39 MB (133 B/rec) | **3.6 MB** (62 B/rec) | **−51%** |
> | Deserialize | 19.72 MB (355 B/rec) | **11.1 MB** (199 B/rec) | **−44%** |
> | Decode ÷ fold | 7.2× | **3.7×** | |
>
> The wire shrank because a record-wise encoding repeats every property name once per record and those
> names were ~55% of the payload. Deserialization beat the −33% the costing experiment predicted, because
> production uses `List<T>` columns where the experiment used `T[]`: System.Text.Json fills a growable
> buffer and then copies it to an exact array, and `List<T>` skips that final copy.
>
> Scaled to the measured run (~730k wire records), and remembering the frame buffer is already pooled so
> its allocation is near zero: decode ~260 MB → ~145 MB, i.e. **~115 MB off the 605 MB (19%)**, plus half
> the wire bytes — which also cuts the service's serialize cost and pipe traffic, so its 292 MB improves
> too.
>
> **What the shape cost, and what it removed.** It removes an invariant records gave for free: columns
> within a group must agree in length, and ragged columns would pair one file's name with another's
> directory. `DryRunColumns.FindRaggedColumn` is the trust-boundary check, run *before* any positional
> read, rejecting as `IPC_MALFORMED`. Against that, it deleted the converter's four DTO pools
> (`_filePool`/`_opPool`/`_fileListPool`/`_opListPool`), their return paths, and the `FileName = ""`
> un-pinning — recycling is now `Clear()` on buffers that keep their capacity. `DryRunReport` and the
> record types are unchanged: the batched path has no production consumer left (no CLI), so the
> assembling sink rehydrates columns into records via `DryRunColumns.ToRecords`, which is documented as
> never-on-an-ingest-path.
>
> **The split moved again, as expected.** Post-columnar, deserialization is 11.1 MB of which strings are
> 4.0 MB (**35%**) and lists+reader 7.1 MB (64%) — the same 4.0 MB of strings as before, now a larger
> share of a smaller total. The guard added in finding 12 failed on exactly this and was the thing that
> flagged it. The test is now named for what it measures rather than for a conclusion, having had two
> conclusion-shaped names invert under it.
>
> **Still open: the `Utf8JsonReader` fold**, now worth ~11 MB/chunk rather than ~20 — it would capture
> both the 64% remainder and most of the 35%, since a reader can hash a name's UTF-8 bytes and only
> materialize strings the store has not already interned. Same drift caveat as before, same mitigation
> (reader in Contracts beside the types, differential conformance test, enum exhaustiveness).

> **Status: ANSWERED by paging, 2026-08-07. Read this note before the brief below it.**
>
> The brief asks how to shave the remaining ~72 MB of a resident preview. That question is closed, because
> the preview is no longer resident. `DryRunViewModel` now opens a run's plan as a HANDLE and reads a
> window of it (`get-run-plan-view` / `get-run-plan-page`); the rows live on the service, and the client
> keeps `PagedDryRunRowStore.MaxResidentPages` pages per tab.
>
> **Measured, by `DryRunViewModelMemoryTests`:**
>
> | | Retained with a preview open |
> |---|---:|
> | Whole-plan ingest, 500k rows (the figure this brief is about) | 72.1 MB |
> | Paged, 500k rows | **17.7 MB** |
> | Paged, 5,000,000 rows | **19.6 MB** |
>
> The second number is the one that matters, and it is not a 4× saving — it is a change of kind. Retained
> heap no longer tracks the plan's size at all: a ten-fold larger plan costs 1.9 MB more, which is the
> facet keys and the chip counts, not rows. The ~147 MB transient sort is gone outright, along with the
> `ComputeLoad` passes that produced it — the service decides the order during the plan's own walk
> (`RunSnapshotOrder`) and the client sorts nothing.
>
> **What this does to the questions below.** Question 1 ("is 72 MB a problem?") is moot at 17.7 MB flat.
> Question 2's breakdown described a store holding every row; a page store holds 512. Candidate A (the
> UTF-8 name blob) was worth ~22 MB against 72 MB retained — against ~17 MB total, of which names are a
> fraction of a fraction, it is not worth the UTF-8/UTF-16 ordering hazard. **Treat A as rejected.**
> Candidate C (the transient sort key array) no longer exists client-side.
>
> One finding from that list did land, from an unexpected direction: `ColumnBuffer`'s 8192-element
> segments were sized for a store of unknown length, and a page holds 512 rows — 94% of every column
> empty, multiplied by the resident page count. Sizing segments to the page took the paged figure from
> 76.7 MB to 17.7 MB. That is most of the saving above, and nothing in the brief predicted it.
>
> Still open and NOT answered here: the shipped exe's real process footprint (Question 1's three
> numbers). Those need a human — see the end of `docs/dry-run-paging-next-steps.md`.

---

> **Status: RESEARCH BRIEF, not a plan.** Nothing here is approved work. The deliverable is a
> recommendation with numbers behind it — including "stop here", which is a legitimate and
> possibly correct answer. Written 2026-08-03, immediately after the columnar pass landed.

## Why you are being asked

The UI used to reach 600 MB+ on large dry runs. Three passes have happened:

| Pass | What it did | Retained @ 500k, deep-path shape |
|---|---|---|
| (baseline) | — | 519 MB |
| 2026-07-15 | lazy display strings, interned roots, directory-table wire contract | 219 MB |
| 2026-07-15 addendum | forest built from shared directory structure, lazy leaves | (tree: 681 → 116 → 48 MB) |
| 2026-08-03 | columnar `DryRunRowStore`, handle rows, streaming ingest | **72 MB** |

Each pass had a clear, large target. This one does not — which is the point of the research.
Read `docs/dry-run-memory-optimization.md` first (all three passes are recorded there, most
recent at the top), then `docs/dry-run-service-memory.md` §11 for the service-side
"considered and rejected" list.

## Question 1 — the framing question, do this first

**Is 72 MB actually a problem?** Answer this before evaluating any optimization, because it
may make the rest moot.

Everything in the table above is a *retained-heap delta measured in a test host*
(`GC.GetTotalMemory` either side of `ApplyReport`). That is the right gauge for comparing
passes against each other, and the wrong one for answering "is the app usable on a
memory-constrained device". Nobody has measured the shipped UI's actual process footprint
since the columnar pass. What matters to a user is private commit / working set of the
NativeAOT `FileManager.UI.exe`, which is the managed heap *plus* the AOT runtime, Avalonia,
the render surface, font atlases, and the loaded profile state.

Concretely, find out:

1. **Idle footprint** of the published UI with no preview loaded.
2. **Footprint with a 500k preview on screen.**
3. **Whether it comes back down** after the preview is cleared, and after a second run. (The
   service needed `ConcurrentGarbageCollection=false` to return pages at all — see that
   csproj's comment. The UI has *no* GC configuration and has never been checked for the same
   behaviour. A UI that climbs monotonically across repeated runs is a worse problem than 72 MB.)

Two ways to get this, in preference order:

- **Ask Jacob to run it.** He drives the UI (he does not want the agent doing UI testing) and
  can read private bytes off Task Manager. Give him a precise script: publish, launch, dry-run
  a large profile, report the three numbers.
- **Add self-instrumentation.** `EngineHost` already logs resolved GC configuration and
  per-run memory for the service; mirroring that in the UI (a Serilog line after
  `ApplyPrepared` and after `ClearReport` with `GC.GetTotalMemory`,
  `GetGCMemoryInfo().HeapSizeBytes`, `TotalCommittedBytes`, `PrivateMemorySize64`) is cheap,
  permanently useful, and does not require a human in the loop for future passes. This is
  probably worth doing regardless of what you conclude.

**If the shipped app now sits comfortably on a constrained machine, recommend stopping.** Say so
plainly and close the question — a 30% cut of a number nobody notices is not worth the API churn
described below.

## Question 2 — where the remaining bytes are

Only if Question 1 says there is still a problem.

Here is a derived breakdown of the 72.1 MB at the deep-path shape. **It is arithmetic, not a
measurement — verify or refute it before trusting it.** The fixture is
`DryRunViewModelMemoryTests.BuildDeepReport`: 500,000 source files named
`render-output-0000000.png` (26 chars, all distinct), 20 per leaf directory, ~25,000 leaf
directories, paths ~100 chars; two of every three source files produce one destination
operation (~333k), and exactly two distinct `Detail` strings.

| Component | Est. | Reasoning |
|---|---:|---|
| Interned file names | ~38 MB | 500k distinct × 80 B (16 header + 4 length + 52 chars + 2 terminator, padded) |
| Source columns | ~15 MB | 500k × 30 B (2 ints, a ref, a long, 2 bytes, an int) |
| Materialized directory paths | ~8 MB | ~53k entries (both sides) × ~150 B |
| Operation columns | ~10 MB | ~333k × 29 B |
| CSR offsets | ~2 MB | `int[500_001]` |
| Both tabs' row-key arrays | ~3 MB | `int[500k]` + `int[333k]` |
| **Total** | **~76 MB** | against 72.1 MB measured — close enough that the model is probably right |

**The headline: file names are roughly half the remaining heap** (~53% deep, ~43% on the
shallow shape where names are shorter). Everything else is small enough that no single item is
worth a pass on its own.

**Verify it cheaply.** Build the store, then build a second one identical except that every
`FileName` is replaced with one shared constant string, and diff the retained heap. The delta
is the true name cost. Ten minutes of work and it either confirms the table or sends you
somewhere else entirely.

## Question 3 — the candidates

### A. UTF-8 name blob (the only one with real upside)

Replace `ColumnBuffer<string> _srcName` / `_opName` with a `byte[]` blob of UTF-8 bytes plus
`(int offset, ushort length)` per name. 26 ASCII chars becomes 26 bytes + 6 bytes of index
instead of an 80-byte string object plus an 8-byte reference.

- **Est. saving:** ~38 MB → ~16 MB deep-path; ~28 MB → ~12 MB shallow. Call it **72 → ~50 MB**
  and **65 → ~43 MB**. Confirm against your own measurement of the name cost first.
- **Cost — this is the real question.** Every consumer of a file name has to accept a span or
  pay a string allocation:
  - `DryRunPaths.PathContains(dirPath, fileName, term)` — the search predicate, runs per row
    per keystroke. Needs a `ReadOnlySpan<char>`/UTF-8 overload. It already has a hand-rolled
    joint-spanning tail scan and a dedicated benchmark (`DryRunPathsBenchmarks`, with a
    `SpanningJoint` case) — treat that benchmark as the regression gate.
  - `DryRunSort.RelativeKey` — builds the sort key per row at load. Currently produces a
    `string`; would need to build into a pooled buffer or produce a comparable span.
  - Display reads (`FileName`, `SourcePath`, `ParentDisplay`) allocate a string per access.
    That is fine — only realized rows hit them — but confirm nothing on a hot path reads a
    name expecting it to be free.
  - Non-ASCII names must not break. UTF-8 length ≠ char count; the sort comparison is
    `OrdinalIgnoreCase` over UTF-16 today, and byte-wise UTF-8 comparison is **not** equivalent.
    This is the subtle correctness risk in the whole idea — think it through before costing it.
- **Verdict to reach:** is ~22 MB at the 500k cap worth span-ifying two utility APIs and taking
  on a UTF-8/UTF-16 ordering hazard? Have a defensible opinion either way.

### B. Everything else — probably not, say so explicitly

Listed so the next reader does not re-derive them:

- **Narrow `RootDirIndex` to a byte.** A file's root is one of a handful of configured source
  or target paths, so a small root table would fit in a byte instead of a 4-byte index into
  `dirPaths`. Saves ~1.5 MB. Not worth a column-format change.
- **Drop the op-name column when it equals its source's name.** Was considered and rejected
  during the columnar pass: it needs an owner-source column (4 B/op) to resolve the name back,
  so it trades 8 B/op for 4 B/op — ~1.3 MB here — and reintroduces a lookup on a hot path.
  Interning already collapses the *strings*; only the references remain.
- **Pack `_srcKind` and `_srcDisposition` into one byte.** Saves 0.5 MB. No.
- **Derive `_opSize` instead of storing it.** It is derived at `Complete()` from the source and
  subject columns, which are then dropped. Keeping those to drop this is a wash.

### C. The transient sort key array (peak, not retained)

`ComputeLoad` materializes `string[] keys` — one relative-path string per row, ~30 MB at the
cap — sorts an index array against it, then drops it. It is the largest remaining *transient*.
Options: sort with a comparator that computes keys on demand against the existing per-partition
`relDirCache`, or store a per-row relative-directory id and compare `(dirId, name)` pairs.
Worth evaluating only if Question 1 identifies a *peak* problem rather than a retained one. Note
`DryRunSort` has a documented ordering subtlety — see `docs/dry-run-service-memory.md` §11's
fragment-wise-comparator caveat: a tuple comparison is **not** equivalent to
`string.Compare(fullA, fullB, OrdinalIgnoreCase)`, and getting it wrong silently reorders the
preview. Any change here needs a property test against the reference comparison.

### D. GC configuration (Tier 0, but tread carefully)

The service sets `ConcurrentGarbageCollection=false` after measuring it (peak 234 → 326 MB,
settled 231 → 12 MB — the trade goes both ways, and the comment in
`src/FileManager.Service/FileManager.Service.csproj` is worth reading in full). The UI has no
such configuration. **Do not copy it blindly:** blocking gen2 collections in a process with a UI
thread are visible jank, which is a different and worse cost than a background service pays.

Also note Jacob's standing preference, recorded in `docs/dry-run-service-memory.md` §13:
*"Attempting to manually control gc mid-run seems like a code smell. I want to avoid generating
the garbage in the first place via pooling and other memory management strategies."* An explicit
collect is acceptable only at preview-close (a moment the user already expects to be a
transition), never mid-run, and only with a measurement showing it does something.

## How to measure

- **Retained:** `dotnet test tests/FileManager.UI.Tests/FileManager.UI.Tests.csproj -c Release
  --filter "Category=Memory" --logger "console;verbosity=detailed"`. Three probes in
  `DryRunViewModelMemoryTests`; each prints its bytes. Current: 65.5 MB shallow, 72.1 MB
  deep-path, 48.5 MB tree toggle. Budgets 95 / 100 / 60 MB.
- **Allocation and time:** `dotnet run -c Release --project benchmarks/FileManager.UI.Benchmarks
  -- --filter "*DryRunViewModelBenchmarks*"`. Takes ~15 min. Current at 500k: `ApplyReport`
  838 ms / 149,709 KB, `IngestReport` 203 ms / 77,906 KB, `PrepareReport` 557 ms / 71,783 KB,
  `ToggleStatusFilter` 12.6 ms / 3,257 KB. Artifacts are gitignored, so record numbers in the
  doc, not the repo.
- **End-to-end process footprint:** `tools/FileManager.MemoryProbe` measures the *service* and
  now also its own client-side peak (`--client-ingest sink|report`). It does **not** measure the
  UI — it cannot, since `DryRunRowStore` lives in `FileManager.UI` and the probe deliberately
  references Contracts only. Question 1 needs either Jacob or new UI instrumentation.
- **No baseline exists for `ToggleStatusFilter`** — the pre-columnar committed report carried
  only `PrepareReport` rows. If you need one, stash and re-run.

## Constraints that must not break

Load-bearing, both pinned by `tests/FileManager.UI.Tests/DryRunRowStoreTests.cs`:

1. **Row handles are values, not identities.** `DryRunFileRow` is a record over
   `(DryRunRowStore Store, int Index)`; the bound lists build a handle per indexer access, so
   ListBox selection, `IndexOf` and container recycling all depend on two handles for the same
   row being `Equals`. **Never add a field to a row record that joins its generated equality** —
   that is exactly why lazy row projection was rejected in the July pass (the row held a nested
   list compared by reference) and why `Actions` is an `init` property rather than a positional
   member.
2. **`DryRunRowList<T>` must implement non-generic `IList`.** Avalonia's `ItemsSourceView` uses
   it for indexed access and otherwise copies the whole source into a list, materializing every
   handle and undoing the entire design. Before the columnar pass this held by accident, because
   the bound instance was a `List<T>`.

Plus: both exes are `PublishAot`, so no runtime reflection outside the source-generated
`FileManagerJsonContext`; and four `x:CompileBindings="False"` regions in `DryRunView.axaml`
(lines 62, 111, 506, 644) bind row and tree command names by reflection, making
`IDryRunFileRow`'s five commands a de-facto public API.

## What to hand back

A short recommendation, not a patch:

1. The Question 1 numbers, and whether they say to stop.
2. A verified (not derived) breakdown of the remaining retained heap.
3. For candidate A only: estimated saving, the UTF-8/UTF-16 ordering analysis, and a cost in
   files-touched — then a yes or no.
4. Explicit "not worth it" on B, so nobody re-derives them.

If the answer is "stop", say so in one paragraph and add it to
`docs/dry-run-memory-optimization.md` as a closing note, so the next person does not start this
over.
