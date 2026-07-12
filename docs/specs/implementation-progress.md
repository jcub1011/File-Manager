# Implementation Progress & Roadmap: File Manager v1

**Last updated:** 2026-07-10
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

Two coherent increments are complete:

1. **Author profiles + preview (dry-run).** The configuration and simulation surface: Contracts,
   profile store/validator/catalog, filtering, source scanner, file hasher, the dry-run engine,
   settings, the IPC server + config/dry-run handlers, and the Avalonia UI (list/editor/dry-run/
   settings/status bar).
2. **Durable safety substrate + crash recovery.** The correctness core the live engine will run
   on — the write-ahead journal, in-memory registries, atomic placement + read-back verification,
   rollback, disk preflight, source disposition, and crash recovery — implemented, unit/integration
   tested (incl. the §7.4 fault-injection matrix), and wired into service startup for
   recovery-first. **Not yet driven by a live job in normal operation.**

**Test status:** 312 tests passing across the solution (Core 163, Contracts 72, UI 60,
Platform.Windows 10, Service 7). Solution builds with 0 warnings / 0 errors.

**What the app does today:** author/validate profiles, preview a run via dry-run (real scan/
filter/hash/conflict paths), and start a recovery-first service that serves IPC. It does **not**
yet move files automatically or on manual invocation — that is the next set.

---

## Subsystem status (by architecture §4)

### §4.1 Profiles
| Component | Status | Note |
| --- | --- | --- |
| `IProfileStore` / `IProfileValidator` / `IProfileCatalog` | ✅ | |
| `IProfileMatcher` | ⬜ | Needed by the shell picker / manual-run path (next set). |

### §4.2 Triggers & watching
| Component | Status | Note |
| --- | --- | --- |
| `ISourceScanner` | ✅ | |
| `IWatcherService` / `ISettleTracker` / `IReadinessProbe` | ⬜ | Automatic-trigger set. |
| `ISchedulerService` (+ `CronExpression`) | 🟡 | `CronExpression` parser exists; service not built. |
| `ITriggerQueue` / `IPauseStateService` | ⬜ | Live-executor set. |

### §4.3 Jobs
| Component | Status | Note |
| --- | --- | --- |
| Core job types (`JobId`, `NormalizedPath`, `JobPlan`, `JobExecution`, …) | ✅ | |
| `JobStateMachine` | ✅ | §7.1 transition tables. |
| `PathLockRegistry` / `SelfWriteSuppressionRegistry` | ✅ | |
| `IDiskPreflight` | ✅ | |
| `IJobExecutor` / `IJobOrchestrator` | ⬜ | Live-executor set — the walking skeleton. |

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
| Handlers: status, list/get/save/delete/validate profile, dry-run(+stream), settings, shutdown | ✅ | 11 registered. |
| Handlers: `run-profile`, `set-paused`, `subscribe`, `get-matching`, `get-recent-jobs`, `get-job-log` | ⬜ | Defined but unhandled → `NOT_IMPLEMENTED`. Live-executor set. |

### §4.10 Dry-run & observability
| Component | Status | Note |
| --- | --- | --- |
| `IDryRunEngine` | ✅ | Transformer profiles report `Unknown (requires transform)`. |
| `IEngineEventBus` / `IJobLogStore` | ⬜ | Live-executor set (event broadcast is a no-op today). |

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
| `FileManager.Service` | 🟡 | Runs recovery-first + IPC; trigger/tray startup slots still empty (§2.4 steps 6–7). |
| `FileManager.UI` | 🟡 | List/editor/dry-run/settings/status bar; no tray, `--pick`, or `--tray`. |
| `FileManager.Cli` | ⬜ | Project does not exist yet. |

---

## Roadmap (remaining sets, in dependency order)

### Set 3 — Live single-job vertical *(next)*
The walking skeleton: the first time a file actually moves. Manually triggered, no transformers,
no watcher.
- `IJobExecutor` (the §4.3 phase algorithm, minus the transform phase) driving the substrate.
- `IJobOrchestrator` + `ITriggerQueue` + `IPauseStateService` + `IEngineEventBus` + `IJobLogStore`.
- `IProfileMatcher`; the `run-profile` / `set-paused` / `subscribe` / `get-matching` /
  `get-recent-jobs` / `get-job-log` handlers; real `EngineStatusSnapshot`.
- Startup slot: trigger-queue consumer; event bus → IPC broadcast.

### Set 4 — Transformers
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
