# Implementation Progress & Roadmap: File Manager v1

**Last updated:** 2026-08-04
**Companion to:** [`architecture-v1.md`](architecture-v1.md) (authoritative shape) and
[`spec-draft-v3.md`](spec-draft-v3.md) (authoritative behavior).

This document tracks *what is built* against the architecture and *what remains*, in dependency
order. It is the running status board; update it as each set lands. Section references (§) point
into `architecture-v1.md`.

---

## Status legend

| Mark | Meaning |
| --- | --- |
| ✅ | Implemented and tested |
| 🟡 | Partial — see note |
| ⬜ | Interface/DTO only, or not started |

---

## Where we are

Three coherent increments are complete:

1. **Author profiles + preview (dry-run).** The configuration and simulation surface: Contracts,
   profile store/validator/catalog, filtering, source scanner, file hasher, the dry-run engine,
   settings, the IPC server + config/dry-run handlers, and the Avalonia UI (list/editor/dry-run/
   settings/status bar).
2. **Durable safety substrate + crash recovery.** The correctness core the live engine runs on —
   the write-ahead journal, in-memory registries, atomic placement + read-back verification,
   rollback, disk preflight, source disposition, and crash recovery — implemented, unit/integration
   tested (incl. the §7.4 fault-injection matrix), and wired into service startup for recovery-first.
3. **Live single-job vertical (the walking skeleton) — engine *and* UI.** The first time a file moves
   in normal operation: `IJobExecutor` drives the substrate through the §4.3 phase algorithm (minus
   transform), fed by `IJobOrchestrator` + `ITriggerQueue` + `IPauseStateService` + `IEngineEventBus` +
   `IJobLogStore` + `IProfileMatcher`; the six previously-unhandled IPC requests (`run-profile`,
   `set-paused`, `subscribe`, `get-matching`, `get-recent-jobs`, `get-job-log`) are live, the event
   stream broadcasts to subscribers, and the Service startup starts the orchestrator's trigger-queue
   consumer. The GUI is now wired to all of it: a **"Run now"** row action (confirmed, since it moves
   real files), an **activity panel** with live progress and per-job log drill-down, and a **pause
   toggle** in the status bar. Manually triggered only — no watcher/scheduler, no transformers yet.

4. **Snapshot-driven runs + `SyncMode.Mirror`.** A manual run is now a first-class object with a
   lifecycle — **plan → await approval → execute → close** — instead of an anonymous burst of jobs. Its
   first act is to capture the work via the *same* planner the dry-run preview uses
   (`IProfilePlanner`, extracted so a preview and a real run cannot describe different work), spool it
   to a frozen snapshot under `runs/<run-id>/`, and publish `run-planned` with exact counts. Nothing is
   touched until `approve-run`. Executing then reads the snapshot back: copies flow through the
   unchanged `ITriggerQueue` → `JobOrchestrator` → `IJobExecutor` path, and — for Mirror —
   `IMirrorDeletionPass` removes the destination orphans the plan named, **to the Recycle Bin, never
   hard-deleted**, write-ahead journalled and recorded in a dedicated `audit/mirror-YYYYMM.ndjsonl`
   trail. Runs report aggregate progress and a terminal `run-completed`, and can be cancelled.

5. **The job queue: concurrent runs, retained previews, and per-run pause.** A run is now something the
   user can *watch and hold*. A non-modal **Job Queue window** lists every run the engine knows about —
   previews included, since a dry run IS a run's planning phase — with live progress, **per-run
   pause/resume**, cancel, and inline approve. Previews are **retained per profile**: navigating away no
   longer declines the run, so re-opening the Preview tab re-streams the frozen plan from the run's own
   snapshot (metadata is cached, never rows — a materialized preview costs ~200–350 MB), and a
   configurable staleness banner says when a result may be out of date. Several previews can plan at once,
   bounded by `EngineConfig.MaxConcurrentPlans`.

6. **Runs are kept until discarded.** A finished run used to be forgotten ten minutes after it ended, on a
   hard-coded timer. It is now **retained** — the ten-minute mark only dims the row as *old* — and leaves
   the queue in one of two ways: the user **discards** it (the new `discard-run`, which cancels a live run
   first), or **auto-delete** reaps it after a configurable interval (default 24 h, and switchable off
   entirely). What makes that affordable is `RunState.ReleaseAfterClose`: a closed run drops its
   `PathsWritten` set, validation issues and completed task, and finally disposes its
   `CancellationTokenSource`, so it costs scalars rather than megabytes.

**Test status:** 1696 tests passing across the solution (Core 847, UI 650, Contracts 153,
Platform.Windows 14, Service 32). 0 errors. One pre-existing flaky test is documented below. A clean
Release build emits 19 analyzer warnings: 18 in test projects (xUnit1031/xUnit2031 in Core.Tests, CA1416
platform-guard notices in Platform.Windows.Tests) plus one pre-existing CS9107 in
`FileManager.Core/Files/FileSystemService.cs` — the earlier claim that `src/` is warning-free was
inaccurate, not a regression.

**File-operation assurance.** The live path now has a dedicated integration suite that drives the real
substrate (real journal, hasher, conflict resolver, placer, rollback executor, disposition service) over
a real temp filesystem and asserts against what is actually on disk — `JobExecutorFileOperationTests`
(topologies, byte fidelity, conflict modes, verification methods, idempotency),
`JobExecutorDispositionTests` (every OnSuccess action plus the full I-DISPOSE negative matrix),
`JobExecutorFailureRollbackTests` (a forced failure at each phase, the StageOverwrites restore, the
retry budget), `JobExecutorConcurrencyTests` (jobs racing one path), and `JobPlanFactoryTests` (target
path arithmetic). Fault injection lives in `FaultyFileHasher` (wrong-hash vs transient-error, which the
engine must treat differently) and `FaultyJobJournal` (the only deterministic way to fail *after* a
target is placed).

