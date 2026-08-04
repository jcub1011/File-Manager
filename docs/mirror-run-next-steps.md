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
| 3 | [Itemized approval view](#3-itemized-approval-view-in-the-dry-run-view) | The approval step currently shows counts, not rows; the IPC is already done | M |
| 4 | [Cancel affordance in the activity panel](#4-cancel-affordance-in-the-activity-panel) | `cancel-run` works but has no button | S |
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

## 3. Itemized approval view in the dry-run view

**Already in place — the engine half is done and tested:**

- `GetRunPlanStreamRequest` / `get-run-plan-stream` and
  `src/FileManager.Core/IPC/Handlers/GetRunPlanStreamHandler.cs`, registered in the dispatch table.
- It replays the snapshot as the **same `DryRunChunkResponse` frames a preview streams**, deliberately,
  so no second renderer is needed: source rows as `Processed`, orphans as `Deleted`, terminated by a
  `DryRunCompleteResponse` carrying the plan's `Truncated` flag and `SpaceProjection`.
- `IRunCoordinator.SnapshotDirectory(runId)` resolves the run's frozen work list.

**Not in place:** anything in the UI. `IIpcGateway` has `RunProfileAsync` / `ApproveRunAsync` /
`CancelRunAsync` but **no plan-stream method**, and nothing calls the request. What ships today is
`MainWindowViewModel.ApproveOrDeclineRunAsync` → `BuildPlanConfirmation`, a dialog with counts (and the
deletion count called out).

**Why finish it:** counts tell the user *how many* files leave the target; only rows tell them *which*.
For Mirror that is the difference between "14 files will be removed" and seeing that one of them is
something they care about.

**To do:**

1. `IIpcGateway` + `IpcGateway`: add a plan-stream method modelled on the existing dry-run stream
   consumption (`src/FileManager.UI/Services/IpcGateway.cs:228` shows the shape —
   `client.DryRunStreamAsync` with an `IDryRunChunkSink`). The frames are identical, so the same sink
   works.
2. `DryRunViewModel`: accept a plan stream as an alternative source to a preview. Note its existing
   epoch guard (`_reportEpoch`) and the `I-POOL-RECYCLE` ownership contract on chunk entries — copy out
   anything retained.
3. A "pending run" banner with **Approve** / **Cancel**, routed to `ApproveRunAsync`.
4. Have `RunPlannedEvent` open this view instead of (or before) the dialog.

Careful: `DryRunViewModel` is ~3000 lines and is the most performance-sensitive view in the app
(columnar row store, pooled carriers, allocation-gauge tests). Read `docs/dry-run-ui-memory-next-steps.md`
before touching it. This is the main reason it was not attempted in the first pass.

---

## 4. Cancel affordance in the activity panel

`cancel-run` works end to end (`CancelRunHandler`, `IRunCoordinator.Cancel`,
`IIpcGateway.CancelRunAsync`, `ITriggerQueue.DropRun`) and is covered by
`RunLifecycleTests.Cancelling_mid_execution_drops_the_queued_work_and_skips_the_deletion_phase`. There
is simply no button: `ActivityViewModel` has no cancel command.

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

`src/FileManager.Core/Runs/ISourceSelector.cs` currently chooses from static inputs at **plan** time, and
its doc records the superseded media-type intent. Under this design the decision happens at **dispatch**
time and must see live per-volume load, so the interface moves. The important property survives:
`RunCopyItem.SourceIndex` still records what actually happened, so the snapshot and the audit trail agree
with reality even though the choice was not made in advance.

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

Expect **1436 tests, 0 failures** and **19 warnings** (the pre-existing baseline: CA1416 in
Platform.Windows.Tests, xUnit1031/2031 in Core.Tests, and one CS9107 in
`src/FileManager.Core/Files/FileSystemService.cs`). Note the "src is warning-free" claim in older docs
is stale — that CS9107 predates this work.

Manual Mirror check against the checked-in scenario environment:

```powershell
powershell -ExecutionPolicy Bypass -File tests/FileManager.Scripts.Windows.Tests/New-MirrorEnv.ps1
```

Point `EnginePaths.Root` at `tests/FileManager.Scripts.Windows.Tests/mirror`, run the profile from the
GUI, and confirm:

1. The approval prompt names 2 files to remove, and **nothing has moved yet**.
2. Approving recycles `stale-orphan.txt` and `old\deep-orphan.txt` — check the Recycle Bin, not just
   their absence.
3. `audit/mirror-YYYYMM.ndjsonl` has a row per deletion.
4. Re-running deletes nothing further (idempotency).
5. Repeat with `MirrorDeletion.Proactive` — the orphans should go *before* the copies land.
