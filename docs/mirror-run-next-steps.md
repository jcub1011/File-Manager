# Snapshot-driven runs & Mirror: remaining work

**Last updated:** 2026-08-04
**Companion to:** [`specs/implementation-progress.md`](specs/implementation-progress.md) (status board) and
[`specs/architecture-v1.md`](specs/architecture-v1.md) §10.1 (the shape as built).

Mirror shipped in the "Set 3c" increment: a run plans its work into a frozen snapshot via the same
`IProfilePlanner` the dry-run preview uses, waits for approval, then copies and — under
`SyncMode.Mirror` — recycles the destination orphans the plan named. 1436 tests pass; the Mirror
vertical is covered end-to-end over a real named pipe against the production DI graph.

This document is what is *not* done, in the order I would do it. Item 1 was a real defect and is now
fixed (kept here for the diagnosis); everything else is additive.

---

## Priority summary

| # | Item | Why now | Size |
| --- | --- | --- | --- |
| ~~1~~ | ~~[Placer leaves `.fmtmp-` temps when sibling cancellation races rollback](#1-fixed-jobexecutiontargets-was-built-by-a-torn-lazy-initializer)~~ | **DONE** — root cause was a torn lazy initializer in `JobExecution`, not the placer | — |
| 2 | [Decide the ratio-guard override](#2-decision-the-ratio-guard-has-no-override) | Blocks confident use against real archives | S (after a decision) |
| ~~3~~ | ~~[Itemized approval view](#3-done-itemized-approval-view-in-the-preview-tab)~~ | **DONE** — the dry-run tab became the Preview tab and its footer approves the run | — |
| 4 | [Cancel affordance in the activity panel](#4-cancel-affordance-in-the-activity-panel) | `cancel-run` works but has no button *during execution* — the planning phase now has one | S |
| 5 | [Reconcile history (`get-recent-reconciles`)](#5-reconcile-history-get-recent-reconciles) | A deletion pass is invisible in the activity list | S–M |
| 6 | [Remove emptied directories at a Mirror destination](#6-remove-emptied-directories-at-a-mirror-destination) | Destination accumulates an empty skeleton | S |
| 7 | [Whole-profile reconcile for automation](#7-whole-profile-reconcile-for-automation) | Scoped runs never reconcile; needs Set 5 or a new request | M, design first |
| 8 | [Volume-aware dispatch](#8-volume-aware-dispatch) | Helps every multi-source run; no identity or disposition questions | S–M |
| 9 | [Duplicate source election](#9-duplicate-source-election-throughput-driven) | Bigger win, but gated on source-disposition semantics | M–L, decision first |

---

## 1. FIXED: `JobExecution.Targets` was built by a torn lazy initializer

**Status:** fixed. Pre-existing, *not* introduced by the snapshot/Mirror work.
**Severity (as found):** worse than the reported symptom — see "what it actually cost" below.

### What the hypothesis in this section used to say, and why it was wrong

This section previously asked whether `TargetProgress.TempPath` was assigned before or after the copy,
and proposed "record-then-write" as the fix. **That discipline was already in place** and was never the
problem: `AtomicPlacer.cs` journals `twb` (which carries the temp path), *then* sets
`tp.TempPath`/`tp.State = TempWriting`, *then* starts the copy that creates the file. The window
between the record and the assignment contains no `await`, so it is not a cancellation window at all.

### The actual root cause

`JobExecution` built its two derived members lazily:

```csharp
public JobStateMachine States => _states ??= new JobStateMachine(Plan.JobId);
public IReadOnlyList<TargetProgress> Targets => _targets ??= BuildTargets(Plan);
```

`??=` is not atomic. `JobExecutor.DistributeAsync` fans out one `Task.Run` per target and **every one
of them touches `Targets` for the first time simultaneously**, so several threads each ran
`BuildTargets` and only the last write to `_targets` survived. Every target that then mutated a
*discarded* `TargetProgress` was invisible to everyone else.

`JobExecutor.RollBackAsync` snapshots `execution.Targets` into `TargetRollbackItem`s, so it read
`State = Pending` and `TempPath = null` for targets that had really reached `Staged`.
`RollbackExecutor.RevertTarget` maps `Pending` to `RollbackAction.None` — it did nothing, and the temp
stayed on disk. The journal proves it; here is the captured reproduction:

```
twb  target=2 / twb target=1 / twb target=0      <- all three began writing
tver target=0 / tver target=1                    <- t1 and t2 verified
tstg target=1 / tstg target=0                    <- ...and staged
rbbegin  failedTarget=2
trb  target=0 action=None                        <- rollback saw them as Pending
trb  target=1 action=None
trb  target=2 action=RemovedTemp                 <- only the thread that won the race
close outcome=Failed
```

That is why it read as flaky and load-dependent: whether a given target's mutations survived depended
on which thread won a race.

### What it actually cost

Leaked temps were the visible half. The same lost state meant **a target that reached `Placed` could be
skipped by rollback entirely** — this job's content left at the destination, the prior version left in
`.fm_staging`, and the job still reporting "rolled back cleanly". The reported 1-in-8 test failure was
the mildest symptom of the bug, not its extent.

### The fix

`src/FileManager.Core/Jobs/Job.cs` — `States` and `Targets` are now built **eagerly in `Plan`'s `init`
accessor**, which runs once on the constructing thread before the execution is published. No lazy
initialization, no race, and no call-site churn (the only construction site is an object initializer).

Three further defects found on the same path while diagnosing, each fixed and each with its own test:

- **`AtomicPlacer` never reclaimed its own temp.** It now deletes it in the existing `finally` on any
  exit that did not place it. Rollback and crash recovery still reclaim it too — this is the earliest
  attempt, not the only one, and it runs while the target still holds its path lock.
- **`RollbackExecutor.RevertStaged` chained its two steps with `??`**, so a failed restore skipped the
  temp delete. They are independent and both now always run. Residual paths are also confirmed against
  the filesystem rather than defaulting to `FinalPath`, which pointed the user at a file that is
  usually fine and hid the artifact that is not.
- **`DistributeAsync` treated "every target cancelled, none reported an error" as success.** Since
  `PlaceOneAsync` reports cancellation as no-error, an *external* cancel made the job journal
  `job-committed` and **dispose the source with nothing placed** — confirmed empirically by the new
  test, which permanently deleted the source before the fix. Latent in production only because
  `JobOrchestrator` passes `CancellationToken.None` (I-ATOMIC-JOB). Cancellation now routes to
  rollback, a faulted target task becomes a `JobError` instead of unwinding past the rollback branch,
  and `ExecuteAsync`'s catch-all attempts the sweep before giving up.

### Tests

`JobExecutionTests` (new) pins the shared-state invariant with threads released together;
`AtomicPlacerTests.A_cancelled_placement_removes_the_temp_it_created`;
`RollbackExecutorTests` gains the failed-restore and residual-naming cases;
`JobExecutorFailureRollbackTests` gains `A_job_cancelled_mid_placement_leaves_no_temp_behind` and
`A_cancelled_job_never_commits_and_never_disposes_the_source`. Each was confirmed to fail against the
unfixed code. `JobExecutorHarness.ExecuteWithRetriesAsync` is back to the cheaper delay-then-advance
loop and its workaround comment is gone; `JobExecutorFailureRollbackTests` ran 25/25 clean.

---

## 2. DECISION: the ratio guard has no override

`MirrorDeletionPass.RefuseOnRatio` refuses a pass that would remove more than
`EngineConfig.MirrorMaxDeleteFraction` (50%) of the files swept under one target root, once at least
`MirrorRatioFloor` (20) orphans are involved.

**Why it exists:** it is the only gate that catches the two mistakes every *other* gate reports green
for — a `TargetLayout` flip between `PreserveStructure` and `Flatten` (which legitimately makes every
existing destination file an orphan), and a source share that remounted empty. Both produce a plan that
is internally correct and catastrophic.

**Why it needs a decision:** it will also refuse a legitimate large cleanup — the user really did delete
half their source tree — and there is currently **no way to say "yes, I meant it"**. The first person
to hit that is stuck.

Options, roughly in increasing cost:

- **Config escape hatch.** `EngineConfig.MirrorMaxDeleteFraction = 1.0` disables it. Already possible in
  code but `EngineConfig` is registered with defaults only and is not user-editable, so today this means
  a rebuild.
- **A `force` flag on approval.** `ApproveRunRequest` gains `AcknowledgeLargeDeletion`; the coordinator
  passes it through and the pass skips only the ratio gate (never the others). Fits the existing
  acknowledge-a-blocking-warning pattern, and the approval dialog is already the place where the count
  is shown.
- **Per-profile setting.** Most discoverable, most surface area (schema, validation, UI, back-compat).

My recommendation is the `force` flag: the user is already looking at "14 files to REMOVE" in the
approval prompt, so that is the honest moment to ask "this is more than half of that folder — sure?".

Files: `src/FileManager.Core/Runs/Reconcile/MirrorDeletionPass.cs` (`RefuseOnRatio`),
`src/FileManager.Core/EngineConfig.cs`, `src/FileManager.Contracts/IPC/IpcRequest.cs`,
`src/FileManager.Core/Runs/RunCoordinator.cs`.
Tests: `MirrorDeletionGateTests.The_RATIO_guard_deletes_NOTHING_when_most_of_a_target_root_would_go`
already pins the refusal; add the override case beside it.

---

## 3. DONE: itemized approval view in the Preview tab

**Status:** shipped. The Dry Run tab became the **Preview** tab, and it is now the approval view — the
counts dialog is gone, and so is the pre-flight blast-radius dialog.

The decision that made it small: rather than feeding a standalone preview into a run, **the preview IS
the run's planning phase**. `RunCoordinator.Begin` already drove the same `IProfilePlanner` and froze the
result, so nothing needed to be recomputed or persisted — `get-run-plan-stream` replays that snapshot as
the identical `DryRunChunkResponse` frames, which is exactly what it was built for.

What that took:

- **Protocol 10.** `RunProfileRequest.InlineProfile`, mirroring `DryRunStreamRequest.InlineProfile`, so a
  run can be planned from the editor's unsaved draft. `RunProfileHandler` uses it in place of the catalog
  lookup; every gate (`Active`, `Sources`, scope containment) still applies to whichever profile resolved.
- **A frozen profile executes the run.** `IRunSettleSink` gained `PlannedProfile(runId)`, and
  `JobOrchestrator.RunJobAsync` prefers it for a run-tagged payload. This was **a pre-existing defect**,
  not a cost of the draft path: the copies read the live catalog while the Mirror deletion pass read the
  snapshot, so a profile edited while a run awaited approval changed the copies and not the deletions.
  A draft-planned run merely made it reachable on purpose (its profile is in no catalog at all, so every
  payload would have been dropped). Consequence to know: a run-tagged payload no longer stops because its
  profile was deactivated mid-run — the user approved *this* plan, and `cancel-run` is how it stops.
- **UI.** `IIpcGateway.GetRunPlanStreamAsync` (the old `DryRunAsync` is gone — one path now);
  `DryRunViewModel.LoadPlanAsync` reusing the whole ingest path unchanged (store, epoch guard, off-thread
  prepare, allocation sample points); a footer with **Approve Run** / **Discard**;
  `MainWindowViewModel.PreviewProfileAsync` replacing `RunProfileNowAsync`.
- **Lifetime.** A run parked in `AwaitingApproval` holds a snapshot directory and has no expiry, so an
  unanswered preview is declined on every abandonment path: a superseding preview, profile switch or
  close, and window close.

### The trap in this design, found twice after the first pass

**A snapshot's executable halves are not its displayable ones, and the difference is invisible until a
panel is empty.** `copies.ndjsonl` lists only files there is *work* for and records where each is READ
from; `deletes.ndjsonl` lists only orphans. Both are exactly right for execution and neither can be shown
to anyone. Rendering the tab from them produced, in two rounds of the same mistake:

- source rows with an empty target fan-out, and no destination side at all for any profile that removes
  nothing (every additive profile);
- then no *source* side at all for a profile already up to date — a Mirror profile that is fully
  synchronized has zero copy items, so the panel went blank exactly when the honest answer was "these
  files, every one already up to date".

Both read to the user as **"the preview found nothing"**, which is the one thing an approval view must
never be ambiguous about.

The fix is two display-only files beside the executable ones — `sources.ndjsonl` / `RunSourceItem` and
`destinations.ndjsonl` / `RunDestinationItem`. Everything they hold was already flowing through
`RunSnapshotWriter.Consume` and being dropped. `sources.ndjsonl` records **every source the plan looked
at**, whatever it decided, in plan order — so an item's ordinal IS the source index the plan assigned it,
and a destination row names its source by that ordinal directly. Nothing in execution reads either file;
orphans stay in `deletes.ndjsonl` so the deletion pass still touches only the small half it needs.

`GetRunPlanStreamHandler` therefore reads the **display** halves and never `copies.ndjsonl`. If a future
change points it back at the executable ones, the panels go blank again in exactly these two ways.

**Why no test caught it.** Every row-projection test scripted a rich `DryRunReport` through the fake's plan
stream, so the handler was only ever asked to replay data the real writer never produced.
`GetRunPlanStreamHandlerTests` now runs the **real** planner into the **real** writer for that reason, with
the already-synchronized-Mirror and additive-profile cases as its first two. `DryRunRowStoreTests` pins the
frame split a replay uses (all sources, then all destinations), which a preview's per-file bundles never
exercise.

One behaviour change survives all of this: the scan-choice split button is gone. A run plans with the
profile's own `ScanDestination`, so there was no wire path for a per-run override and no reason to invent
one.

Still open, and cheap now that the plan is on screen: the ratio-guard override (§2) has an obvious home —
an `AcknowledgeLargeDeletion` checkbox in this footer, beside the count it is about.

---

## 4. Cancel affordance in the activity panel

`cancel-run` works end to end (`CancelRunHandler`, `IRunCoordinator.Cancel`,
`IIpcGateway.CancelRunAsync`, `ITriggerQueue.DropRun`) and is covered by
`RunLifecycleTests.Cancelling_mid_execution_drops_the_queued_work_and_skips_the_deletion_phase`.

The **planning** phase now has its button: `DryRunViewModel.CancelPreviewCommand`, shown on the Preview
tab while `PlanningRunId` is set. What is still missing is a cancel for a run already **executing** —
`ActivityViewModel` has no cancel command.

**To do:** a run-level row in `ActivityViewModel` showing phase and progress against the known total
(`RunProgressEvent` carries `Completed` / `Total` / `Deleted`), with a Cancel command wired to
`CancelRunAsync`. `MainWindowViewModel.HandleEngineEvent` already routes `RunProgressEvent` to a notice
string — that is where the row should be fed from instead.

Semantics to preserve in the copy: cancelling drops work that has not started, but **jobs already in
flight always finish** (I-ATOMIC-JOB), and the deletion phase is skipped entirely.

---

## 5. Reconcile history (`get-recent-reconciles`)

A deletion pass does not appear in the recent-jobs list. This was deliberate:
`JobSummary`/`JobSummaryDto` require a `SourcePath` and a `JobOutcome`, and a pass has neither —
stuffing a synthetic string into a typed field is the kind of small lie that later reads as truth.

What exists instead: the pass reuses `JobId` for its identity precisely so it is drillable with
`get-job-log` on its pass id (`MirrorDeletionPass.Log` narrates opened / deleted / skipped / aborted /
closed), and every deletion is permanently recorded in `audit/mirror-YYYYMM.ndjsonl` via
`IReconcileAuditLog.ReadRecent`.

**To do:** a `get-recent-reconciles` request returning the pass's own shape (pass id, profile id, run
id, outcome, deleted/skipped counts, bytes, timestamp), plus an activity-panel section for it. The
audit log already answers most of it; the alternative is an in-memory ring beside
`IJobLogStore.RecordSummary`.

---

## 6. Remove emptied directories at a Mirror destination

The destination sweep yields **files only**, so a Mirror run that removes every file from a subtree
leaves the (now empty) directory skeleton behind forever. Pinned as a decision, not a bug, by
`MirrorDeletionFileOperationTests.Empty_directories_left_behind_are_NOT_removed`.

**To do:** after a successful pass, walk the directories that contained deleted orphans bottom-up and
remove those that are now empty.

Guards this must respect — all of which the file path already handles and a directory path would need
to re-establish:

- Never remove a target **root** itself, however empty.
- Never descend or remove a reparse point (the sweep refuses to judge them; `OperationKind.Unknown`).
- Never touch an infrastructure directory (`InfrastructurePaths.IsInfrastructureDirectoryName` —
  `.fm_staging`, `.pipeline_tmp`).
- "Empty" must mean empty *now*, checked at the moment of removal, not empty according to the plan.
- A directory the user deliberately created and left empty at the destination is indistinguishable from
  one this run emptied — so scope removal to directories that actually contained a deleted orphan, and
  update the test above to match whatever is decided.

---

## 7. Whole-profile reconcile for automation

**Current limit:** an orphan is "a destination file no source writes to", which can only be decided over
the *complete* source set. So a run narrowed to a path copies but **deletes nothing** — the pass refuses
with "this run covered only …, not the whole profile"
(`MirrorDeletionPass.Refuse`, pinned by `MirrorDeletionGateTests.A_SCOPED_run_deletes_NOTHING`).

The GUI always sends a whole-profile run (`Path: null`), so interactive use is unaffected. What has no
path to reconciliation is anything that triggers on a subtree: the shell context menu, and eventually
the watcher.

**Options:**

- **Set 5's scheduler** drives a whole-profile run on a schedule. This is the natural home and is
  already on the roadmap — probably the answer.
- **A `reconcile-profile` request** for a deletion-only whole-profile pass, for a client that wants to
  reconcile without a full copy run.

Either way this wants a short design note first: what triggers it, how it interacts with a watcher's
per-file runs, and whether a deletion-only pass still needs the copy barrier (it does not, but it does
still need to know the source set is completely readable).

---

## 8. Volume-aware dispatch

**Supersedes the original "rank drives by media type" idea.** That plan was to probe NVMe / SATA SSD /
HDD via `DeviceIoControl` and schedule reads by an estimated speed ranking. It is abandoned: media type
is a poor proxy for throughput (a saturated NVMe is slower than an idle HDD; a 10GbE share beats a local
USB disk), and a static probe cannot see queue depth, competing processes, NAS congestion, SMR write
cliffs, thermal throttling, or a drive that is degrading. **Let observed availability decide instead.**

The replacement idea, and the one worth doing first, needs no identity check, no disposition change, no
journal change, and no hardware probing:

> Spread in-flight reads across source volumes rather than letting the FIFO concentrate them.

`TriggerQueue` is strict FIFO and volume-blind; `JobOrchestrator` runs an anonymous `MaxWorkers` pool
that pulls from it. A run whose payloads happen to arrive in long same-source runs therefore reads one
drive hard while another sits idle. Making dispatch volume-aware fixes that for **every** multi-source
run, not just ones with overlapping content.

**Self-calibrating without measuring anything.** Dispatch on *least outstanding read work per volume*,
keyed by `IVolumeInfoProvider.GetVolumeKey`. A fast volume drains its in-flight work sooner, so it looks
more available, so it receives more. That is the whole calibration, and it is strictly better than an
EWMA of measured MB/s: on small files, measured rate is dominated by per-file overhead, so a
throughput-tracking signal calibrates on noise. It also adapts to degradation for free, which was the
motivating case.

**Precedent to follow:** `ScanScheduler` / `ScanThreadResolver` already do per-volume concurrency
budgeting for enumeration, including `DriveClass` overrides from settings. Same volume key, same shape.

**To do:** a volume-aware dequeue. Either give `ITriggerQueue.DequeueAsync` a "prefer a payload whose
source volume has the least outstanding work" selection step, or keep the queue FIFO and have the
orchestrator maintain per-volume in-flight counts and pick accordingly. The latter is less invasive and
keeps the coalescing FIFO semantics intact.

**Sort long-first where there is a choice.** Greedy pull scheduling has a known tail failure: the last
large item lands on the slowest volume and dominates makespan. Descending-size dispatch (LPT) bounds
that at 4/3 of optimal.

**Testability:** assert properties, not assignments — every file dispatched exactly once, no volume
exceeding its in-flight budget, a stalled volume stops receiving work. `TimeProvider`/`FakeTimeProvider`
are already injected throughout.

---

## 9. Duplicate source election (throughput-driven)

Where two or more Sources of a profile hold the same file, the run may read it from whichever volume is
free. This is the narrower, higher-value half of the idea — and it is **gated on a semantics decision**,
not on any technical unknown.

### What the code already gives us

Two findings that make this far safer than it first looks, both verified:

1. **The destination is source-independent.** `TargetPathLayout.For` forces `Flatten` whenever
   `Sources.Count > 1`, so with multiple sources the destination is the bare filename regardless of which
   source wins. Deferring the choice therefore cannot perturb destination paths, and so cannot perturb
   the Mirror survivor set or the orphan list. The snapshot stays trustworthy.
2. **The M:1 priority rule is order-independent.** `ConflictResolver.PriorityKeepsExisting`
   (`ConflictResolver.cs:89-101`) skips the incoming file only when `sourceIndex > placedByIndex`. So if
   the priority source lands first the later one is skipped, and if it lands *second* it **overwrites**.
   Either way the destination ends up with the priority source's content. Election cannot silently write
   the wrong content under `Overwrite`/`OverwriteIfNewer` — spec §3.4 is enforced downstream by
   `SourcePriorityRegistry`, not by dispatch order.

**Identity is already paid for.** `JobExecutor.SealSourceAsync` (`JobExecutor.cs:341-349`) hashes the
source unconditionally under `Sha256`/`XxHash128`, whatever the destination looks like, and
`AtomicPlacer.CheckUnchangedAsync` hashes the existing final file to compare. So for a duplicate pair
both sources are *already* fully read today — the hashes exist, they are simply not shared between the
two jobs. There is no "hash both to prove identity" cost to pay.

The one gap: the **plan** phase only hashes a source when there is an existing destination to compare
against (`DryRunEngine.cs:1135-1161`), so identity is not known *before* dispatch. That turns out not to
matter, because the existing unchanged-check is itself the identity oracle, applied after the fact.

### Where the win actually is

Because both sources are read today regardless, **reordering alone saves nothing.** There are exactly two
places a saving can come from:

**(a) Elect who does the double read.** The job that reaches an empty destination first does the full
copy — its source is read twice (seal hash, then hash-on-write copy). The one that arrives second hashes
its source once, finds the file already correct, and skips. Electing the *fast* volume to go first gives
`fast x2 + slow x1` instead of `slow x2 + fast x1`.

Safe, no collapsing, no semantic change — but it only pays under **`ConflictResolution.Skip`**, where
first-arriver wins outright. Under `Overwrite`/`OverwriteIfNewer`, priority pins it: the priority
source's content must land, so that source has to do a full copy whatever the order, and if a fast
lower-priority source goes first you get *two* full copies. Election is neutral at best there,
counterproductive at worst, unless the interchangeable-replicas assertion below relaxes priority.

**(b) Collapse the candidates into one job** and never read the losers. This is the real win — and it is
what the decision below gates.

### THE GATE: source disposition

Today a duplicate pair is **two payloads, two jobs**, and each job disposes *its own* source. With
`OnSuccess = MoveToTrash` or `PermanentDelete`, **both** source copies are disposed and the destination
keeps one file.

Collapse them into one job and only the elected source is disposed. The loser is left sitting there — a
user who set `PermanentDelete` expecting their staging drives emptied would find one still full, and the
deletion audit trail would agree with the wrong picture.

Fixable, but not incidentally. "Dispose all replicas" requires:

- `JobOpenedRecord.Source` (a single `SourceSnapshot`) to become a set — a journal shape change.
- One `DispositionAuditRecord` row per disposed source, so the no-loss trail names every file removed.
- `ISourceDispositionService` to dispose a set rather than `JobExecution.Plan.Source`.

**Nothing else blocks collapsing.** This decision does.

### Two smaller semantic notes

- **`RenameSuffix` must be excluded.** A duplicate pair legitimately produces *two* destination files
  (`name.ext` and `name (1).ext`). Collapsing would produce one. Election is only meaningful for
  `Overwrite` / `OverwriteIfNewer` / `Skip`.
- **`ConflictResolution.Skip` does not consult priority at all** (`ConflictResolver.cs:48-51`) — first
  arriver wins. Today the winner is already decided by path-lock acquisition order, so election does not
  *introduce* nondeterminism there; it makes the bias explicit and deliberate.

### The interchangeable-replicas assertion

To get the saving under `Overwrite`/`OverwriteIfNewer` — where priority otherwise decides who copies —
the profile needs to say the sources are equivalent. A per-profile flag ("treat these Sources as
interchangeable replicas") converts a correctness question into a user assertion: it is cheap, it is
honest, it confines the §3.4 relaxation to profiles that opted in, and it is the natural home for the
"dispose all replicas" semantics above.

### Bonus this design enables: read failover

Once a file has more than one known-good source, a read that fails mid-copy (bad sector, dropped share)
can transparently retry from a sibling. `ITransientRetryPolicy` currently retries the *same path* 3×2 s;
falling back to another replica is strictly better, and it also fixes the one thing pull scheduling
cannot self-correct — an item already dispatched to a pathologically slow volume, which nothing
reassigns.

### The seam needs to move

There was a plan-time seam for this — `ISourceSelector`, with a `PrioritySourceSelector` that reproduced
the existing §3.4 rule and a DI registration — and it has been **deleted**: nothing ever injected it, no
test exercised `Select`, and its own doc conceded the interface would be re-shaped rather than
implemented. A seam in the wrong place is worse than none, because `PrioritySourceSelector.Rank` could
drift out of step with the `JobPlan.PriorityIndex` rule it mirrored and no test would notice.

Two conclusions from that design worth keeping:

- **Rank by live load, not by media type.** Ordering volumes NVMe > SATA SSD > HDD was considered and
  abandoned: media type is a poor proxy for throughput, and no static probe sees queue depth, competing
  processes, NAS congestion, or a drive that is degrading. Dispatch instead on *least outstanding read
  work per volume* (keyed by `IVolumeInfoProvider.GetVolumeKey`) — a faster volume drains its in-flight
  work sooner, so it looks more available, so it receives more. That self-calibrates with nothing to
  measure and no hardware probing.
- **So the decision cannot be made at plan time at all**, which is why the seam belonged at dispatch. The
  load-bearing property survives either way: `RunCopyItem.SourceIndex` records what actually happened, so
  the snapshot and the audit trail agree with reality even though the choice was not made in advance.

### Scope reality check

Scheduling freedom exists *only* for files contested by two or more Sources — for distinct files there is
no choice, since each must come from its own Source. A "Documents + Photos" profile with disjoint sources
gets **nothing** from §9. Its value is a direct function of overlap, which is why §8 (which helps every
multi-source run) should come first.

---

## Verification recipes

Build and full suite:

```
dotnet build File-Manager.slnx
dotnet test File-Manager.slnx
```

Expect **1465 tests, 0 failures** and **19 warnings** (the pre-existing baseline: CA1416 in
Platform.Windows.Tests, xUnit1031/2031 in Core.Tests, and one CS9107 in
`src/FileManager.Core/Files/FileSystemService.cs`). Note the "src is warning-free" claim in older docs
is stale — that CS9107 predates this work.

Manual Mirror check against the checked-in scenario environment:

```powershell
powershell -ExecutionPolicy Bypass -File tests/FileManager.Scripts.Windows.Tests/New-MirrorEnv.ps1
```

Point `EnginePaths.Root` at `tests/FileManager.Scripts.Windows.Tests/mirror`, press **Preview** in the
GUI, and confirm:

1. It lands on the Preview tab and the footer names 2 files to remove, with **nothing moved yet**. Both
   panels have rows: sources with their targets listed, destinations with the orphans. Then check the two
   empty-panel cases specifically — a non-Mirror profile (its Destinations tab must show where each copy
   lands) and a Preview run twice in a row (the second must still list every source, as unchanged).
2. Editing the draft without saving and pressing Preview again plans the NEW draft, and leaves no stray
   directory under `EnginePaths.RunsDirectory` for the superseded run.
3. **Approve Run** recycles `stale-orphan.txt` and `old\deep-orphan.txt` — check the Recycle Bin, not just
   their absence.
4. `audit/mirror-YYYYMM.ndjsonl` has a row per deletion.
5. Previewing again says "Nothing to do" with no footer, and leaves no pending run (idempotency).
6. Repeat with `MirrorDeletion.Proactive` — the orphans should go *before* the copies land.