**What the app does today:** author/validate profiles, preview a run via dry-run, and **actually move
a file — from the GUI**: pick a profile, Run now, confirm, and watch it land. Under the hood that is
lock → journal-open → preflight → filter → seal → verified atomic placement → commit → disposition,
with streamed `job-started` / `job-progress` / `job-completed` events, queryable recent-jobs and
per-job logs, and a global pause that queues work until resumed. Automatic (watcher/schedule)
triggers and transformers are the next sets.

---

## Subsystem status (by architecture §4)

### §4.1 Profiles
| Component | Status | Note |
| --- | --- | --- |
| `IProfileStore` / `IProfileValidator` / `IProfileCatalog` | ✅ | |
| `IProfileMatcher` | ✅ | Containment + filter-aware match; feeds `get-matching` and the future picker. |

### §4.2 Triggers & watching
| Component | Status | Note |
| --- | --- | --- |
| `ISourceScanner` | ✅ | |
| `IWatcherService` / `ISettleTracker` / `IReadinessProbe` | ⬜ | Automatic-trigger set. |
| `ISchedulerService` (+ `CronExpression`) | 🟡 | `CronExpression` parser exists; service not built. |
| `ITriggerQueue` / `IPauseStateService` | ✅ | Coalescing FIFO with a pause-gated dequeue; pause persists to `state/pause.json`. |
| `IRunPauseGate` / `RunPauseRegistry` | ✅ | Per-run pause. The global pause BLOCKS the dequeue; a per-run pause FILTERS it, so holding one run does not stall the rest. Withholds unstarted work only — never aborts (§4.2). |

### §4.3 Jobs
| Component | Status | Note |
| --- | --- | --- |
| Core job types (`JobId`, `NormalizedPath`, `JobPlan`, `JobExecution`, …) | ✅ | |
| `JobStateMachine` | ✅ | §7.1 transition tables. |
| `PathLockRegistry` / `SelfWriteSuppressionRegistry` | ✅ | |
| `IDiskPreflight` | ✅ | |
| `IJobExecutor` / `IJobOrchestrator` (+ `JobPlanFactory`) | ✅ | §4.3 phase algorithm minus transform; bounded-parallel per-target placement; rollback hand-off. |

### §4.4 Filtering
| Component | Status | Note |
| --- | --- | --- |
| `IFilterCompiler` / `CompiledFilterSet` / rule set | ✅ | |

### §4.5 Transformers
| Component | Status | Note |
| --- | --- | --- |
| `IArgumentParser` / `ITokenExpander` / `IProcessRunner` / `ITransformerChainRunner` | ⬜ | Transformers set. |

### §4.6 Placement & verification
| Component | Status | Note |
| --- | --- | --- |
| `IFileHasher` | ✅ | Streaming full hash (XXH3-128/SHA-256) + `HashSampledToBytesAsync`, the bounded-read digest for §3.4.1 identity. |
| `IdentityStrategy` / `SampledHashLayout` | ✅ | The one §3.4.1 identity rule, shared by the executor's probe phase and the dry-run engine so preview and run cannot drift. |
| `IConflictResolver` | ✅ | `Probe` (dry-run) + `Resolve` (live, priority + lock-aware suffix). |
| `SourcePriorityRegistry` | ✅ | Session-scoped; provenance not persisted (documented v1 limit). |
| `IAtomicPlacer` | ✅ | Unchanged short-circuit (now `IdentityReference`-driven, run before sealing) + full journaled place sequence. |
| `ITransientRetryPolicy` | ✅ | Fixed 3×2 s. |

### §4.7 Journal, recovery & audit
| Component | Status | Note |
| --- | --- | --- |
| `IJobJournal` | ✅ | CRC-framed NDJSON, fsync, torn-tail, rotate. |
| `IRollbackExecutor` | ✅ | §4.7 ordered sweep, I-STAGING-KEEP. |
| `ICrashRecovery` | ✅ | §7.3 tables + forward gate + orphan sweep; wired recovery-first. |
| `IDispositionAuditLog` | ✅ | |
| `IReconcileAuditLog` | ✅ | A **sibling** trail (`audit/mirror-YYYYMM.ndjsonl`), not a widened `DispositionAuditRecord` — a destination orphan is not a source disposition and must not read like one. |

### §4.8 Disposition
| Component | Status | Note |
| --- | --- | --- |
| `ISourceDispositionService` | ✅ | + journal-driven overload for recovery row I. |
| `ITrashService` (Windows) | ✅ | `IFileOperation` COM via `[GeneratedComInterface]`. |

