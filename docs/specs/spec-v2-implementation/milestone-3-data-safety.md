# Milestone 3 — Data safety: verification, rollback, staging, mirror, metadata

## Goal

Deliver the spec's data-safety guarantees so a Job is atomic with respect to its single file: verify
each Target copy before any source cleanup, roll the whole file back across all Targets on any
failure (restoring staged originals), implement the destructive `Mirror` sync mode with deletions
routed to the native trash, preserve metadata best-effort, and detect the one data-losing
configuration. This is the milestone that makes the "strongly guard against accidental file loss"
promise real (durability across crashes is M4).

## Spec references

- §3.3 — verification (`SizeTimestamp` / `SHA256` / `None`) and rollback scope (steps 1–5).
- §3.1.1 — `Mirror` (exact replica; absent-at-source files removed **to trash**, never hard-deleted).
- §5.3 — soft deletion: Windows `IFileOperation` (Recycle Bin); Linux FreeDesktop Trash spec.
- §6.1 — the one data-losing combination (`VerificationMethod=None` + source-removing `OnSuccess`).
- §6.2 — `OverwriteHandling`: `StageOverwrites` (stage prior version, restore on rollback).
- §6.4 — metadata preservation + `MetadataOnConflict` (`WarnAndContinue` / `FailJob`).
- §4 Phase 5 (verification + staged-overwrite placement), Phase 6 (`MoveToTrash` becomes native).

## Scope

**In scope**
- `VerificationMethod` against the Job's final temp output: `SizeTimestamp` (with tolerance caveat),
  `SHA256` (streamed), `None`. Verification gates source disposition.
- Rollback engine implementing §3.3 steps: abort remaining steps; remove freshly-written/un-renamed
  temp artifacts from **all** Targets (incl. completed ones); restore staged prior versions; leave
  source untouched; log + surface.
- `OverwriteHandling=StageOverwrites`: move prior Target file to a per-Job staging area immediately
  before rename; restore on rollback, discard on success.
- `SyncMode=Mirror`: compute the Target-vs-aggregated-Source difference, route surplus Target files
  to native trash; the GUI flagging is M7.
- Native soft delete: `IFileOperation` (Windows), FreeDesktop Trash (Linux) — powering both
  `OnSuccess=MoveToTrash` and Mirror deletions.
- Metadata preservation (timestamps + permissions/ACLs best-effort); pre-copy detectable-loss
  warnings; `MetadataOnConflict` runtime behavior.
- Engine-side detection signal for the §6.1 combination (surfaced as a flag for M7's blocking UI).

**Out of scope (owning milestone)**
- Persisting rollback/journal state across a crash → M4 (M3's rollback is in-process/in-memory).
- The deletion **audit trail** persistence → M4. The GUI warning for §6.1 → M7.

## Tasks

- [ ] `IVerifier` with `SizeTimestampVerifier` (size + mtime within tolerance; document FAT/NTFS/
      ext4/network caveat), `Sha256Verifier` (streamed, no whole-file buffering), and a `None` no-op.
- [ ] Wire verification into Phase 5: verify each Target temp copy against the final temp output
      before atomic rename; failure → rollback.
- [ ] `RollbackContext` tracking, per Target, the temp artifacts written and any staged original.
- [ ] `RollbackEngine` performing §3.3 steps across all Targets; ensure no Target is left with a
      half-finished set for the file.
- [ ] `StagingArea` per Job; `StageOverwrites` move-before-rename + restore/discard.
- [ ] `ITrashService` with `WindowsRecycleBin` (`IFileOperation` via COM interop) and `LinuxTrash`
      (FreeDesktop `~/.local/share/Trash/` with `.trashinfo`). Native `OnSuccess=MoveToTrash` lands by
      having `SourceDisposer` consume `ITrashService` (or by a native `ISourceDisposer`) — the M1
      placeholder trash is swapped behind the existing `ISourceDisposer` seam, not by editing
      `JobEngine`. See the [engine service & dependency pattern](README.md#engine-service--dependency-pattern).
- [ ] `MirrorPlanner`: enumerate aggregated Source set vs Target set, identify surplus, route to trash;
      integrate as a post-placement step under `SyncMode=Mirror`.
- [ ] `MetadataCopier`: timestamps + Unix mode bits / Windows ACLs best-effort; detect pre-copy loss
      (e.g. NTFS→exFAT) and apply `MetadataOnConflict`.
- [ ] `SafetyAnalyzer.Evaluate(Profile)` returning the §6.1 risk level (blocking for
      `None`+`PermanentDelete`, warning for `None`+`MoveToTrash`).
- [ ] Tests: forced failure at distribution / step / verification each reverts the file across all
      Targets and restores staged originals; `Mirror` deletions land in trash (recoverable);
      `SHA256` catches a corrupted copy; metadata `FailJob` rolls back; `SafetyAnalyzer` flags the
      §6.1 combo.

## Proposed structure

```
src/FileManager.Core/Verification/
  IVerifier.cs, SizeTimestampVerifier.cs, Sha256Verifier.cs, NoneVerifier.cs
src/FileManager.Core/Safety/
  RollbackEngine.cs, RollbackContext.cs, StagingArea.cs, SafetyAnalyzer.cs
src/FileManager.Core/Sync/
  MirrorPlanner.cs
src/FileManager.Core/Trash/
  ITrashService.cs, WindowsRecycleBin.cs (IFileOperation interop), LinuxTrash.cs
src/FileManager.Core/Metadata/
  MetadataCopier.cs
```

## Acceptance criteria

- Injecting a failure at each of Phase 3/4/5 leaves the source intact and every Target clean; under
  `StageOverwrites`, the replaced Target file is restored byte-for-byte.
- `Mirror` produces an exact replica of the aggregated Source set, and every surplus Target file is
  recoverable from the Recycle Bin / Trash (never hard-deleted).
- `SHA256` verification fails a deliberately corrupted Target copy and triggers rollback.
- `SafetyAnalyzer` returns *blocking* for `None`+`PermanentDelete` and *warning* for
  `None`+`MoveToTrash`.

## Dependencies

M2 (transformer chain produces the final temp output; abort hook to extend into full rollback).

## Risks / open items

- Windows `IFileOperation` COM interop must stay AOT-compatible (explicit interface declarations, no
  reflection-based COM); validate against the trim/AOT analyzers.
- `SizeTimestamp` is best-effort by design (timestamp resolution varies); tests assert the tolerance
  window behavior, not exactness.
- In-memory rollback here is not crash-durable — M4 layers the journal on top so an interrupted
  rollback can be resumed.
