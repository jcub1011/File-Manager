# Milestone 5 — Triggers & concurrency

## Goal

Drive Jobs automatically and safely in parallel. Introduce the single authoritative execution model —
one bounded worker pool across all Profiles with a FIFO per-path lock — and the two automated
triggers: a reactive file watcher with a real readiness/settle policy, and a scheduler (cron +
interval) with timezone and missed-run handling. After M5 the engine runs hands-off against
configured Sources at scale (the manual shell trigger arrives with shell integration in M8).

## Spec references

- §3.2 — triggers (Watcher, Scheduled/Interval; Manual is M8).
- §3.2.1 — file readiness / settle policy (`SettleDelaySeconds` + readiness probe;
  `StabilityIntervalMs`; network relaxation to size-stability).
- §3.2.2 — missed scheduled runs (`CatchUpOnce` / `Skip`).
- §5.4 — bounded worker pool (`MaxWorkers`, default = CPU count) and same-file path lock (FIFO);
  network advisory-lock caveat (in-process locking only).
- §11 — watcher scale: Linux `inotify` limits → periodic rescan; Windows `ReadDirectoryChangesW`
  buffer sizing + overflow recovery by rescan.

## Scope

**In scope**
- `WorkerPool` running Jobs concurrently regardless of Profile; `MaxWorkers` from `ServiceConfig`
  (M0; default = CPU count).
- Intra-Job multi-Target write parallelism (§4 Phase 4 / §5.4 "writes to multiple Targets may also
  use the pool"): when a Job has several Targets, its per-Target temp-writes may be dispatched onto
  the same bounded pool rather than written strictly sequentially. (M1 wrote Targets sequentially;
  M5 promotes this to bounded-parallel under the shared pool/`PathLockManager`.)
- `PathLockManager`: lock keyed by absolute path, acquired on source + each Target temp/final path
  before acting; second contender waits FIFO. In-process only (network caveat documented).
- File watcher per Source root; debounce on `SettleDelaySeconds`; readiness probe — Windows
  exclusive-open; Linux not-advisory-locked + size stable across two probes `StabilityIntervalMs`
  apart; network Sources relax to size-stability with a per-Job logged caveat.
- Watcher scale handling: Linux inotify watch-limit management with degrade-to-rescan; Windows buffer
  sizing + buffer-overflow recovery via rescan.
- Scheduler: cron expressions + fixed intervals; per-Profile timezone (default system local);
  `MissedRunPolicy` (`CatchUpOnce` coalesces misses into one run at next start; `Skip`).
- Make journal/audit access (M4) concurrency-safe under the pool.

**Out of scope (owning milestone)**
- Manual shell invocation + profile-choice prompt → M8. The Service host that owns these long-lived
  loops → M6 (M5 builds them as components the host will wire).

## Tasks

- [ ] `WorkerPool` (bounded, async) consuming a Job queue; backpressure; graceful drain on shutdown.
- [ ] `PathLockManager` with FIFO fairness; integrate lock acquisition into `JobEngine` for source +
      Target paths; deadlock-free ordering (sort lock keys).
- [ ] Make `FileJournal`/`AuditLog` writers concurrency-safe (single-writer queue or lock).
- [ ] `SourceWatcher`: `FileSystemWatcher`-based events + settle debounce + `ReadinessProbe`
      (per-OS, network-aware). Emit ready files to the Job queue.
- [ ] `ReadinessProbe`: Windows exclusive-open attempt; Linux advisory-lock check + two-sample size
      stability; network detection → size-stability only (logged).
- [ ] Watcher resilience: inotify-limit detection + periodic rescan fallback (Linux);
      `ReadDirectoryChangesW` buffer sizing + overflow → rescan (Windows).
- [ ] `Scheduler`: cron + interval parsing, timezone resolution, due-time evaluation; on start,
      apply `MissedRunPolicy` using last-run timestamps persisted via the journal/state.
- [ ] Tests: pool saturates N workers without exceeding `MaxWorkers`; two Jobs targeting the same path
      serialize (no corruption); a file still being written is not picked up until stable;
      simulated watcher overflow triggers a rescan that catches missed files; `CatchUpOnce` coalesces
      multiple missed windows into one run; `Skip` ignores them.

## Proposed structure

```
src/FileManager.Core/Execution/
  WorkerPool.cs, JobQueue.cs, PathLockManager.cs
src/FileManager.Core/Triggers/Watcher/
  SourceWatcher.cs, ReadinessProbe.cs, WatcherScaleManager.cs, RescanFallback.cs
src/FileManager.Core/Triggers/Schedule/
  Scheduler.cs, CronExpression.cs, MissedRunEvaluator.cs
src/FileManager.Core/State/
  LastRunStore.cs (schedule bookkeeping)
```

## Acceptance criteria

- Under load the pool never exceeds `MaxWorkers` concurrent Jobs and saturates available I/O without
  starving other work.
- Two Jobs that would touch the same absolute path run serialized (FIFO), with no shared-file
  corruption.
- A file being actively written is not ingested until it satisfies both settle + readiness; network
  Sources fall back to size-stability with the caveat logged.
- A simulated buffer overflow / inotify-limit event recovers via rescan with no missed files.
- `CatchUpOnce` runs exactly one coalesced evaluation after downtime spanning several windows; `Skip`
  runs none.

## Dependencies

M4 (journal/audit; schedule last-run state; recovery interplay). Builds on the M1 engine.

## Risks / open items

- Cron library choice must be AOT-safe (prefer a small dependency-free parser or vetted package).
- Advisory locks over SMB/NFS are unreliable (§5.4) — locking is authoritative only within this
  engine instance; documented as not a cross-machine mutex.
- Rescan fallback can be expensive on huge trees; rescan scope is bounded to affected Source roots.