### §4.9 IPC
| Component | Status | Note |
| --- | --- | --- |
| `IIpcServer` + framing/client/launcher | ✅ | |
| Handlers: status, list/get/save/delete/validate profile, dry-run(+stream), settings, shutdown | ✅ | `get-status` now sources the real snapshot from `IJobOrchestrator`. |
| Handlers: `run-profile`, `set-paused`, `get-matching`, `get-recent-jobs`, `get-job-log` | ✅ | Registered in the dispatch table. |
| Handlers: `get-runs`, `set-run-paused` | ✅ | The job queue's re-seed (the event stream is drop-oldest lossy) and its pause toggle. |
| Handler: `discard-run` | ✅ | Removes a run, cancelling it first if live. The ONLY user-driven deletion, and the only thing besides auto-delete that takes an entry out of the coordinator. 23 entries in the dispatch table. |
| `subscribe` + `IIpcServer.Broadcast` | ✅ | Handled by the server directly: ack + open-ended one-way `EngineEvent` stream over a bounded, drop-oldest per-subscriber channel. |
| Protocol version | ✅ | **13** — `discard-run`, and `RunSummaryDto.ClosedAtUtc` so a queue can age a finished run it learned about from a reconcile rather than from the terminal event. The retention CONTRACT changed, which is what makes it a bump: an old client assumes a finished run vanishes within ten minutes and offers no way to remove one. Prior: **12** — `get-runs` / `set-run-paused`; `run-planned` gained `ProfileName` (a draft-planned run is in no catalog, so a client-side lookup leaves exactly the GUI's own runs nameless), `run-progress` gained `Paused`. UI and Service must ship together. Prior: **7** — drops `ThemeMode` from `GlobalSettings` (settings.json schema v5); it moved to the UI-owned `client-settings.json`. Prior: 6 added `job-progress` / `run-queued` events and gave `run-profile` a `run-profile-result` reply. UI and Service must ship together. |

### §4.10 Dry-run & observability
| Component | Status | Note |
| --- | --- | --- |
| `IDryRunEngine` | ✅ | Transformer profiles report `Unknown (requires transform)`. |
| `IProfilePlanner` | ✅ | THE source-phase → survivor-set → destination-sweep sequence, shared by the streamed dry-run handler and the run pipeline so a preview and a real run cannot drift. |
| `IEngineEventBus` / `IJobLogStore` | ✅ | In-proc pub/sub bridged to IPC broadcast; per-job log files + an in-memory recent-jobs ring. |
| `JobProgressPublisher` | ✅ | Per-job, throttled to 100 ms, monotonically clamped (distribution reports from parallel target tasks). |

### §4.11 Platform (Windows)
| Component | Status | Note |
| --- | --- | --- |
| `IIpcEndpointProvider` / `IAutostartRegistrar` | ✅ | |
| `IVolumeInfoProvider` / `IMetadataPreserver` / `ITrashService` | ✅ | |
| `IReadinessProbe` (Windows) | ⬜ | Automatic-trigger set. |
| `IShellIntegration` (Windows) | ⬜ | Clients & shell set. |

### Hosts
| Host | Status | Note |
| --- | --- | --- |
| `FileManager.Service` | 🟡 | Runs recovery-first + IPC + the orchestrator/trigger-queue consumer + event-bus→IPC broadcast bridge; watcher/scheduler/shell/tray startup slots still empty (§2.4). |
| `FileManager.UI` | 🟡 | List/editor/preview/settings/status bar, activity panel, global pause toggle **+ the non-modal Job Queue window, retained per-profile previews, and the staleness banner**; no tray, `--pick`, or `--tray`. |
| `FileManager.Cli` | ⬜ | Project does not exist yet. |

---

## Roadmap (remaining sets, in dependency order)

### Set 3c — Mirror follow-ups
*Detailed in [`../mirror-run-next-steps.md`](../mirror-run-next-steps.md), including the pre-existing
placer temp-cleanup bug and the open ratio-guard decision.*
- Render a pending run's itemized work list in the dry-run view (`get-run-plan-stream` already serves it
  as `DryRunChunkResponse` frames; the approval prompt currently shows counts only).
- A Cancel affordance in the activity panel (`cancel-run` and the gateway method exist).
- Volume-aware dispatch: spread in-flight reads across source volumes instead of letting the FIFO
  concentrate them. Self-calibrating on least-outstanding-work per volume, so it needs no hardware
  probing and helps every multi-source run.
- Duplicate source election on the same signal, decided at dispatch time. Gated on deciding "dispose all
  replicas" — see the doc.
- Remove emptied directories at a Mirror destination.
- `get-recent-reconciles`, so a deletion pass appears in the activity history.

### Set 4 — Transformers *(next)*
- `IArgumentParser` / `ITokenExpander` / `IProcessRunner` / `ITransformerChainRunner`.
- The executor's transform phase (workspace, `output-sealed`); 2×/1× workspace preflight
  accounting (§4.3). Resolves dry-run's `Unknown (requires transform)`.

### Set 5 — Automatic triggers
- `IReadinessProbe` (Windows) / `IWatcherService` / `ISettleTracker` / `ISchedulerService`.
- Startup slots: watcher, scheduler missed-run evaluation. Self-write suppression end-to-end.

### Set 6 — Clients & shell
- `FileManager.Cli` (`filemanager` executable).
- Tray (`--tray`) + shell picker (`--pick`). *(The UI activity view landed early, with Set 3, so the
  walking skeleton is actually walkable from the GUI.)*
- `IShellIntegration` (Windows) registered idempotently at startup (§2.4 step 5).

---

## Notable decisions & documented limitations

### Run retention (Set 5b)

- **Retention no longer deletes; it ages.** The ten-minute constant kept its number and lost its teeth: it
  is now the line at which a finished run reads as *old* in the queue (dimmed, with its age), not the line
  at which the engine forgets it. A run is history worth keeping, and deleting one is a decision.
- **Two ways out, and only two.** `discard-run` (the user) and auto-delete (`AutoDeleteFinishedRuns` +
  `FinishedRunRetentionHours`, default on at 24 h, both read on every sweep so a change needs no restart).
  Before this, `_runs.TryRemove` appeared exactly once in the whole coordinator — inside the age sweep —
  so there was no way for a client to make the engine forget a run at all.
- **Closed runs are released, not kept whole.** `RunState.ReleaseAfterClose` drops `PathsWritten`,
  `ValidationIssues` and the completed `Work` task, caps `DeletionFailures` at 100, and **finally disposes
  the `CancellationTokenSource` — which nothing anywhere had ever done.** This is not tidying: the old
  ten-minute window was the only thing bounding what a run held, and without this "never auto-delete"
  would be an out-of-memory button. `PathsWritten` alone is ~8–10 MB for a 33k-file run.
