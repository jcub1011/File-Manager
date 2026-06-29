# Milestone 4 — Durable journal, crash recovery & audit trail

## Goal

Make the data-safety guarantees survive a crash. Every Job records its state transitions and the
locations of its temp/staging artifacts to a durable, append-only, fsync'd **journal**. On startup
the engine scans for Jobs left `OPEN` and either cleans, completes, or rolls them back — so an
interruption can never both delete a source and lose its copies. A separate append-only **deletion
audit trail** records every disposition and Mirror deletion.

## Spec references

- §4 Phase 1 (journal `OPEN`) / Phase 6 (journal `CLOSED`).
- §6.3 — durable journal, fsync, startup recovery of `OPEN` Jobs, the
  "source never disposed until all Targets verified" invariant.
- §7 — deletion audit trail (path, action, destination/Trash location, timestamp, Job ID).

## Scope

**In scope**
- Append-only journal with fsync on each record; records Job ID, Profile ID, source path, chosen
  policies, state transitions, and temp/staging artifact locations.
- Journal write points integrated into the lifecycle: `OPEN` at ingestion, transitions through
  distribution/placement, `CLOSED` at successful disposition.
- Startup recovery scanner: for each `OPEN` Job —
  - not-yet-in-placement → clean temp workspace; (watcher/schedule sources) leave for re-detection;
  - mid-placement → use the journal to complete remaining atomic renames + verification, or roll
    back (restoring staged originals via M3), then close the entry.
- Enforce the invariant: source disposition only after the journal records all Targets verified.
- Deletion audit trail: durable append-only log of every source disposition and Mirror deletion.
- **Persistent rotating application log** (§7 bullet 1): a rotating, `Verbosity`-filtered log-file
  writer that backs the engine-level logging surface introduced in M1 (which was in-memory/console
  until now). This is distinct from both the deletion audit trail and the in-GUI activity view (M7);
  M4 owns the file writer + rotation because it shares the durable-writer infrastructure built here.
- Journal compaction/rotation for closed entries; documented file locations (resolves the M0-flagged
  Appendix B item for journal/audit **and log** locations, reading paths/sizes from `ServiceConfig`).

**Out of scope (owning milestone)**
- Concurrency/worker-pool interactions with the journal → M5 (M4 assumes single-Job execution still;
  M5 makes journal access concurrency-safe).

## Tasks

- [ ] Journal record schema (versioned) and an append-only writer with per-record fsync; crash-safe
      framing (length-prefixed + checksum so a torn tail record is detectable).
- [ ] `IJournal` API: `Open(job)`, `Record(transition)`, `Close(job)`, `ReadOpenEntries()`.
- [ ] Integrate journal calls at each lifecycle phase boundary in `JobEngine`.
- [ ] `RecoveryService` run at startup: classify each `OPEN` Job (pre-placement vs mid-placement) and
      drive clean / complete / rollback using M3's `RollbackEngine` and the recorded artifact paths.
- [ ] Assert the source-disposition invariant in code (disposition guarded by an "all Targets
      verified" journal fact).
- [ ] `AuditLog`: append-only writer for dispositions + Mirror deletions (path, action, destination/
      Trash location, timestamp, Job ID).
- [ ] Journal/audit file location resolution + rotation/compaction policy; document final paths and
      sizes (closes the corresponding Appendix B open item).
- [ ] `RotatingLogWriter` for the persistent application log: rotation by the `ServiceConfig` size
      setting, `Verbosity` filtering, wired behind the M1 engine logging surface so all Jobs persist
      to it from service start (independent of whether the GUI is attached).
- [ ] Tests: kill-mid-placement simulation (interrupt before rename, between renames, after some
      Targets) → recovery yields either fully-placed-and-verified or fully-rolled-back, never a
      deleted source with missing copies; torn-tail record is ignored cleanly; audit entries present
      for every deletion.

## Proposed structure

```
src/FileManager.Core/Journal/
  IJournal.cs, FileJournal.cs, JournalRecord.cs, JournalFraming.cs
src/FileManager.Core/Recovery/
  RecoveryService.cs
src/FileManager.Core/Audit/
  AuditLog.cs, AuditEntry.cs
src/FileManager.Core/Logging/
  RotatingLogWriter.cs (Verbosity-filtered persistent app log)
src/FileManager.Core/Jobs/
  JobEngine.cs (journal integration at phase boundaries)
```

## Acceptance criteria

- Killing the engine at any point mid-Job and restarting it never leaves a deleted/disposed source
  with missing Target copies (direct §12 criterion); recovery either completes or rolls back the Job.
- A torn final journal record (simulated partial write) is detected and skipped without corrupting
  recovery.
- Every source disposition and every Mirror deletion appears in the audit trail with the documented
  fields.
- The journal is fsync'd per record (verified via a fault-injection test double).

## Dependencies

M3 (rollback engine, staging, trash) — recovery reuses these to complete/roll back Jobs.

## Risks / open items

- fsync semantics differ across OS/filesystem; the writer abstracts the flush and is tested with a
  fault-injecting stream rather than relying on real power loss.
- Journal growth under high Job volume — rotation/compaction must not race with active writers
  (single-writer now; revisit under the M5 worker pool).
