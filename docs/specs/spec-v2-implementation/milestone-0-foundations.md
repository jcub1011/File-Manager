# Milestone 0 — Foundations, solution restructure & Profile schema

## Goal

Replace the placeholder file-browser project with the multi-project solution the spec implies, and
implement the authoritative **Profile** data model: schema-validated JSON, loadable from a per-OS
config directory, with every enum and sub-object from §5.1 represented as strongly-typed C# records.
By the end of M0 the engine library compiles, loads a `profiles/*.json` set, validates it, and a CI
pipeline builds and tests on Windows and Linux. No file processing happens yet.

## Spec references

- §0 Glossary — terminology (Profile, Job, Source, Target, Transformer, Engine, Payload).
- §1 / §2 — product framing and the three-component architecture (only the project skeleton here).
- §2.1 — IPC transport names (define the `FileManager.Contracts` project and pipe/socket name
  constants; transport implementation is M6).
- §5.1 — Profile schema (JSON), one file per Profile, `SchemaVersion`, enum authority.
- Appendix B — flag the open items this milestone touches (config/log/journal locations; per-OS path
  rules) as TODOs in the schema/docs without resolving them.

## Scope

**In scope**
- Solution restructure into the projects in the table below; retire `src/FileManager`.
- Profile model + all enums + JSON (de)serialization and schema validation.
- Per-OS config-directory resolution and Profile discovery/loading.
- A **global `ServiceConfig`** model + file (sibling to `profiles/`) holding the service-scoped
  settings that are deliberately *not* per-Profile: `MaxWorkers` (§5.4, default = CPU count), the
  optional executable **allowlist** (§9), and the log/journal/audit **file locations + rotation
  sizes** (§7, Appendix B). M0 defines the model, validation, and defaults; M6 consumes it in the
  service host, M5 reads `MaxWorkers`, M9 reads the allowlist.
- Test project, first unit tests (schema round-trip + validation), and minimal CI.

**Out of scope (owning milestone)**
- Any Job execution, filtering, or copying → M1.
- IPC transport wire implementation → M6.
- GUI and Service host projects are *created as stubs only* where needed; real content in M6/M7.

## Tasks

- [ ] Create the solution layout and `Directory.Build.props` (shared `net10.0`, `Nullable=enable`,
      `IsAotCompatible=true`, analyzers). Add a `.editorconfig` matching the existing code style
      (file-scoped namespaces, records, `required` members).
- [ ] Create `src/FileManager.Core` (class library) and `src/FileManager.Contracts` (class library).
- [ ] Migrate reusable scaffold pieces: `IFileSystemService` / `FileSystemService` /
      `FileSystemEntry` into `FileManager.Core` (kept for path enumeration / GUI path-picking).
- [ ] Retire the old `src/FileManager` GUI project and update `FileManager.slnx` to reference the
      new projects.
- [ ] Implement the Profile model as records: `Profile`, `ScheduleTrigger`, `TriggerSet`,
      `SourceSpec`, `TransformerStep`, `TargetSpec`, `PolicySet`, `FilterSet`, `AttributeFilter`,
      `LoggingSpec`.
- [ ] Implement all enums with names matching §5.1 enum authority exactly: `SyncMode`,
      `TargetLayout`, `ConflictResolution`, `OverwriteHandling`, `VerificationMethod`, `OnSuccess`,
      `MetadataOnConflict`, `OnFailure` (single value `AbortRestoreAndClean` today — modeled as an
      enum for future extensibility; the §5.1 sample sets it, so `PolicySet` must carry it or the
      round-trip criterion below fails), `ArgumentMode`, `OutputMode`, `MissedRunPolicy`, `Verbosity`.
- [ ] JSON (de)serialization via **`System.Text.Json` source generators** (AOT-safe — no reflection).
- [ ] Schema validation: required fields, `SchemaVersion` check, enum membership, cross-field rules
      (e.g. `OnSuccess=MoveToArchive` requires `ArchiveFolder`; `NewFile` step requires
      `ExpectedOutputExtension`). Produce a structured list of validation errors, not exceptions.
- [ ] Config-directory resolver: Windows `%APPDATA%\FileManager\`, Linux
      `$XDG_CONFIG_HOME/filemanager/` (fallback `~/.config/filemanager/`); `profiles/` subfolder.
- [ ] `ProfileStore` that discovers and loads all `profiles/*.json`, returning loaded Profiles plus
      per-file validation results.
- [ ] `ServiceConfig` record + loader (`config.json` in the config dir): `MaxWorkers`, `Allowlist`
      (nullable list), and log/journal/audit location + rotation-size settings, all with documented
      defaults; validated like Profiles. Consumed in M5 (`MaxWorkers`), M6 (host wiring), M9
      (allowlist), and M4/M7 (log locations).
- [ ] Define IPC name constants in `FileManager.Contracts`: `\\.\pipe\filemanager-<user>` /
      `$XDG_RUNTIME_DIR/filemanager.sock` (constants + helpers only).
- [ ] `tests/FileManager.Core.Tests` (xUnit): schema round-trip of the §5.1 sample, validation
      success/failure cases, config-path resolution per OS.
- [ ] CI workflow (`.github/workflows/build.yml`): restore/build/test matrix on
      `windows-latest` + `ubuntu-latest`.
- [ ] Add a `CLAUDE.md` / update `README.md` describing the new layout and build commands.

## Proposed structure

```
FileManager.slnx
Directory.Build.props
.editorconfig
src/FileManager.Core/
  FileSystem/    IFileSystemService.cs, FileSystemService.cs, FileSystemEntry.cs   (migrated)
  Profiles/      Profile.cs, TriggerSet.cs, SourceSpec.cs, TransformerStep.cs,
                 TargetSpec.cs, PolicySet.cs, FilterSet.cs, LoggingSpec.cs
  Profiles/Enums.cs
  Profiles/ProfileStore.cs, ProfileValidator.cs, ProfileJsonContext.cs (source-gen)
  Configuration/ ConfigPaths.cs, ServiceConfig.cs, ServiceConfigStore.cs
src/FileManager.Contracts/
  IpcNames.cs
tests/FileManager.Core.Tests/
.github/workflows/build.yml
```

## Acceptance criteria

- `dotnet build` and `dotnet test` succeed on Windows and Linux via CI.
- The exact §5.1 sample Profile round-trips through serialize → deserialize unchanged.
- A Profile missing a required field, using an unknown enum value, or violating a cross-field rule
  produces a descriptive validation error (and is not silently accepted).
- `ProfileStore` loads a folder of multiple Profile files and reports valid/invalid per file.
- `ServiceConfig` loads from `config.json` (and supplies defaults when absent), exposing `MaxWorkers`,
  allowlist, and log/journal/audit locations to later milestones.
- No reflection-based JSON (AOT analyzers produce no warnings).

## Dependencies

None (first milestone).

## Risks / open items

- **Appendix B** path-format rules (UNC, `\\?\` long paths, `~`/env expansion, case sensitivity) are
  *flagged* here but deferred — the model stores raw absolute strings; normalization policy is
  decided in M1 when paths are first acted on.
- Exact config/log/journal/audit locations and rotation sizes are modeled in `ServiceConfig` with
  placeholder defaults here; finalized alongside their consumers (journal + audit + rotating log
  writer in M4; in-GUI display in M7).