- **`PathsWritten` is now Mirror-only.** It was populated on every settle for every profile and read only by
  the Mirror deletion pass's self-write guard, so additive profiles (most of them) were paying one retained
  string per copied file to build a set no code path would ever look at.
- **The clear happens strictly after its one consumer**, which is the only real hazard in releasing a closed
  run: clearing the self-write set early would let the deletion pass recycle a file the run had just
  written. Pinned by
  `RunRetentionTests.A_path_this_run_wrote_is_never_deleted_even_when_the_plan_called_it_an_orphan`, which
  settles the copy onto the orphan's own path — exactly the plan-drift the guard exists for.
- **The count cap is not a user setting.** `EngineConfig.MaxRetainedClosedRuns` (500, matching the
  recent-jobs ring) applies even with auto-delete off, evicting the oldest-closed first and never a live
  run. It is the backstop that makes "off" a safe choice rather than an unbounded one.
- **The sweep interval is fixed at 5 minutes**, no longer `retention / 2` — which was sensible at ten
  minutes and would have been a 12-hour sweep at a day, leaving both the setting and the cap unenforced for
  half a day at a time. `CloseUnstarted` also sweeps now; it never did, and it is the path the GUI takes on
  every superseded preview.
- **Discarding an executing run does not stop it instantly.** It cancels — so work already in flight
  finishes (I-ATOMIC-JOB) — and the row leaves at once. Its `ExecuteAsync` keeps unwinding against a
  `RunState` no longer in `_runs` and publishes a terminal event for a run no client lists: harmless,
  and cheaper than teaching every path to check whether it has been forgotten mid-flight.
- **Superseded previews are discarded, not declined.** Declining only *closes* a run, and closed runs are
  now kept — so the old behaviour would have filled the queue with rows for previews the user replaced by
  pressing Preview again and never asked to keep.
- **Two settings rows rather than one "never or N" control.** No such editor kind exists, and
  `AutoNumberSettingViewModel.Auto` means "the engine picks the number", never "off" — a single control
  would have meant a new view-model type and a new `DataTemplate`. A `Setting.Bool` beside a
  `Setting.AutoNumber` says the same thing with templates that already exist.
- **The queue list is virtualized now.** An `ItemsControl` in a `ScrollViewer` realizes every item, which
  was fine against a ten-minute engine-side window and is not against hundreds of retained rows. Kept an
  `ItemsControl` rather than switching to `ListBox`: the row buttons bind through `$parent[ItemsControl]`.
- **Another client's queue lags a discard** until its next reconcile — `get-runs` is a full replace, so it
  self-corrects, but there is no `run-discarded` event. Single-user desktop; not worth a broadcast.

#### Two defects found on the first real use of Discard

- **A discarded run resurrected its own row.** Every queue event handler synthesizes a row for a run it does
  not know — correct for a lossy stream, since a queue silently missing an executing run is far worse than
  one extra row. But a discarded run publishes its *own* closing events: cancelling a planning run makes
  `PlanAsync` publish `run-planned` (carrying the cancellation as its error) and then `run-completed`, and
  both arrived at a queue that had just dropped the row and dutifully put it back. What the user saw was
  Discard cancelling the job and leaving the entry sitting there. Fixed with a bounded set of discarded ids
  consulted by all three handlers *and* by `ReconcileAsync` (an in-flight `get-runs` is the same
  resurrection by another route). The id is remembered **before** the IPC call, since the events can arrive
  while it is still awaiting, and forgotten again if the discard genuinely failed — otherwise that run's row
  would sit frozen at whatever it last said.
- **Discarding a still-PLANNING run left the Preview tab spinning forever.** The shell's discard hook
  checked only `PendingRunId`, which is null during a scan — it is `PlanningRunId` that holds the id then.
  Nothing cleared `IsPreviewing`, and nothing else could: the hook drops the run from `_ownRunIds` at the
  same moment, so the late `run-planned` that would otherwise have ended the wait was no longer recognized
  as this window's. `ForgetDiscardedRun` now covers all three stages a preview passes through — PLANNING,
  LOADING (the plan streaming into the store, which had no state to match against at all and would have
  surfaced the discard as "Preview failed: no run with id …"), and PENDING.
- **A test-side race the fix exposed.** `ShowRunPlanAsync` raises the footer inside `LoadPlanAsync` and
  remembers the result only after it returns, so a test waiting on `PendingRunId` could proceed while the
  preview store was still empty — and the next preview then found nothing to supersede. Waits now observe
  the thing being asserted. Only a test concern: these continuations resume on the UI thread in the app
  (the event pump deliberately omits `ConfigureAwait(false)`) and on the pool under xUnit.

### Job queue, retained previews & per-run pause (Set 5)

- **A per-run pause withholds; it never aborts.** The global pause is a safety brake on the whole engine,
  so `MirrorDeletionPass` aborts fail-closed under it. A per-run pause is the user saying "hold this one"
  about a run they already approved, so the pass WAITS instead — abandoning its deletion half would leave
  the destination not a mirror of the source, reported as an abort reason they never asked for. Two pauses
  with different semantics is a real wart; the alternative was worse. Cancel is how a run stops.
- **Three things fall out of that, none optional.** The drain barrier subtracts paused time (or a run
  paused over lunch comes back and deletes nothing, reported as unaccounted copies); a paused run is never
  quiescent (or the destructive phase starts while the user believes the run is held); and **cancel clears
  the pause flag** — without it a cancelled-while-paused run has nothing left that can release its drain
  loop and sits in `Executing` until the 30-minute deadline, never closing, never cleaning up its
  snapshot, never leaving the queue. Found by a test teardown hanging; pinned by
  `RunPauseTests.Cancelling_a_paused_run_closes_it_instead_of_waiting_out_the_barrier`.
