# Functional & Technical Specification: Cross-Platform File Pipeline Utility

**Version:** 2 (supersedes `spec-draft-v1.md`)
**Status:** Draft for implementation planning
**Last updated:** 2026-06-25

> This revision incorporates the audit of v1 (`docs/specs/spec-draft-v1.md`) and the design
> decisions made during stakeholder review. Changes from v1 are summarized in
> [Appendix C: Changes from v1](#appendix-c-changes-from-v1).

---

## 0. Glossary

To remove the v1 terminology drift ("templates" / "profiles" / "configuration schemas" were used
interchangeably), the following terms are authoritative throughout this document.

| Term | Definition |
| --- | --- |
| **Profile** | A single, named, machine-specific automation definition (sources, transformers, targets, policies, filters). The unit a user configures. Serialized as JSON. Replaces v1's "template". |
| **Job** | One execution of a Profile against **one file**. A Job owns its own lifecycle, temp workspace, verification, rollback, and journal entry. |
| **Source file / asset** | A single file that has matched a Profile's sources and filters and entered a Job. |
| **Payload** | The path + action information passed into the engine by a trigger (watcher event, schedule tick, or shell invocation). |
| **Source** | A configured input directory belonging to a Profile. |
| **Target** | A configured output directory belonging to a Profile. |
| **Transformer (step)** | One external CLI invocation in a Profile's ordered processing chain. |
| **Engine / Core Service** | The headless background process that watches, schedules, executes Jobs, and owns the durable journal. |

---

## 1. Product Overview

The File Pipeline Utility is a lightweight, rule-based file synchronization, aggregation,
transformation, and automation tool for Windows and Linux. It runs as a background process that
manages a user-facing tray indicator (where the desktop environment supports one), a configuration
GUI, and operating-system context-menu integration.

Its purpose is to automate multi-point file-routing and processing workflows — transcoding
audio/video, compiling project assets, distributing build artifacts, aggregating data folders —
while applying verification and staging layers that **strongly guard against accidental file loss**.

> **Wording note (v1 fix):** v1 claimed to "eliminate" file loss. No copy tool can make that an
> absolute guarantee. The design *minimizes and makes recoverable* the data-loss surfaces; the exact
> guarantees and their limits are stated in [§3.3](#33-transactional-verification--rollback) and
> [§6](#6-data-safety-model).

### 1.1 Process model (service vs. tray)

v1 conflated "background service" and "task tray". They are split here:

* The **Core Service** is the long-lived engine. On Windows it runs as a per-user background
  process started at logon (not a machine-wide Windows Service in v2 — it operates on the logged-in
  user's mounted drives and recycle bin). On Linux it runs as a **systemd user service**
  (`systemctl --user`).
* The **Tray Indicator** is an optional UI surface attached to the Core Service when a desktop
  session with a system tray exists. Where no tray is available (e.g. headless, or GNOME without an
  extension), the service runs without it and is managed via the GUI and CLI. The service never
  depends on the tray being present.

---

## 2. System Architecture

The utility separates ingestion (**Sources**), processing (**Transformers**), and distribution
(**Targets**) behind a single execution engine, so it stays agnostic to specific OS file managers
while supporting the four directory topologies in [§3.1](#31-directory-topologies).

```
                   ┌─────────────────────────────┐
                   │     OS File Explorer        │
                   │  (Windows / Linux Context)  │
                   └──────────────┬──────────────┘
                                  │ Passes Payload (Path + Action)
                                  ▼
┌──────────────────┐    IPC    ┌─────────────────────────────┐  Reads / Writes  ┌──────────────────┐
│  Management GUI  │◄─────────►│       Core Service          │◄────────────────►│  Profile JSON +  │
└──────────────────┘   (local  │   (Engine + Tray + Journal) │                  │  Durable Journal │
                        socket) └──────────────┬──────────────┘                  └──────────────────┘
                                              │ Executes per-file Jobs
                                              ▼
                        ┌──────────────────────────────────────────┐
                        │ Sources ──► Transformers ──► Targets      │
                        └──────────────────────────────────────────┘
```

1. **Core Service (headless engine):** watches source roots, runs the scheduler, owns the bounded
   worker pool ([§5.4](#54-concurrency--locking)), executes Transformer chains, performs verification
   and rollback, and persists the durable journal ([§6.3](#63-crash--restart-recovery)).
2. **Shell Integration Layer:** platform-specific registrations that pass a Payload (path + action)
   to the engine. The shell entry hands off to the running service over the same local IPC channel;
   if the service is not running it is started and the Payload queued. (v1 left "CLI flags or IPC"
   ambiguous — IPC to the running service is the canonical path; a CLI flag exists only as the
   fallback launcher.)
3. **Configuration GUI:** create/edit Profiles, view live engine state and the activity/error view
   ([§7](#7-observability-logging--notifications)), and run **dry-run simulations**
   ([§8](#8-dry-run--simulation)).

### 2.1 IPC transport

GUI↔Service and Shell↔Service communication uses a **local-only transport**: a named pipe on
Windows (`\\.\pipe\filemanager-<user>`) and a Unix domain socket on Linux
(`$XDG_RUNTIME_DIR/filemanager.sock`). No network listener is opened. The wire format is
length-prefixed JSON messages.

---

## 3. Core Functional Requirements

### 3.1 Directory Topologies

The engine routes files across four topologies. **The unit of execution is always a single file
(a Job); topologies describe how many Sources feed and how many Targets receive.**

* **One-to-One (1:1):** a single Source to a single Target.
* **One-to-Many (1:N — Distribution):** one Source distributes each file to N Targets.
* **Many-to-One (M:1 — Aggregation):** M Sources feed one Target. Aggregation **flattens** by
  default (subfolder structure collapsed into the Target root); cross-source name collisions are
  resolved by the Profile's conflict policy ([§3.4](#34-conflict-resolution)).
* **Many-to-Many (M:N):** M Sources feed N Targets.

#### 3.1.1 Sync mode (replaces the ambiguous "mirror")

Every Profile declares a **`SyncMode`**, because "mirror" in v1 was undefined and potentially
destructive at the target:

* **`AdditiveArchive`** — Targets only ever gain or update files. Nothing at a Target is ever removed
  for being absent from a Source. Safe default.
* **`Mirror`** — Targets become an **exact replica** of the (aggregated) Source set: files present at
  a Target but absent from the Source set are **removed to the Recycle Bin / Trash** (never hard
  deleted). This is destructive at the Target by design; the GUI flags it clearly.

#### 3.1.2 Folder structure at target

Per-Profile **`TargetLayout`**: `PreserveStructure` (default) recreates each file's relative
subfolder path under the Target; `Flatten` places every file in the Target root. Aggregation (M:1)
forces `Flatten` regardless of this setting.

### 3.2 Automated Execution Triggers

A Profile may enable any combination of:

* **Manual Shell Invocation:** right-click context-menu action on a file or folder. On a folder,
  descent is recursive subject to the Profile's `MaxDepth` filter. **Profile selection:** the engine
  **always prompts the user to pick** which Profile to run for the invoked path (even when exactly
  one matches), so a manual action is never ambiguous or surprising. The prompt lists the Profiles
  whose Sources/filters match the invoked path; if none match (e.g. the path lies outside every
  configured Source), the prompt is never an empty dead end — it always offers a **"Create Profile…"**
  action that opens the configuration GUI pre-seeded with the invoked path.
* **Reactive File Watcher:** continuous monitoring of Source roots. A configurable per-Source
  **settle policy** decides when a file is ready ([§3.2.1](#321-file-readiness-settle-policy)).
* **Scheduled / Interval:** cron expression or fixed interval. Each scheduled Profile declares a
  timezone (default: system local). **Missed-run policy** (`CatchUpOnce` default, or `Skip`) governs
  what happens if the service was off at the due time ([§3.2.2](#322-missed-scheduled-runs)).

#### 3.2.1 File readiness (settle policy)

> **v1 fix:** v1's "settling delay ensures file writes are closed" overstated what a debounce can do.

A file is considered ready only when **both** hold: (a) no change events for `SettleDelaySeconds`,
**and** (b) a readiness probe succeeds — the engine opens the file for exclusive read (Windows) /
checks it is not advisory-locked and its size is stable across two probes `StabilityIntervalMs`
apart (Linux). Network sources relax to size-stability only, since exclusive-open semantics over
SMB/NFS are unreliable; this caveat is logged per Job.

#### 3.2.2 Missed scheduled runs

Per Profile: `CatchUpOnce` (default) runs a single evaluation at next service start, coalescing
multiple misses into one; `Skip` ignores missed windows and waits for the next scheduled time.

### 3.3 Transactional Verification & Rollback

* **Verification (`VerificationMethod`, default ON):** before any source cleanup, each Target copy
  is verified against the **final transformed file in the Job's temp workspace** using one of:
  * `SizeTimestamp` (Tier 1) — size match plus modified-time within a tolerance. *Caveat:* timestamp
    resolution/preservation varies across FAT/NTFS/ext4/network and copy method, so a tolerance
    window is used and Tier 1 is best-effort.
  * `SHA256` (Tier 2) — full byte-stream checksum. Authoritative but costs a full read of each copy.
  * `None` — no verification. Permitted for throughput, **but** the GUI raises a blocking warning
    whenever `VerificationMethod = None` **and** `OnSuccess` deletes/permanently removes the source
    (see [§6.1](#61-the-one-data-losing-combination)).
* **Rollback scope (made explicit):** a Job is atomic with respect to **its single file**. If any
  Target write, Transformer step, or verification fails:
  1. The engine aborts the remaining steps for that Job.
  2. It removes that Job's freshly-written / un-renamed temp artifacts from **all** Targets,
     including Targets that had already completed for this file (so no Target is left with a
     half-finished set for the file).
  3. For Targets where an existing file was replaced under `StageOverwrites`
     ([§6.2](#62-overwrite-handling)), the staged prior version is **restored**.
  4. The source file is left **untouched**.
  5. The event is logged and surfaced per [§7](#7-observability-logging--notifications).

> Because the unit is a single file, "partial multi-target failure" (v1 gap A6) is resolved cleanly:
> rollback reverts that one file across every Target; other files' Jobs are unaffected.

### 3.4 Conflict Resolution

When a file would collide with an existing name at a Target (or across Sources during M:1
aggregation), the Profile's `ConflictResolution` decides:

* `Overwrite` — replace the existing file (subject to overwrite handling, [§6.2](#62-overwrite-handling)).
* `OverwriteIfNewer` — replace only if the incoming file's modified-time is newer than the existing
  Target file's modified-time.
* `RenameSuffix` — write the incoming file with an incrementing suffix (`name (1).ext`).
* `Skip` — leave the existing Target file; skip the incoming file (logged).

> v1's undefined `Source Priority Override` is dropped. For M:1 aggregation collisions, source order
> in the Profile defines priority when `Overwrite`/`OverwriteIfNewer` is used.

---

## 4. End-to-End Processing Workflow

Each ready file runs as an independent **Job** through this lifecycle. Aggregation/distribution are
expressed by how many Sources feed and how many Targets receive within each Job — there is no
separate batch state machine.

```
 [Source file ready] ──► 1. Ingestion (Watcher / Schedule / Shell) + Journal: OPEN
                           │
                           ▼
                     2. Filter Matching (Include / Exclude / size / age / attrs / depth / dedupe)
                           │  (fail → Job ends, logged as SKIPPED)
                           ▼
                     3. Transformer chain (sequential; each step new-file OR in-place)
                           │  (non-zero exit / timeout → abort + rollback)
                           ▼
                     4. Target distribution (copy-to-temp-name per Target, bounded parallel)
                           │
                           ▼
                     5. Integrity verification (per VerificationMethod) + atomic rename into place
                           │  (fail → rollback, restore staged originals)
                           ▼
                     6. Source disposition (per OnSuccess) + Journal: CLOSED
```

### Phase 1 — Ingestion & Journal open

On trigger, the engine assigns a unique Job ID, records a **journal entry** (state `OPEN`) capturing
the Profile ID, source path, and chosen policies, locks the source file's metadata snapshot, and
loads the active Profile.

### Phase 2 — Filter screening

The file is evaluated against the Profile's filters ([§5.1](#51-profile-schema-json)). Filters
include globs, regex, size bounds, modified-time and created-time age, file attributes, subfolder
depth, and content-hash dedupe. Filters may be defined globally for the Profile and/or per-Source;
per-Source rules override global. **A screened-out file is logged as `SKIPPED`** (not silently
dropped — v1 said "quietly skipped", which contradicted the log-everything posture) and the Job ends
gracefully.

### Phase 3 — Transformer chain

If the Profile has Transformers, the engine creates an isolated temp workspace
(`<Profile temp root>/.pipeline_tmp/<JobId>/`) and runs each step in order:

1. **Token expansion** — dynamic tokens ([§5.2](#52-tokens)) expand to absolute paths in the
   sandbox. Each token expands to **one** value; how it is inserted depends on argument mode (below).
2. **Step I/O mode (`OutputMode`)** — resolves v1's contradiction where the example `mid3v2` step was
   in-place yet the model assumed new files:
   * `NewFile` — the step writes a distinct output (`$step_output_path`, named with
     `ExpectedOutputExtension`); that output becomes the next step's input.
   * `InPlace` — the step mutates `$step_input_path` directly and produces no new file; the same
     working file (a copy made on entry to the chain, never the original source) carries forward.
3. **Argument handling** — `ArgumentMode` is `Literal` (default) or `Shell`:
   * `Literal` — `Arguments` is parsed into a fixed argv list; tokens are substituted as single,
     un-split values. No shell, so spaces / quotes / `$(...)` in filenames cannot break out. Safe.
   * `Shell` — `Arguments` is passed to `cmd.exe`/`/bin/sh` for users who need pipes, redirection,
     wildcards, or env vars. Opt-in per step; the GUI marks it as a higher-risk mode and the engine
     applies platform escaping to substituted tokens. See [§9](#9-security-model).
4. **Process invocation** — the executable runs as a child process with `TimeoutSeconds`. `stdout`
   and `stderr` are captured to the Job log.
5. **Success check** — exit code `0` (or any code in an optional `SuccessExitCodes` list) succeeds;
   the engine then frees the prior step's intermediate file. Any other exit code, or a timeout,
   **aborts the chain and triggers rollback** ([§3.3](#33-transactional-verification--rollback)).

### Phase 4 — Target distribution

The final file state is written to every configured Target. Each Target write goes to a
**non-conflicting temporary name** in the Target directory first (enabling the atomic rename in
Phase 5 and bounding the partial-write window). Writes across Targets run on the bounded worker pool
([§5.4](#54-concurrency--locking)). Name collisions are resolved per
[§3.4](#34-conflict-resolution). Metadata is preserved best-effort per [§6.4](#64-metadata-preservation).

### Phase 5 — Verification & atomic placement

Per `VerificationMethod`, each Target's temp copy is verified against the Job's final temp output.
On success the temp copy is **atomically renamed** to its final name; under `StageOverwrites`, any
existing Target file is moved to staging immediately before the rename. On any failure, rollback
runs ([§3.3](#33-transactional-verification--rollback)).

### Phase 6 — Source disposition

After all Targets are verified and placed, the Job applies `OnSuccess` ([§5.1](#51-profile-schema-json)):
`KeepSource` (pure copy/sync), `MoveToTrash`, `MoveToArchive` (into a configured folder),
or `PermanentDelete` (explicit opt-in). The journal entry is set to `CLOSED`.

> **Wording fix:** v1 called this "Atomic Source Deletion". Deletion of a file to Recycle Bin/Trash
> is not atomic in the transactional sense; since the unit is a single file, the disposition of that
> one file simply succeeds or is logged as a disposition error (the copies at Targets are already
> safe at this point).

---

## 5. Technical & Operational Specifications

### 5.1 Profile schema (JSON)

Profiles are schema-validated JSON. **Storage:** one Profile per file under the config directory
(`profiles/*.json`); the service loads all of them. A top-level `SchemaVersion` enables future
migration. Paths are **machine/OS-specific** and absolute (Profiles are not portable across OSes).

```json
{
  "SchemaVersion": 2,
  "ProfileId": "e2a3c4b5-1234-4a5b-8c9d-0123456789ab",
  "Name": "Chained Audio Optimization Pipeline",
  "Active": true,

  "SyncMode": "AdditiveArchive",
  "TargetLayout": "PreserveStructure",

  "Triggers": {
    "ManualShell": true,
    "Watcher": true,
    "Schedule": {
      "Enabled": false,
      "Cron": "0 */6 * * *",
      "Timezone": "America/Chicago",
      "MissedRunPolicy": "CatchUpOnce"
    }
  },

  "Sources": [
    {
      "Path": "C:\\dropzone\\raw",
      "SettleDelaySeconds": 2,
      "StabilityIntervalMs": 500,
      "Filters": null
    }
  ],

  "Transformers": [
    {
      "Step": 1,
      "Name": "FFMPEG Audio Transcoder",
      "ExecutablePath": "C:\\tools\\ffmpeg.exe",
      "ArgumentMode": "Literal",
      "Arguments": "-i $step_input_path -b:a 320k $step_output_path",
      "OutputMode": "NewFile",
      "ExpectedOutputExtension": ".mp3",
      "SuccessExitCodes": [0],
      "TimeoutSeconds": 120
    },
    {
      "Step": 2,
      "Name": "ID3 Metadata Tagger",
      "ExecutablePath": "C:\\tools\\mid3v2.exe",
      "ArgumentMode": "Literal",
      "Arguments": "--artist=Production $step_input_path",
      "OutputMode": "InPlace",
      "TimeoutSeconds": 30
    }
  ],

  "Targets": [
    { "Path": "C:\\archive\\local" },
    { "Path": "Z:\\vault" }
  ],

  "Policies": {
    "ConflictResolution": "RenameSuffix",
    "OverwriteHandling": "StageOverwrites",
    "VerificationMethod": "SHA256",
    "OnSuccess": "MoveToTrash",
    "ArchiveFolder": null,
    "OnFailure": "AbortRestoreAndClean",
    "MetadataOnConflict": "WarnAndContinue"
  },

  "Filters": {
    "Include": ["*.wav", "*.flac"],
    "ExcludeGlob": [".DS_Store", "Thumbs.db"],
    "IncludeRegex": null,
    "ExcludeRegex": null,
    "MinSizeBytes": null,
    "MaxSizeBytes": null,
    "ModifiedWithin": null,
    "ModifiedOlderThan": null,
    "CreatedWithin": null,
    "Attributes": { "IncludeHidden": false, "IncludeSystem": false, "FollowSymlinks": false },
    "MaxDepth": null,
    "ContentHashDedupe": false
  },

  "Logging": { "Verbosity": "FailuresAndSkips", "NotifyOnFailure": true }
}
```

> **Enum authority (v1 fix):** policy/verification values are now consistent across prose and schema.
> `OnSuccess ∈ {KeepSource, MoveToTrash, MoveToArchive, PermanentDelete}`;
> `VerificationMethod ∈ {SizeTimestamp, SHA256, None}`;
> `ConflictResolution ∈ {Overwrite, OverwriteIfNewer, RenameSuffix, Skip}`;
> `OverwriteHandling ∈ {DirectOverwrite, StageOverwrites}`;
> `SyncMode ∈ {AdditiveArchive, Mirror}`; `TargetLayout ∈ {PreserveStructure, Flatten}`;
> `MetadataOnConflict ∈ {WarnAndContinue, FailJob}`;
> `OnFailure ∈ {AbortRestoreAndClean}` (single value today; the enum exists as an extension point —
> the §3.3 rollback behavior is what `AbortRestoreAndClean` denotes);
> `Verbosity ∈ {FailuresOnly, FailuresAndSkips, All}` (see [§7](#7-observability-logging--notifications)).

### 5.2 Tokens

Tokens expand to absolute paths/strings relative to the active step. **Delimiter:** `$name`; a
literal dollar is escaped `$$`. Token names are case-sensitive.

* `$source_root_path` — the Source directory that contained this file. For M:1 aggregation the
  current file always originates from exactly one Source, so this is unambiguous per Job.
* `$step_input_path` — absolute path of the file entering the active step.
* `$step_output_path` — absolute path the active step must write to (NewFile steps only).
* `$filename_stem` — base name **without** extension of the current step's input.
* `$extension` — extension (including leading dot) of the current step's input.
* `$filename_current` — `$filename_stem` + `$extension` (full base name, no directory).

> v1's single ambiguous `$filename_current` ("excluding path wrappers") is split into
> `$filename_stem` / `$extension` so steps that change extension remain unambiguous.

### 5.3 Operating-System Integration

#### Windows

* **Context menu:** registered per-user under `HKCU\Software\Classes\...\shell` (no admin required)
  for `Directory`, `Directory\Background`, and `AllFilesystemObjects`. To obtain a **top-level** entry
  in the Windows 11 context menu (not only under "Show more options"), the integration ships an
  `IExplorerCommand` handler packaged via a sparse MSIX package; the legacy registry verb remains as
  a fallback for Windows 10 and the classic menu.
* **Soft deletion:** uses `IFileOperation` so `MoveToTrash` populates the native Recycle Bin.

#### Linux

* **Context menu:** the installer registers actions for the file managers it detects —
  Nautilus (extension/scripts), Dolphin (ServiceMenu `.desktop`), and Nemo/Thunar custom actions.
  Each maps the menu action to a Payload sent over the IPC socket. Coverage is explicit per file
  manager rather than a single assumed `.desktop` path.
* **Soft deletion:** conforms to the FreeDesktop Trash Specification (`~/.local/share/Trash/`).

#### Installation & autostart

Windows: per-user install; the Core Service is registered as a logon startup task. Linux: a
`systemd --user` unit (`filemanager.service`) enabled for the user. The service runs without a tray
if none is available.

### 5.4 Concurrency & Locking

* **Execution model:** a single **bounded worker pool** (configurable `MaxWorkers`, default = CPU
  count) runs Jobs concurrently regardless of which Profile they belong to. This is the single
  authoritative model — it replaces v1's conflicting "asynchronous queue" / "parallel threads" /
  "strict sequential loop" statements. Within one Job, writes to multiple Targets may also use the
  pool.
* **Same-file collision rule:** the engine maintains a lock keyed by absolute path. A Job acquires
  locks on its source file and each Target temp/final path before acting; a second Job that would
  touch a locked path waits (FIFO) rather than racing. This prevents two Jobs from corrupting a
  shared Target file and replaces v1's vague "shared hardware sectors" language.
* **Network caveat:** advisory locks over SMB/NFS are unreliable; locking is enforced in-process
  (authoritative for this engine instance) and is **not** a cross-machine mutex.

---

## 6. Data Safety Model

### 6.1 The one data-losing combination

The only configuration that can lose data is `VerificationMethod = None` combined with an `OnSuccess`
that removes the source (`MoveToTrash` is recoverable; `PermanentDelete` is not). The GUI shows a
**blocking warning** when a Profile sets `VerificationMethod = None` together with
`OnSuccess = PermanentDelete`, and a non-blocking warning for `MoveToTrash`. Verification defaults to
ON precisely to keep this off the default path.

### 6.2 Overwrite handling

When a Target file is replaced, the engine always copies to a temporary name first and then
**atomically renames** over the destination, so a Target file is never left half-written.
Per-Profile `OverwriteHandling`:

* `DirectOverwrite` — the rename replaces the prior version, which is then gone. Maximum throughput;
  rollback cannot restore an already-replaced Target file (only un-renamed temp artifacts are
  cleaned).
* `StageOverwrites` — immediately before the rename, the prior version is moved to a per-Job staging
  area; it is **restored on rollback** and discarded on success. Maximum safety, extra disk + I/O.

### 6.3 Crash / restart recovery

The engine writes a **durable journal** (append-only, fsync'd) recording each Job's state
transitions and the locations of its temp/staging artifacts. On startup the engine scans for Jobs
left `OPEN`:

* If the Job had not begun final placement, its temp workspace is cleaned and (for watcher/schedule
  sources) the file is re-detected on the next scan.
* If the Job was mid-placement, the engine uses the journal to either complete the remaining atomic
  renames + verification or roll them back (restoring staged originals), then closes the entry.

The source file is never disposed of until the journal records all Targets verified, so an
interruption can never both delete a source and lose its copies.

### 6.4 Metadata preservation

Timestamps and permissions/ACLs are preserved **best-effort** (Unix mode bits / Windows ACLs;
cross-filesystem mapping is imperfect). When loss/alteration is **detectable before** the copy
(e.g. NTFS→exFAT, or crossing OS permission models), the engine warns. `MetadataOnConflict` chooses
the runtime behavior when it actually occurs: `WarnAndContinue` (default) logs and proceeds;
`FailJob` treats it as a Job failure and rolls back.

---

## 7. Observability, Logging & Notifications

* **Persistent log:** a rotating log file per the configured `Verbosity`
  (`FailuresOnly` / `FailuresAndSkips` / `All`).
* **Deletion audit trail:** every source disposition and every Mirror target deletion is recorded in
  a durable, append-only audit log (path, action, destination/Trash location, timestamp, Job ID) —
  the safety net for the no-loss goal.
* **In-GUI activity/error view:** recent Jobs with status (success / skip / failure) and drill-down
  into per-Job logs.
* **OS / tray notification on failure:** native notification when a Job fails (`NotifyOnFailure`).
  Skips are log-only unless `Verbosity` is raised.

---

## 8. Dry-Run / Simulation

The GUI offers a dry-run that executes **nothing destructive and writes nothing**. It reports the
full scope of a run: every file that would match (with the deciding filter), every Transformer
command that would execute (fully token-expanded), every Target path that would be written, and
every **deletion and overwrite** that would occur (including Mirror target removals and source
disposition). Its purpose is to let a user see the complete blast radius before any real, possibly
destructive, execution.

---

## 9. Security Model

The primary attack surface is invoking arbitrary executables with arbitrary arguments and expanded
tokens.

* **Default to literal argv** (`ArgumentMode: Literal`) so filenames cannot inject shell commands;
  `Shell` mode is explicit opt-in, visibly marked, and applies platform escaping to substituted
  tokens.
* **Executable validation:** `ExecutablePath` must resolve to an existing file; an optional
  per-install **allowlist** can restrict which executables Profiles may invoke.
* **Least privilege:** the Core Service runs as the logged-in user, not elevated; it opens no network
  listener (IPC is a local pipe/socket with per-user scoping).
* **No credential storage:** network Targets rely on OS-mounted/authenticated shares
  ([§10](#10-network-targets)); the tool stores no share credentials.

---

## 10. Network Targets

Network Targets are addressed purely by path (mapped drive / mount point / UNC). The share must be
mounted and authenticated by the OS; the tool stores and supplies no credentials. If a Target path is
unreachable at execution time, the Job fails for that file and rolls back per
[§3.3](#33-transactional-verification--rollback). (Retry/backoff for transient network errors is a
candidate enhancement — see [Appendix B](#appendix-b-open-items-to-finalize).)

---

## 11. Non-Functional Requirements

* **Watcher scale:** handle high-churn directories without missing events — on Linux, manage
  `inotify` watch limits and degrade to periodic rescan when exceeded; on Windows, size the
  `ReadDirectoryChangesW` buffer and recover from buffer-overflow notifications by rescanning.
* **Throughput:** the bounded pool must saturate available I/O without starving the GUI/IPC.
* **Footprint:** idle service should be lightweight (target: minimal CPU when idle, watchers only).
* **Large files:** verification and copy must stream (no whole-file buffering in memory).

## 12. Acceptance Criteria (high level)

* Each topology (1:1, 1:N, M:1, M:N) and each `SyncMode` produces the documented Target state,
  including Mirror deletions routed to Trash.
* A forced failure at each lifecycle phase leaves the source intact and Targets clean (and, under
  `StageOverwrites`, restores replaced files).
* Killing the service mid-Job and restarting it never results in a deleted source with missing
  Target copies (journal recovery).
* `Literal` argument mode is immune to a crafted filename containing quotes/`$(...)`.
* Dry-run produces a report and makes zero filesystem changes.

---

## Appendix A: Mapping of v1 audit findings → v2 resolutions

| v1 finding | Resolved in |
| --- | --- |
| A1 concurrency contradiction | §5.4 single bounded-pool model |
| A2 overwrite data loss | §6.2 atomic rename + `StageOverwrites`; §3.3 rollback restores |
| A3 per-file vs batch | §3.1 / §4 — unit is one file (Job) |
| A4 transformer in-place vs new-file | §4 Phase 3 `OutputMode` |
| A5 "mirror" undefined | §3.1.1 `Mirror` vs `AdditiveArchive` |
| A6 partial multi-target failure | §3.3 rollback reverts the file across all Targets |
| B7 schema gaps | §5.1 expanded (triggers, filters, versioning, per-step flags) |
| B8 multi-profile storage | §5.1 one file per Profile |
| B9 path handling | §5.1 machine-specific absolute paths (per-OS format → Appendix B) |
| B10 token quoting/injection | §4 Phase 3 `ArgumentMode`; §5.2 delimiter/escaping; §9 |
| B11 settle guarantee | §3.2.1 readiness probe |
| B12 Tier-1 caveat | §3.3 SizeTimestamp tolerance caveat |
| B13 crash recovery | §6.3 durable journal |
| B14 OS integration depth | §5.3 (IExplorerCommand, per-FM Linux, install/autostart) |
| B15 manual invoke profile choice | §3.2 always prompt |
| B16 conflict/verify enums | §3.4 + §5.1 enum authority |
| B17 directory/file semantics | §3.1.2 layout; §6.4 metadata; filters cover attrs/symlinks/depth |
| C18–C26 wording | §1, §3.3, §4 Phase 6, §2 (IPC), §5.1 ($schema dropped from instance) |
| D27–D29 terminology | §0 glossary; §5.1 enums; §5.2 tokens |
| E (missing sections) | §6–§12 added |

## Appendix B: Open items to finalize (detail-level, not blocking)

* Per-OS path-format rules: Windows drive letters vs UNC vs long-path (`\\?\`) support; case
  sensitivity differences; whether `~`/env expansion is allowed in `Path`.
* Network Target transient-error **retry/backoff** policy (currently fail-and-rollback).
* Exact log/journal/audit file locations and rotation sizes.
* Sparse MSIX packaging details and signing for the Windows 11 `IExplorerCommand` handler.
* Whether `ContentHashDedupe` hashes against a maintained Target index or computes on demand.

## Appendix C: Changes from v1

v2 keeps v1's overall Sources→Transformers→Targets shape and the JSON-profile approach, and changes:
the execution unit is now explicitly per-file; one authoritative concurrency model (bounded pool);
`Mirror`/`AdditiveArchive` split; per-step `OutputMode` and `ArgumentMode`; staged-overwrite safety
and a durable journal; expanded filters and schema (`SchemaVersion`, triggers, per-source filters);
consistent policy enums; split filename tokens; deeper OS-integration spec; and new sections for the
data-safety model, security, observability, dry-run, non-functional requirements, and acceptance
criteria. Terminology is standardized on **Profile** / **Job** via the glossary.
