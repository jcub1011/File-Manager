# Implementation Progress & Roadmap: File Manager v1

**Last updated:** 2026-07-22
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
3. **Live single-job vertical (the walking skeleton).** The first time a file moves in normal
   operation: `IJobExecutor` drives the substrate through the §4.3 phase algorithm (minus transform),
   fed by `IJobOrchestrator` + `ITriggerQueue` + `IPauseStateService` + `IEngineEventBus` +
   `IJobLogStore` + `IProfileMatcher`; the six previously-unhandled IPC requests (`run-profile`,
   `set-paused`, `subscribe`, `get-matching`, `get-recent-jobs`, `get-job-log`) are live, the event
   stream broadcasts to subscribers, and the Service startup starts the orchestrator's trigger-queue
   consumer. Manually triggered only — no watcher/scheduler, no transformers yet.

**Test status:** 709 tests passing across the solution (Core 350, UI 211, Contracts 120,
Platform.Windows 14, Service 14). Solution builds with 0 warnings / 0 errors.

**What the app does today:** author/validate profiles, preview a run via dry-run, and — new in this
set — **actually move a file** on a manual `run-profile` invocation over IPC: lock → journal-open →
preflight → filter → seal → verified atomic placement → commit → disposition, with a streamed
`job-started`/`job-completed` event, queryable recent-jobs and per-job logs, and a global pause that
queues work until resumed. Automatic (watcher/schedule) triggers and transformers are the next sets.

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

### §4.10 Dry-run & observability
| Component | Status | Note |
| --- | --- | --- |
| `IDryRunEngine` | ✅ | Transformer profiles report `Unknown (requires transform)`. |
| `IEngineEventBus` / `IJobLogStore` | ✅ | In-proc pub/sub bridged to IPC broadcast; per-job log files + an in-memory recent-jobs ring. |

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
| `FileManager.UI` | 🟡 | List/editor/dry-run/settings/status bar; no tray, `--pick`, or `--tray`. |
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
- UI activity view + tray (`--tray`) + shell picker (`--pick`).
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
- **Source priority** is session-scoped in-memory; across a restart it degrades to arrival order
  (same open design as `ContentHashDedupe`, Appendix B).

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