- **`RunPauseRegistry` is standalone, not a member of `RunCoordinator`.** The coordinator depends on
  `ITriggerQueue` and the queue must read pause state on every dequeue, so a coordinator that answered
  `IRunPauseGate` itself would close a dependency cycle the container refuses to resolve.
- **A preview is retained per PROFILE, and the store owns the leak.** A run parked in `AwaitingApproval`
  holds a snapshot directory with no expiry, which is why `DryRunViewModel` used to decline one on every
  profile switch. Retaining results deliberately keeps those runs parked, so `PreviewStore` inherited the
  obligation: it declines every entry it supersedes and declines all of them on window close. Nothing else
  may leave a run parked. `PreviewStoreTests` is mostly about that, not about the dictionary.
- **Metadata is cached; rows are not.** The store holds the `RunPlannedEvent` (a few hundred bytes) and
  re-streams the rows from the run's snapshot on demand, because a materialized preview costs ~200–350 MB
  of process footprint. `Clearing_the_preview_releases_the_row_store` is the guard that this stayed true —
  it fails if anything ever re-roots the store, and it still reports 0.3 MB residual (0% retained).
- **Retained previews do NOT survive a service restart.** `EngineHost.PurgeRunSnapshots` sweeps `runs/` at
  startup and the coordinator's run table is in-memory, so every parked run is gone. `RUN_NOT_FOUND` on a
  restore is therefore the ORDINARY case, not an error: the entry is dropped and the tab shows "the saved
  preview has expired" with no banner.
- **A parked run still has no expiry.** One per profile bounds it, but previewing 50 profiles holds 50
  snapshot directories until the window closes. The retention sweep only ever touches *closed* runs — the
  count cap added in Set 5b likewise. Worth a cap later; not built here.
- **Approve is gated on having SEEN the plan.** The queue shows every run, including another client's, but
  a row offers Approve only when this window planned it; otherwise it offers "View plan", which loads it
  into the Preview tab and *then* makes it approvable. That is the invariant the two-phase run exists to
  protect. Approving from the queue while the tab shows that same run routes through the tab's footer, so
  the blocking-warning acknowledgment the user just ticked is not sent as false.
- **`MaxConcurrentPlans` (3) is a safety valve, not a throughput knob.** Concurrent previews are the point,
  but the service was measured at ~292 MB producing ONE 33k-file plan. The slot is released when the work
  list freezes, not when the run closes — a parked run holding one would deadlock the queue after three
  previews. A queued run reports the `Waiting` phase, which is a display distinction over `Planning` and
  deliberately NOT a `RunPhase` member: adding one would ripple through the §7.1 tables, `RunStatus` and
  every consumer of both to express something only a caption needs.
- **A queue row cannot list its own files.** `JobSummaryDto` and `JobStartedEvent` carry a `ProfileId`, not
  a `RunId` — a deliberate Set 3b decision, since the trigger queue coalesces across runs and a run id
  would misattribute the survivor. So the queue shows run-level `Completed`/`Total` and per-file detail
  stays in the activity panel.
- **The staleness threshold is client-side** (§2.3): the engine never reads it, and a stale preview is a
  statement about what the USER is looking at — the plan is exactly as valid as when it was frozen. Stored
  as `int?` because `ClientSettings` is a positional record, so an absent member deserializes through the
  constructor as 0, not 15; a plain `int` would have made every existing settings file report "stale
  immediately". The banner is advisory and Approve stays enabled: an old plan risks being INCOMPLETE,
  never wrong, because the executor re-screens and can only ever do less than planned.
- **The queue window is the app's only non-modal window.** Every other secondary window is `ShowDialog`.
  A queue you must dismiss before editing a profile is a dialog, not a queue. The composition root tracks
  the instance so a second press focuses the open window rather than stacking duplicates, and the view
  model is owned by the shell so it keeps consuming events while the window is shut.

### Snapshot-driven runs & Mirror (Set 3c)
- **A run executes from a frozen snapshot, produced by the preview's own planner.** `IProfilePlanner`
  was extracted from `DryRunStreamHandler` so the source-phase / survivor-set / sweep sequencing exists
  once. The run writes its output to `runs/<run-id>/` and executes from that, which means the deletions a
  Mirror run performs are provably the deletions the preview showed — there is no second computation to
  disagree with the first. It also freezes the work list, gives progress a real denominator, and creates
  the barrier point at which "every copy has landed, now remove the orphans" can be expressed at all.
- **The executor stays authoritative.** It re-screens and re-checks-unchanged, so it can only ever do
  LESS than the plan predicted, never more. That asymmetry is what makes the plan safe to treat as a
  ceiling, and it is why `RunCopyItem.PlannedKind` is advisory (display and progress) rather than an
  instruction.
- **Only `Processed` source operations become work.** A filter-excluded file and a file already identical
  at every target both produce no copy item — exactly what the preview showed for them. A file that
  changes between planning and execution is therefore picked up by the NEXT run rather than silently
  altering the run the user approved.
- **Tightening a filter on a Mirror profile deletes files it copied earlier.** An excluded source file
  contributes no destination operation, so no survivor, so its previously-copied destination reads as an
  orphan. This matches the dry run exactly (verified against `DestinationProjector.AccumulateSurvivors`),
  which is why it stands — but it is the single most surprising thing the app can do, so it is named in
  the run confirmation and gated behind the new blocking `PROFILE_MIRROR_DELETES` acknowledgment.
