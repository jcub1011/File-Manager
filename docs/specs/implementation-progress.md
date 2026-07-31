# Implementation Progress & Roadmap: File Manager v1

**Last updated:** 2026-07-30
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

**Test status:** 1121 tests passing across the solution (Core 509, UI 434, Contracts 135,
Platform.Windows 14, Service 29). 0 errors. A clean build emits 18 analyzer warnings, all in test
projects (xUnit1031/xUnit2031 in Core.Tests, CA1416 platform-guard notices in Platform.Windows.Tests);
`src/` is warning-free.

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
| `IFileHasher` | ✅ | |
| `IConflictResolver` | ✅ | `Probe` (dry-run) + `Resolve` (live, priority + lock-aware suffix). |
| `SourcePriorityRegistry` | ✅ | Session-scoped; provenance not persisted (documented v1 limit). |
| `IAtomicPlacer` | ✅ | Unchanged short-circuit + full journaled place sequence. |
| `ITransientRetryPolicy` | ✅ | Fixed 3×2 s. |

### §4.7 Journal, recovery & audit
| Component | Status | Note |
| --- | --- | --- |
| `IJobJournal` | ✅ | CRC-framed NDJSON, fsync, torn-tail, rotate. |
| `IRollbackExecutor` | ✅ | §4.7 ordered sweep, I-STAGING-KEEP. |
| `ICrashRecovery` | ✅ | §7.3 tables + forward gate + orphan sweep; wired recovery-first. |
| `IDispositionAuditLog` | ✅ | |

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
| `subscribe` + `IIpcServer.Broadcast` | ✅ | Handled by the server directly: ack + open-ended one-way `EngineEvent` stream over a bounded, drop-oldest per-subscriber channel. |
| Protocol version | ✅ | **7** — drops `ThemeMode` from `GlobalSettings` (settings.json schema v5); it moved to the UI-owned `client-settings.json`. Prior: 6 added `job-progress` / `run-queued` events and gave `run-profile` a `run-profile-result` reply. UI and Service must ship together. |

### §4.10 Dry-run & observability
| Component | Status | Note |
| --- | --- | --- |
| `IDryRunEngine` | ✅ | Transformer profiles report `Unknown (requires transform)`. |
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
| `FileManager.UI` | 🟡 | List/editor/dry-run/settings/status bar **+ activity panel, pause toggle, and "Run now"**; no tray, `--pick`, or `--tray`. |
| `FileManager.Cli` | ⬜ | Project does not exist yet. |

---

## Roadmap (remaining sets, in dependency order)

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
