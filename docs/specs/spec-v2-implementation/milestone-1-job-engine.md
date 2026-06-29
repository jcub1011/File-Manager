# Milestone 1 — Single-file Job engine (happy path)

## Goal

Turn a Profile + a single ready file into a completed **Job** that distributes the file to all
configured Targets and disposes of the source — the spec's per-file execution unit (§3.1, §4) end to
end, minus transformers (M2) and verification/rollback (M3). After M1 the engine can run all four
topologies with `AdditiveArchive`, both target layouts, conflict resolution, and `DirectOverwrite`
placement, driven by a programmatic "process this path" entrypoint (real triggers arrive in M5).

## Spec references

- §3.1 / §3.1.1 (`AdditiveArchive`) / §3.1.2 (`TargetLayout`) — topologies and target layout.
- §3.4 — conflict resolution (`Overwrite`, `OverwriteIfNewer`, `RenameSuffix`, `Skip`).
- §4 Phases 1, 2, 4, 5 (atomic placement only), 6 (basic disposition).
- §5.2 — filename tokens (`$filename_stem`, `$extension`, `$filename_current`, `$source_root_path`).
- §6.2 — copy-to-temp + atomic rename mechanism (`DirectOverwrite` path only).

## Scope

**In scope**
- `Job` model and a state machine over the lifecycle phases (verification stubbed as pass-through).
- Filter screening (§5.1 `FilterSet`): include/exclude globs, include/exclude regex, size bounds,
  modified/created age, attributes (hidden/system/symlink), `MaxDepth`, content-hash dedupe.
  Global + per-Source filters with per-Source override. Screened-out files logged `SKIPPED`.
- Topology routing: 1:1, 1:N, M:1 (flatten-forced), M:N; `PreserveStructure` vs `Flatten`.
- Per-Target write to a non-conflicting temp name, then atomic rename into place.
- `ConflictResolution` at the Target (and M:1 cross-source order = Profile source order).
- `OnSuccess`: `KeepSource`, `MoveToArchive`, `PermanentDelete`, and a *non-native* `MoveToTrash`
  placeholder (native Recycle Bin / FreeDesktop Trash lands in M3).
- Path normalization policy (resolves the M0-flagged Appendix B item for the scope used here).
- Structured per-Job logging at the engine level (`Verbosity`).

**Out of scope (owning milestone)**
- Transformer chain → M2. Verification + real rollback + `StageOverwrites` + `Mirror` + native
  trash + metadata → M3. Journal/recovery → M4. Triggers + worker pool + locking → M5.

## Tasks

- [ ] `Job` record: Job ID, Profile reference, source path + metadata snapshot, chosen policies,
      and a `JobState` enum (`Open`, `Screened`, `Distributing`, `Placed`, `Closed`, `Skipped`,
      `Failed`).
- [ ] `JobEngine.ProcessFile(Profile, string sourcePath, IngestionContext)` entrypoint that runs the
      lifecycle synchronously (concurrency added in M5).
- [ ] Filter screening component reusable for dry-run later (M7); record the **deciding filter** for
      each skip so the dry-run report can reuse it (§8).
- [ ] Glob + regex matching helpers; size/age evaluation; attribute checks (hidden/system/symlink
      via `System.IO` attributes); depth computation relative to the Source root; content-hash dedupe
      (hash strategy noted as Appendix B open item — compute-on-demand for now).
- [ ] Target resolver: for each Target, compute the destination path from `TargetLayout` and the
      file's relative path under its Source; force `Flatten` for M:1 aggregation.
- [ ] Conflict resolution at destination per `ConflictResolution` (incl. `RenameSuffix` "name (1).ext"
      incrementing, `OverwriteIfNewer` mtime comparison).
- [ ] Copy-to-temp-name in the Target directory, streamed (no whole-file buffering, §11), then atomic
      rename; `DirectOverwrite` replaces in place.
- [ ] Token expansion for filename tokens (§5.2), used by conflict naming and (later) transformers.
      The `TokenExpander` owns the shared §5.2 rules — `$name` delimiter, `$$` literal-dollar escape,
      case-sensitive names, one value per token — so M2's step tokens extend the same component and
      preview/runtime cannot drift.
- [ ] `OnSuccess` disposition (basic): keep / move-to-archive / permanent-delete / placeholder trash.
- [ ] Engine-level logging surface (verbosity-filtered) + a `JobResult` summarizing outcome.
- [ ] Unit tests: each topology produces the documented Target state; each `ConflictResolution`
      branch; layout preserve/flatten; M:1 source-order priority; skip-logging.

## Proposed structure

```
src/FileManager.Core/Jobs/
  Job.cs, JobState.cs, JobResult.cs, JobEngine.cs, IngestionContext.cs
src/FileManager.Core/Filtering/
  IFilterEvaluator.cs + FilterEvaluator.cs, GlobMatcher.cs, AttributeChecks.cs,
  IDedupeIndex.cs + DedupeIndex.cs
src/FileManager.Core/Routing/
  TargetResolver.cs, IConflictResolver.cs + ConflictResolver.cs
src/FileManager.Core/IO/
  AtomicFileWriter.cs (copy-to-temp + rename, streamed), PathNormalizer.cs
src/FileManager.Core/Tokens/
  TokenExpander.cs (filename tokens)
src/FileManager.Core/Disposition/
  ISourceDisposer.cs + SourceDisposer.cs (basic OnSuccess)
```

The phase collaborators that perform I/O or carry policy — `FilterEvaluator`, `DedupeIndex`,
`ConflictResolver`, `SourceDisposer` (and M2's `TransformerRunner`) — are **interface-backed services
injected into `JobEngine`** per the [engine service & dependency pattern](README.md#engine-service--dependency-pattern):
they take `IFileOperations` (or each other) through their constructors rather than as per-call static
arguments, so the orchestrator can be tested with fakes. The pure helpers (`TargetResolver`,
`GlobMatcher`, `AttributeChecks`, `PathNormalizer`, `TokenExpander`, `AtomicFileWriter`) stay
`static`. `ISourceDisposer` is also the seam M3 swaps to deliver native-trash disposition.

## Acceptance criteria

- For a fixed input tree, each of 1:1 / 1:N / M:1 / M:N under `AdditiveArchive` produces exactly the
  documented Target contents (M:1 flattened; layouts honored otherwise).
- Each `ConflictResolution` value yields the documented behavior on a pre-existing Target file.
- A file failing a filter is logged `SKIPPED` with the deciding filter and the Job ends gracefully.
- Target files are never observed half-written (temp + atomic rename); large files stream without
  large memory growth.
- The source file is unchanged for `KeepSource`; moved/removed as configured otherwise.

## Dependencies

M0 (Profile model, enums, config paths, file-system service).

## Risks / open items

- Atomic rename across volumes is not atomic; `AtomicFileWriter` must temp **within the Target
  directory** so the rename stays same-volume.
- Path normalization (Appendix B) is decided here for the cases exercised; revisit for UNC/long-path
  edge cases in M9 network-target work.