- **Deletion is fail-closed.** Any doubt deletes nothing and says why: a truncated plan, ANY enumeration
  fault (including a warning-level unreadable subdirectory — deliberately stricter than the dry run,
  which only flags such a report), a scoped run, a missing source root, any failed/rolled-back copy job,
  a paused engine, a barrier that timed out, an orphan count over the cap, or a per-root ratio guard.
  A job's `DispositionError` is deliberately **not** a gate: disposition concerns the source, while the
  destination copies are proven placed.
- **The ratio guard is a judgement call.** Refusing a pass that would remove >50% of a target root (at
  ≥20 orphans) is what catches a `TargetLayout` flip and a source share that remounted empty — both
  produce a plan every other gate calls healthy. It has a false-positive mode (a legitimate large
  cleanup) and there is **no override yet**; that needs deciding before this ships widely.
- **Scoped runs never reconcile.** An orphan can only be identified over the complete source set, so a
  run narrowed to a path copies but deletes nothing. The GUI always sends a whole-profile run; the
  long-run answer for automation is the Set 5 scheduler or a dedicated `reconcile-profile` request.
- **Crash recovery takes NO action on an interrupted deletion pass**, and there is no correct action to
  take: the file is either still on disk or already in the Recycle Bin and recovery cannot tell which.
  Re-deleting would destroy a file the pass never reached; restoring would re-create an orphan the user
  approved removing. It is reported by path (`RecoveryReport.UnfinishedMirrorDeletions`) and left alone;
  the next Mirror run re-plans and removes anything still there. Note `journal.Rotate()` discards those
  records, so the warning must be raised by the same recovery pass that read them.
- **Two audit files.** `audit-*.ndjsonl` (source dispositions) and `mirror-*.ndjsonl` (destination
  orphans) rather than one widened record: there is no honest `OnSuccessAction` for "the mirror removed a
  stale copy", and stamping `MoveToTrash` would make it indistinguishable from "your policy recycled your
  original". If a combined viewer is ever wanted, a polymorphic `AuditRecord` base is easier now than
  after both files exist in the field.
- **Empty directories are never removed**, so a Mirror destination accumulates an empty skeleton where a
  source subtree used to be. Pinned by a test so it reads as a decision.
- **A deletion pass does not appear in the recent-jobs ring.** `JobSummary` requires a `SourcePath` and a
  `JobOutcome`; a pass has neither, and faking them is the kind of small lie that later reads as truth.
  A proper `get-recent-reconciles` is the follow-up. The pass IS drillable via `get-job-log` on its pass
  id, which reuses `JobId` precisely so that works for free.
- **`ITriggerQueue.Enqueue` now returns an `EnqueueOutcome`** and the queue gained `DropRun` /
  `PendingCountForRun`. Coalescing silently replaces a pending payload, so a run counting its own jobs
  must learn which payload was displaced or its barrier waits out the whole deadline.
- **Protocol 9.** `run-profile`'s `Path` is optional, `approve-run` / `cancel-run` /
  `get-run-plan-stream` are new, and three run events joined the stream. The bump is mandatory: an old
  client hitting an unknown `EngineEvent` discriminator throws inside `System.Text.Json` and loses its
  whole subscribe stream.

### Fixed pre-existing defect (was not introduced by this work)
*Full diagnosis: [`../mirror-run-next-steps.md`](../mirror-run-next-steps.md) §1.*
- **`JobExecution.States`/`Targets` were built by a torn `??=` lazy initializer.** `DistributeAsync`
  fans out one task per target and they all take the first access at once, so several threads each
  built their own `TargetProgress` array and only the last write survived. Rollback then snapshotted a
  discarded array, read `Pending`/`null` for targets that had really reached `Staged`, and did nothing
  for them — which is what left the `.fmtmp-` temps behind roughly 1 run in 8. The same lost state
  could leave a **placed** target un-reverted while the job reported "rolled back cleanly". Both are
  now built eagerly in `Plan`'s `init` accessor. Three adjacent defects were fixed with it: the placer
  now reclaims its own unplaced temp, `RevertStaged` no longer `??`-skips the temp delete when a
  restore fails, and `DistributeAsync` no longer reads "all targets cancelled, none errored" as
  success (which made an externally-cancelled job commit and dispose the source with nothing placed).

### Fixed: `RunPhase.Closed` was observable before a run's snapshot was cleaned up
- **`RunLifecycleTests.A_closed_run_leaves_no_snapshot_directory_behind` was failing about 1
  full-Core run in 6** — a product ordering defect, not a test-side assumption. `RunCoordinator.Close`
  set `run.Phase = RunPhase.Closed` under `run.Gate`, released the lock, and only *then* deleted the
  snapshot directory. `GetStatus` is the only way a client observes a phase, so from the moment that
  lock released, a closed run still had its scaffolding on disk. The `PlanAsync` failure path had the
  same shape; the *decline* path already cleaned up inside the lock, which is what made the
  inconsistency easy to miss.
- **Fix:** all three terminal paths now clean up *before* `Closed` becomes observable, so the phase
  every client reads as "this run is over" means it. `CleanUpSnapshot` also clears `run.Directory` on
  success, so `SnapshotDirectory` no longer hands out a path to a deleted directory —
  `GetRunPlanStreamHandler` reads the null as `RUN_NOT_FOUND`, which is the truth for a closed run,
  where a stale path surfaced as an opaque I/O error instead.
- **Proven, not just observed:** with the cleanup artificially widened to 300 ms, the test fails 3/3
  under the old ordering and passes 3/3 under the new one. The assertion is deliberately left with no
  retry — polling would let the coordinator publish `Closed` with the directory still present and
  still pass, which is exactly the ordering being pinned.
- `[flagged]` **No startup sweep of `runs/` exists**, despite what `EnginePaths.RunsDirectory` and
  `CleanUpSnapshot` used to claim in their doc comments (both corrected). Nothing enumerates that
  directory, so a cleanup failure — which is swallowed to a warning by design — leaks a snapshot
  directory permanently. Worth a real sweep alongside the `.pipeline_tmp` one; not built here.


- **CRC:** the journal/audit line checksum is CRC-32 (IEEE) via `System.IO.Hashing.Crc32`
  (`NdjsonFrame`). Non-load-bearing — the same function frames the write and validates the read.
  Earlier drafts named CRC-32C; §5.5 is now reconciled to CRC-32. Confirmed by benchmark
  (`ShortInputChecksumBenchmarks`): on journal-line-sized inputs CRC-32 ties or beats the
  alternatives once the per-record fsync is accounted for, and `System.IO.Hashing` has no `Crc32C`
  type (CRC-32C would need hand-rolled SSE4.2 intrinsics for no observable gain).
- **Recovery disposition:** `ISourceDispositionService` gained a journal-driven overload so
  recovery's post-commit branch (§7.3 row I) reconstructs from `job-opened` rather than faking a
  `JobExecution`. Archive layout in that path falls back to flat filename placement (no profile).
- **`.fm_staging` orphan sweep:** recovery cannot enumerate arbitrary target roots without the
  profile catalog, so a truly-orphaned staging dir (no journal trace) is not centrally swept in v1;
  a staging dir left by a failed rollback is quarantined by `RollbackExecutor`'s I-STAGING-KEEP
  guard. `.pipeline_tmp` orphans (central) are swept.
- **`EngineConfig`** is registered with defaults only; reconciling it with the existing
  `settings.json` / `GlobalSettings` is deferred.
- **Settings are split by ownership** (§2.3): `settings.json` / `GlobalSettings` is engine
  configuration the service owns, and `client-settings.json` / `ClientSettings` is UI-owned state the
  engine never reads — the service executable path, the theme, and the sidebar layout. The split is
  what makes the service executable path fixable *while the service is unreachable*, which is the
  only moment it matters: the settings window refuses to write `GlobalSettings` after a failed load,
  so a setting stored there could never recover a service that will not start. Service-owned settings
  grey out (with a tooltip) whenever the load failed. `client-settings.json` has two independent
  writers, so every write is read-modify-write; `ui-state.json` and the old `GlobalSettings.ThemeMode`
  are migrated into it on first run.
- **Service executable resolution is first-*usable*-wins** (§3.3), so a stale or mistyped path falls
  back rather than bricking the launcher. Because that means a wrong path keeps working silently, the
  settings window carries a live notice under the box saying what the value resolves to — found,
  fell back to *X*, not the service executable, or nothing usable anywhere — recomputed off the UI
  thread as the user types. Saving an unusable path is allowed (the notice is the telling), but the
  window stays open while the configured value is not clean, because closing over the top of the
  warning is exactly how a wrong executable got saved unnoticed.
- **Two lockout guards** protect the recovery path, learned from an executable that started but never
  served: `IpcGateway` will not re-spawn the same failed executable for a minute (keyed by path, so a
  correction retries at once), and `OpenSettings` shows the window without awaiting its IPC load.
  Together these stop a bad path from starving the connect gate and locking the user out of the only
  window that can fix it.
- **Saving a changed executable path switches the running service over** (§3.3): it asks whether to
  stop the one already running, then reconnects and polls until the service reports the expected
  `ExecutablePath`. Nothing happens when the running service is already the chosen executable, and
  declining the stop starts nothing — only one service can hold the pipe, so a second would be both
  futile and the duplicate the user declined.
- **Source priority** is session-scoped in-memory; across a restart it degrades to arrival order
  (same open design as `ContentHashDedupe`, Appendix B).
- **No content-identity cache.** `LargeFileIdentity` makes a duplicate cheap to *read*, but every run
  still re-reads what it needs; digests are never remembered between runs, or even between a preview and
  the run that follows it. Designed in `docs/content-identity-cache-next-steps.md`, deliberately not
  built — it is the only part of the idea needing new durable state.

### Defects the file-operation suite found (and fixed)
- **`RenameSuffix` never took the desired name.** A job's lock set already contains every prospective
  final path, and `PathLockRegistry.TryAcquireAdditional` reported a path the *requesting* set already
  held as taken. So the suffix probe skipped the free desired name and placed at `name (1).ext` on a
  first, collision-free run — and because the desired name stayed empty, **every re-delivery suffixed
  again and grew the target set without bound**, squarely violating spec §12's idempotency criterion.
  `TryAcquireAdditional` is now idempotent for a path the set already owns (and does not double-record
  it, which would double-release on dispose). While there, `PathLockSet` was made thread-safe: parallel
  target tasks extend the same set concurrently, and its backing `List` was unguarded.
- **Staging was never cleaned up on the success path.** `RollbackExecutor` deletes staging dirs, but it
  only runs on failure — so under the default `StageOverwrites`, every successful overwrite left a full
  copy of the replaced file in the user's target root under `.fm_staging/<job>/` **permanently**
  (recovery cannot sweep `.fm_staging`, as noted below). I-STAGING-KEEP authorizes deleting a staging
  dir once the job closed `Succeeded`; the executor now does that, after the terminal close is
  journaled, best-effort.
- **`MoveToArchive` with no `ArchiveFolder` reported a false success.** `ResolveArchiveDestination` fell
  back to returning the *source path*, which defeated `Apply`'s null guard: the service did
  `File.Move(source, source)` — a silent no-op — and recorded a disposition claiming the file had been
  archived to its own location. Profile validation normally rejects the combination, but the deletion
  audit trail is the no-loss safety net and must never contain a false entry. The null check moved to
  the caller so the misconfiguration is reported once, cleanly.
- **Test-infrastructure defect:** `FakeMetadataPreserver` ignored `MetadataOnConflict` and failed
  regardless of policy, unlike `WindowsMetadataPreserver` (which fails only under `FailJob`). A
  regression making best-effort metadata preservation fatal would have passed every test.

### Set 3b decisions (UI connection + engine gap closure)
- **Protocol 6.** New `job-progress` and `run-queued` events; `run-profile` now answers
  `run-profile-result` (`QueuedCount`, `Scanning`) rather than a bare `ok`. UI and Service must be
  packaged together — a mixed pair fails loud with `IPC_VERSION_MISMATCH`, by design.
- **Progress is a sink parameter, not a bus dependency.** `IJobExecutor.ExecuteAsync` takes an optional
  `IProgress<JobProgress>` (mirroring `IpcClient.DryRunStreamAsync`), and `JobProgressPublisher` — one
  per job, created by the orchestrator — turns samples into throttled events. This keeps the wire
  contract out of the phase algorithm, keeps progress optional for tests, and gives per-job throttle
  state a home. Publishing is throttled to 100 ms and **monotonically clamped**: distribution reports
  from parallel target tasks, so without the clamp the UI would visibly count backwards.
  Byte-level progress is explicitly out of scope — it would need new sinks through `IAtomicPlacer`,
  `IFileHasher` and the retry policy.
- **No `RunId` correlation token.** Considered and rejected: `TriggerQueue.Enqueue` coalesces on
  `(ProfileId, SourcePath)` and keeps the newest payload, so two concurrent runs of the same profile
  over the same path collapse into one job and a `RunId` would misattribute it to whichever click
  landed second. `JobStartedEvent` already carries `{ProfileId, SourcePath}`, which is sufficient.
- **Inactive profiles are refused *and* dropped.** `RunProfileHandler` answers `PROFILE_INACTIVE` so
  the caller learns why nothing happened; `JobOrchestrator` independently drops an inactive profile's
  payload (§4.3 failure semantics), which is the gate that covers every trigger and closes the window
  where a profile is deactivated between enqueue and dequeue.
- **`JobFailedEvent.ResidualPaths` is now populated** via a new `JobCompletion.ResidualPaths` init
  property. Note the event's list can legitimately be empty on a `RollbackFailed` outcome — when
  rollback failed before it could enumerate residuals — so empty is not a bug.
- **`LastError` is sticky until recovered**, cleared by the next successful/skipped job and on
  orchestrator start. It is a status-bar health hint; last-writer-wins across workers is acceptable
  (anything stricter needs an error ring, a different feature).
- **`profiles-changed` now has a publisher** (an `EngineHost` bridge over `IProfileCatalog.Subscribe`).
  It must stay below step 3's `catalog.Reload()` or startup would emit a spurious event. The UI
  suppresses the echo for ~1 s after its own saves/imports, which already refresh.
- **`engine-warning` still has no publisher** — deliberately. Its real producers (watcher overflow,
  scheduler missed-run) do not exist yet, and the one candidate (a folder scan that aborts) is served
  properly by `RunQueuedEvent.Error`.
- **UI event subscription is split in two.** The gateway exposes a single-attempt
  `IAsyncEnumerable<Result<EngineEvent, IpcError>>` (preserving its never-throws contract, and echoing
  `ISourceScanner.Scan`'s stream-of-Results idiom); `EngineEventPump` owns reconnect/backoff and
  re-seeds via get-recent-jobs + get-status on every attempt, because delivery is drop-oldest lossy.
  It connects with `IpcClient.ConnectAsync`, **not** `ServiceLauncher.ConnectOrStartAsync`, which
  would spawn a service process per retry during an outage.
- **The activity panel docks above the status bar**, not as a third document tab: that tab strip is
  gated on `Editor.HasProfile`, and engine activity is global state that must stay visible with no
  profile selected. Its open/closed state is not persisted this slice.
- **"Run now" always confirms**, naming the source roots and the disposition — it is the only place
  the user sees the blast radius. It is refused while the editor is dirty for that profile, since
  `run-profile` resolves against the persisted catalog and would silently ignore unsaved edits.

### Set 3 decisions & documented simplifications
- **Free-space query walks up to an existing ancestor.** `WindowsVolumeInfoProvider` disk preflight
  runs *before* the workspace directory is created (§4.3 order), so a query on the not-yet-created
  `.pipeline_tmp/<job>` path used to fail (win32 3). It now walks up to the nearest existing ancestor
  (free space is a per-volume answer); Set 4's transform workspace relies on the same fix.
- **`IJobLogStore.RecordSummary` added** to feed the get-recent-jobs ring (the §4.10 interface is
  amended). Recent-jobs are **in-memory only** in v1 — empty after a restart; per-job log files persist.
- **Bounded-parallel per-target placement** with a linked cancellation token (cancel siblings on the
  first failure), per §4.3 step 6.
- **Archive-destination locking is deferred.** The per-job lock set covers source + prospective final
  paths but not a `MoveToArchive` destination — disposition is post-commit and never rolled back, and
  this set has no concurrent watcher. Revisit when automatic triggers land.
- **Filters are compiled per-job** in the executor's Screen phase (cheap; profiles pre-validate
  patterns at save). Screen is the authoritative gate for a manual single-file run, which the scanner
  never pre-filters.
- **`subscribe` is server-handled, not a dispatch-table handler.** An event subscription is
  open-ended and `Broadcast`-driven, unlike the terminating `IIpcStreamingRequestHandler` (dry-run);
  each subscriber gets a bounded, drop-oldest frame channel so a slow client never blocks the publisher.
- **Transformer profiles are refused** by the executor (pre-Open, no journal) until Set 4.
