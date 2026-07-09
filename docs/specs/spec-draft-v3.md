# Functional & Technical Specification: File Manager

**Version:** 3 (supersedes `spec-draft-v2.md`)
**Status:** Draft for implementation planning
**Last updated:** 2026-07-09

> This revision incorporates the viability/scope review of v2. It adds seven design areas v2 left
> open (loop prevention, idempotency, multi-Profile overlap, disk preflight, a CLI, global
> pause/resume, network retry), defers several high-cost features out of the first release, and
> introduces an explicit release-scoping section so implementation planning inherits the boundary
> from the spec. Changes from v2 are summarized in
> [Appendix D: Changes from v2](#appendix-d-changes-from-v2).

---

## 0. Glossary

The following terms are authoritative throughout this document.

| Term | Definition |
| --- | --- |
| **Profile** | A single, named, machine-specific automation definition (sources, transformers, targets, policies, filters). The unit a user configures. Serialized as JSON. |
| **Job** | One execution of a Profile against **one file**. A Job owns its own lifecycle, temp workspace, verification, rollback, and journal entry. |
| **Source file / asset** | A single file that has matched a Profile's sources and filters and entered a Job. |
| **Payload** | The path + action information passed into the engine by a trigger (watcher event, schedule tick, or shell invocation). |
| **Source** | A configured input directory belonging to a Profile. |
| **Target** | A configured output directory belonging to a Profile. |
| **Transformer (step)** | One external CLI invocation in a Profile's ordered processing chain. |
| **Engine / Core Service** | The headless background process that watches, schedules, executes Jobs, and owns the durable journal. |
| **Reserved (enum value)** | A configuration value defined by the schema but rejected by the current release with a clear validation error ("reserved for a future release"). Keeps Profiles forward-compatible. |

---

## 1. Product Overview

File Manager is a lightweight, rule-based file synchronization, aggregation,
transformation, and automation tool for Windows and Linux. It runs as a background process that
manages a user-facing tray indicator (where the desktop environment supports one), a configuration
GUI, a command-line interface, and operating-system context-menu integration.

Its purpose is to automate multi-point file-routing and processing workflows — transcoding
audio/video, compiling project assets, distributing build artifacts, aggregating data folders —
while applying verification and staging layers that **strongly guard against accidental file loss**.

> **Wording note (carried from v2):** no copy tool can make "eliminates file loss" an absolute
> guarantee. The design *minimizes and makes recoverable* the data-loss surfaces; the exact
> guarantees and their limits are stated in [§3.3](#33-transactional-verification--rollback) and
> [§6](#6-data-safety-model).

### 1.1 Process model (service vs. tray)

* The **Core Service** is the long-lived engine. On Windows it runs as a per-user background
  process started at logon (not a machine-wide Windows Service — it operates on the logged-in
  user's mounted drives and recycle bin). On Linux it runs as a **systemd user service**
  (`systemctl --user`). **[Linux release]**
* The **Tray Indicator** is an optional UI surface attached to the Core Service when a desktop
  session with a system tray exists. Where no tray is available, the service runs without it and is
  managed via the GUI and the CLI ([§2.2](#22-command-line-interface)). The service never depends on
  the tray being present.
* The engine supports a **global pause/resume** ([§3.2.4](#324-global-pause--resume)) so a user can
  suspend all automation (e.g. during bulk manual file work inside watched folders) without editing
  each Profile's `Active` flag. Pause state is visible and togglable from the tray, the GUI, and the
  CLI.

### 1.2 Release scoping

This spec describes the complete product. **Releases ship it in three tiers**; features tagged
below are specified here so their design is settled, but they are *not* built in v1. A build
encountering a reserved configuration value rejects the Profile with a clear validation error
rather than silently ignoring it.

| Tier | Scope |
| --- | --- |
| **v1 (first public release, Windows-only ship)** | Engine with all four topologies under `AdditiveArchive`; transformer chains (`Literal` arguments only); `SHA256`/`None` verification; staging, rollback, durable journal, crash recovery; watcher + schedule + manual-shell triggers; loop prevention, idempotency, disk preflight; configuration GUI, dry-run, tray, CLI, global pause/resume; Windows registry context menu; network targets with fixed retry. Code remains platform-neutral (no Windows-only constructs in the engine), but only Windows is tested, packaged, and supported. |
| **Linux release (fast-follow)** | systemd user unit, FreeDesktop trash, inotify watch-limit handling, Linux settle probe, file-manager context menus (Nautilus/Dolphin/Nemo/Thunar), Linux packaging and CI. |
| **Post-v1** | `SyncMode: Mirror` (requires the reconcile-pass design, [§3.1.1](#311-sync-mode)); `ArgumentMode: Shell`; `VerificationMethod: SizeTimestamp`; `ContentHashDedupe`; Windows 11 top-level context menu (`IExplorerCommand` via sparse MSIX); configurable retry/backoff policy. |

Tags used throughout: **[Linux release]** and **[Post-v1]**.

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
│ Management GUI / │◄─────────►│       Core Service          │◄────────────────►│  Profile JSON +  │
│       CLI        │   (local  │   (Engine + Tray + Journal) │                  │  Durable Journal │
└──────────────────┘   socket) └──────────────┬──────────────┘                  └──────────────────┘
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
   if the service is not running it is started and the Payload queued. IPC to the running service is
   the canonical path; the CLI ([§2.2](#22-command-line-interface)) doubles as the fallback launcher.
3. **Configuration GUI:** create/edit Profiles, view live engine state and the activity/error view
   ([§7](#7-observability-logging--notifications)), and run **dry-run simulations**
   ([§8](#8-dry-run--simulation)).
4. **CLI:** a thin client over the same IPC protocol for status, manual runs, and pause/resume
   ([§2.2](#22-command-line-interface)).

### 2.1 IPC transport

GUI↔Service, CLI↔Service, and Shell↔Service communication uses a **local-only transport**: a named
pipe on Windows (`\\.\pipe\filemanager-<user>`) and a Unix domain socket on Linux
(`$XDG_RUNTIME_DIR/filemanager.sock`) **[Linux release]**. No network listener is opened. The wire
format is length-prefixed JSON messages.

### 2.2 Command-line interface

A minimal CLI ships with the service. It is a thin IPC client — every command maps to a message on
the [§2.1](#21-ipc-transport) channel, so the CLI, GUI, and shell integration exercise the same
protocol surface. Commands:

| Command | Behavior |
| --- | --- |
| `filemanager status` | Reports service state (running / paused / not running), active Profile count, Jobs in flight, and last error if any. Exit code 0 if the service is reachable, non-zero otherwise. |
| `filemanager list-profiles` | Lists Profiles (ID, name, active flag, trigger summary). |
| `filemanager run <profile> <path>` | Submits a manual Payload for `<path>` against the named Profile (name or ID). Equivalent to the context-menu action with the Profile pre-chosen. Starts the service if it is not running. |
| `filemanager pause` / `filemanager resume` | Toggles the global pause state ([§3.2.4](#324-global-pause--resume)). |

The CLI is deliberately small: it exists for scripting, headless management (relevant for the Linux
release), and as the fallback launcher named in [§2](#2-system-architecture). Richer administration
stays in the GUI.

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

#### 3.1.1 Sync mode

Every Profile declares a **`SyncMode`**:

* **`AdditiveArchive`** — Targets only ever gain or update files. Nothing at a Target is ever removed
  for being absent from a Source. Safe default, and **the only mode implemented in v1**.
* **`Mirror`** **[Post-v1, reserved]** — Targets become an exact replica of the (aggregated) Source
  set: files present at a Target but absent from the Source set are removed to the Recycle Bin /
  Trash (never hard deleted). Destructive at the Target by design.

> **Why Mirror is deferred:** Mirror deletions are not file-arrival-driven, so the per-file Job
> model cannot express them. Implementing Mirror requires a separate **reconcile pass** — a scan
> that enumerates the full Source set, diffs it against each Target, and journals the resulting
> deletions — with its own trigger timing (per schedule tick? after a watcher quiet period?),
> journaling, and dry-run integration. That design is deliberately out of scope here and must be
> specified before Mirror is built (see [Appendix B](#appendix-b-open-items-to-finalize)). Until
> then, `Mirror` is a reserved value that fails Profile validation.

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
  whose Sources/filters match the invoked path; if none match, the prompt is never an empty dead
  end — it always offers a **"Create Profile…"** action that opens the configuration GUI pre-seeded
  with the invoked path. (The CLI `run` command bypasses the prompt because the Profile is named
  explicitly.)
* **Reactive File Watcher:** continuous monitoring of Source roots. A configurable per-Source
  **settle policy** decides when a file is ready ([§3.2.1](#321-file-readiness-settle-policy)).
* **Scheduled / Interval:** cron expression or fixed interval. Each scheduled Profile declares a
  timezone (default: system local). **Missed-run policy** (`CatchUpOnce` default, or `Skip`) governs
  what happens if the service was off at the due time ([§3.2.2](#322-missed-scheduled-runs)).

#### 3.2.1 File readiness (settle policy)

A file is considered ready only when **both** hold: (a) no change events for `SettleDelaySeconds`,
**and** (b) a readiness probe succeeds — the engine opens the file for exclusive read (Windows).
**[Linux release]:** on Linux the probe checks the file is not advisory-locked and its size is
stable across two probes `StabilityIntervalMs` apart. Network sources relax to size-stability only,
since exclusive-open semantics over SMB/NFS are unreliable; this caveat is logged per Job.

#### 3.2.2 Missed scheduled runs

Per Profile: `CatchUpOnce` (default) runs a single evaluation at next service start, coalescing
multiple misses into one; `Skip` ignores missed windows and waits for the next scheduled time.

> `CatchUpOnce` re-evaluations rely on the unchanged-file short-circuit
> ([§3.4.1](#341-idempotency-unchanged-file-short-circuit)) to be harmless for files already
> delivered.

#### 3.2.3 Loop & self-trigger prevention

File automation's classic failure mode is the engine triggering itself. Three defenses, all
mandatory:

1. **Configuration validation.** On Profile save and on service load, the engine checks path
   relationships across **all** active Profiles:
   * A Target equal to, containing, or contained in any watched Source (its own Profile's or
     another's) raises a **validation warning** in the GUI and log. It is a warning, not an error,
     because deliberate Profile chaining (Profile A's Target feeding Profile B's Source) is a
     supported pattern — but the user must opt in with eyes open.
   * The engine builds a directed graph of Source→Target edges across active Profiles and warns on
     **cycles** (A feeds B feeds A), which are runaway loops by construction.
   * A Job whose resolved Target path equals its own source path is a hard **error** (the Job fails
     in Phase 1).
2. **Self-write suppression.** The engine maintains an in-memory registry of every path its Jobs
   are currently writing (Target temp names, final rename destinations, staging moves). Watcher
   events for a registered path are ignored while the owning Job is active and for one settle
   window afterward, so the engine's own writes never echo back as new work. Deliberate chaining
   still works: a *different* Profile watching the Target sees the file once the writing Job
   completes and its suppression window lapses.
3. **Unconditional infrastructure exclusion.** `.pipeline_tmp/` workspaces and per-Job staging
   directories are excluded from watching and from filter matching regardless of Profile
   configuration. They can never be Sources.

#### 3.2.4 Global pause / resume

The engine exposes a single global **paused** state, togglable from the tray menu, the GUI, and the
CLI ([§2.2](#22-command-line-interface)):

* While paused, no new Jobs are started: watcher events are still observed and coalesced (so
  nothing is missed), scheduled ticks are recorded per the missed-run policy, and manual/CLI
  Payloads are queued.
* Jobs already in flight when pause is requested **run to completion** — a Job is never suspended
  mid-lifecycle, because its safety guarantees (staging, rollback, journal) assume it finishes or
  fails as a unit.
* On resume, queued Payloads and pending watcher work are processed normally. The paused state is
  persisted so a service restart does not silently resume automation the user had paused.

### 3.3 Transactional Verification & Rollback

* **Verification (`VerificationMethod`, default ON):** before any source cleanup, each Target copy
  is verified against the **final transformed file in the Job's temp workspace** using one of:
  * `SHA256` (default) — full byte-stream checksum. Authoritative; costs a full read of each copy.
  * `None` — no verification. Permitted for throughput, **but** the GUI raises a blocking warning
    whenever `VerificationMethod = None` **and** `OnSuccess` deletes/permanently removes the source
    (see [§6.1](#61-the-one-data-losing-combination)).
  * `SizeTimestamp` **[Post-v1, reserved]** — size match plus modified-time within a tolerance.
    Deferred: timestamp resolution/preservation varies across FAT/NTFS/ext4/network and copy
    method, making it a caveat-laden tier; it returns only if fast verification over network
    targets proves valuable.
* **Rollback scope:** a Job is atomic with respect to **its single file**. If any Target write,
  Transformer step, or verification fails (after retries, [§10](#10-network-targets)):
  1. The engine aborts the remaining steps for that Job.
  2. It removes that Job's freshly-written / un-renamed temp artifacts from **all** Targets,
     including Targets that had already completed for this file (so no Target is left with a
     half-finished set for the file).
  3. For Targets where an existing file was replaced under `StageOverwrites`
     ([§6.2](#62-overwrite-handling)), the staged prior version is **restored**.
  4. The source file is left **untouched**.
  5. The event is logged and surfaced per [§7](#7-observability-logging--notifications).

> Because the unit is a single file, partial multi-target failure is resolved cleanly: rollback
> reverts that one file across every Target; other files' Jobs are unaffected.

### 3.4 Conflict Resolution

When a file would collide with an existing name at a Target (or across Sources during M:1
aggregation), the Profile's `ConflictResolution` decides:

* `Overwrite` — replace the existing file (subject to overwrite handling, [§6.2](#62-overwrite-handling)).
* `OverwriteIfNewer` — replace only if the incoming file's modified-time is newer than the existing
  Target file's modified-time.
* `RenameSuffix` — write the incoming file with an incrementing suffix (`name (1).ext`).
* `Skip` — leave the existing Target file; skip the incoming file (logged).

For M:1 aggregation collisions, source order in the Profile defines priority when
`Overwrite`/`OverwriteIfNewer` is used.

#### 3.4.1 Idempotency: unchanged-file short-circuit

**Before** `ConflictResolution` is consulted, the engine checks whether the existing Target file is
already identical to the Job's final output:

* Under `VerificationMethod: SHA256` — same size **and** same content hash.
* Under `VerificationMethod: None` — same size **and** same modified-time (best-effort).

If identical, the Target write is **skipped** and logged as `SKIPPED (UnchangedAtTarget)`; the Job
still counts that Target as satisfied (its content is provably in place), so source disposition
proceeds normally if all Targets are satisfied.

> **Why this is mandatory, not an optimization:** without it, any re-delivery of the same file — a
> watcher re-event (metadata touch, editor save-twice), a `CatchUpOnce` re-evaluation, a re-run of
> a manual action — hits `ConflictResolution`. Under `RenameSuffix` that accumulates
> `name (1).ext`, `name (2).ext`, … indefinitely; under `Overwrite` it burns I/O re-copying
> identical bytes. Idempotent re-delivery is what makes the trigger model safe to re-fire.

---

## 4. End-to-End Processing Workflow

Each ready file runs as an independent **Job** through this lifecycle. Aggregation/distribution are
expressed by how many Sources feed and how many Targets receive within each Job — there is no
separate batch state machine.

```
 [Source file ready] ──► 1. Ingestion (Watcher / Schedule / Shell) + Journal: OPEN
                           │       + self-path check + disk-space preflight
                           ▼
                     2. Filter Matching (Include / Exclude / size / age / attrs / depth)
                           │  (fail → Job ends, logged as SKIPPED)
                           ▼
                     3. Transformer chain (sequential; each step new-file OR in-place)
                           │  (non-zero exit / timeout → abort + rollback)
                           ▼
                     4. Target distribution (unchanged-file short-circuit, else
                           │  copy-to-temp-name per Target, bounded parallel)
                           ▼
                     5. Integrity verification (per VerificationMethod) + atomic rename into place
                           │  (fail → rollback, restore staged originals)
                           ▼
                     6. Source disposition (per OnSuccess) + Journal: CLOSED
```

### Phase 1 — Ingestion, preflight & Journal open

On trigger, the engine assigns a unique Job ID, records a **journal entry** (state `OPEN`) capturing
the Profile ID, source path, and chosen policies, locks the source file's metadata snapshot, and
loads the active Profile. Two preflight checks run before any work:

* **Self-path check:** if any resolved Target path equals the source path, the Job fails
  immediately ([§3.2.3](#323-loop--self-trigger-prevention)).
* **Disk-space preflight:** the engine estimates required space — the temp workspace (source size
  as the per-step estimate) and, per Target volume, the file size plus a staging copy when
  `StageOverwrites` applies — and fails the Job **before any write** if a volume's free space is
  below the estimate plus a safety margin (default 64 MiB). The estimate is best-effort
  (Transformers may grow output); a mid-Job out-of-space error is still handled by normal rollback.

### Phase 2 — Filter screening

The file is evaluated against the Profile's filters ([§5.1](#51-profile-schema-json)). Filters
include globs, regex, size bounds, modified-time and created-time age, file attributes, and
subfolder depth. Filters may be defined globally for the Profile and/or per-Source; per-Source rules
override global. **A screened-out file is logged as `SKIPPED`** (not silently dropped) and the Job
ends gracefully.

### Phase 3 — Transformer chain

If the Profile has Transformers, the engine creates an isolated temp workspace
(`<Profile temp root>/.pipeline_tmp/<JobId>/`) and runs each step in order:

1. **Token expansion** — dynamic tokens ([§5.2](#52-tokens)) expand to absolute paths in the
   sandbox. Each token expands to **one** value, substituted as a single, un-split argv element.
2. **Step I/O mode (`OutputMode`)**:
   * `NewFile` — the step writes a distinct output (`$step_output_path`, named with
     `ExpectedOutputExtension`); that output becomes the next step's input.
   * `InPlace` — the step mutates `$step_input_path` directly and produces no new file; the same
     working file (a copy made on entry to the chain, never the original source) carries forward.
3. **Argument handling** — `Arguments` is parsed into a fixed argv list (`ArgumentMode: Literal`);
   tokens are substituted as single, un-split values. No shell is involved, so spaces, quotes, and
   `$(...)` in filenames cannot break out.
   * **Shell features (pipes, redirection, wildcards, env vars):** the supported pattern is a
     user-authored wrapper script (`.cmd`/`.ps1`/`.sh`) invoked as the step's `ExecutablePath`, with
     tokens passed to it as plain arguments. The wrapper owns its own quoting; the engine never
     interpolates tokens into shell text. `ArgumentMode: Shell` is **[Post-v1, reserved]** — and if
     it returns, it will *not* claim engine-side escaping of substituted tokens (reliable escaping
     for `cmd.exe` is not achievable; the v2 promise created false safety).
4. **Process invocation** — the executable runs as a child process with `TimeoutSeconds`. `stdout`
   and `stderr` are captured to the Job log.
5. **Success check** — exit code `0` (or any code in an optional `SuccessExitCodes` list) succeeds;
   the engine then frees the prior step's intermediate file. Any other exit code, or a timeout,
   **aborts the chain and triggers rollback** ([§3.3](#33-transactional-verification--rollback)).

### Phase 4 — Target distribution

The final file state is written to every configured Target. First, the unchanged-file
short-circuit ([§3.4.1](#341-idempotency-unchanged-file-short-circuit)) may satisfy a Target without
writing. Otherwise each Target write goes to a **non-conflicting temporary name** in the Target
directory first (enabling the atomic rename in Phase 5 and bounding the partial-write window).
Writes across Targets run on the bounded worker pool ([§5.4](#54-concurrency--locking)). Name
collisions are resolved per [§3.4](#34-conflict-resolution). Metadata is preserved best-effort per
[§6.4](#64-metadata-preservation). Transient I/O errors are retried per [§10](#10-network-targets)
before counting as failure.

### Phase 5 — Verification & atomic placement

Per `VerificationMethod`, each Target's temp copy is verified against the Job's final temp output.
On success the temp copy is **atomically renamed** to its final name; under `StageOverwrites`, any
existing Target file is moved to staging immediately before the rename. On any failure (after
retries), rollback runs ([§3.3](#33-transactional-verification--rollback)).

### Phase 6 — Source disposition

After all Targets are verified and placed (or satisfied unchanged), the Job applies `OnSuccess`
([§5.1](#51-profile-schema-json)): `KeepSource` (pure copy/sync), `MoveToTrash`, `MoveToArchive`
(into a configured folder), or `PermanentDelete` (explicit opt-in). The journal entry is set to
`CLOSED`.

> Deletion of a file to Recycle Bin/Trash is not atomic in the transactional sense; since the unit
> is a single file, the disposition of that one file simply succeeds or is logged as a disposition
> error (the copies at Targets are already safe at this point).

---

## 5. Technical & Operational Specifications

### 5.1 Profile schema (JSON)

Profiles are JSON files **validated in code** by the engine's Profile validator on load and save
(structural checks, enum values, path rules, and the cross-Profile checks of
[§3.2.3](#323-loop--self-trigger-prevention)). Validation is implemented as engine code, not a
JSON-Schema library — the engine is AOT-compiled and reflection-free, and validator code can express
cross-field and cross-Profile rules a document schema cannot. **Storage:** one Profile per file
under the config directory (`profiles/*.json`); the service loads all of them. A top-level
`SchemaVersion` enables future migration. Paths are **machine/OS-specific** and absolute (Profiles
are not portable across OSes).

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

> **Enum authority.** Values marked *(reserved)* are schema-legal but rejected by v1 validation
> with a "reserved for a future release" error ([§1.2](#12-release-scoping)):
> `OnSuccess ∈ {KeepSource, MoveToTrash, MoveToArchive, PermanentDelete}`;
> `VerificationMethod ∈ {SHA256, None, SizeTimestamp (reserved)}`;
> `ConflictResolution ∈ {Overwrite, OverwriteIfNewer, RenameSuffix, Skip}`;
> `OverwriteHandling ∈ {DirectOverwrite, StageOverwrites}`;
> `SyncMode ∈ {AdditiveArchive, Mirror (reserved)}`;
> `TargetLayout ∈ {PreserveStructure, Flatten}`;
> `ArgumentMode ∈ {Literal, Shell (reserved)}`;
> `MetadataOnConflict ∈ {WarnAndContinue, FailJob}`;
> `OnFailure ∈ {AbortRestoreAndClean}` (single value today; the enum exists as an extension point —
> the §3.3 rollback behavior is what `AbortRestoreAndClean` denotes);
> `Verbosity ∈ {FailuresOnly, FailuresAndSkips, All}` (see [§7](#7-observability-logging--notifications)).
> `Filters.ContentHashDedupe` is likewise **reserved** (must be `false` in v1); it requires a
> Target-index design ([Appendix B](#appendix-b-open-items-to-finalize)).

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

### 5.3 Operating-System Integration

#### Windows

* **Context menu (v1):** registered per-user under `HKCU\Software\Classes\...\shell` (no admin
  required) for `Directory`, `Directory\Background`, and `AllFilesystemObjects`. On Windows 11 the
  entry appears under **"Show more options"** (the classic menu); on Windows 10 it is top-level.
* **Top-level Windows 11 entry [Post-v1]:** an `IExplorerCommand` handler packaged via a sparse
  MSIX package would surface the action in the Windows 11 primary context menu. Deferred: sparse
  packages must be code-signed (certificate acquisition/cost) and COM interop under AOT is
  high-effort; the registry verb above is fully functional without it.
* **Soft deletion:** uses `IFileOperation` so `MoveToTrash` populates the native Recycle Bin.

#### Linux **[Linux release]**

* **Context menu:** the installer registers actions for the file managers it detects —
  Nautilus (extension/scripts), Dolphin (ServiceMenu `.desktop`), and Nemo/Thunar custom actions.
  Each maps the menu action to a Payload sent over the IPC socket. Coverage is explicit per file
  manager rather than a single assumed `.desktop` path.
* **Soft deletion:** conforms to the FreeDesktop Trash Specification (`~/.local/share/Trash/`).

#### Installation & autostart

Windows: per-user install; when `ServiceStartupMode` is `RunOnStartup` the Core Service is registered
as a logon startup task (per-user HKCU Run key — no elevation).
Linux **[Linux release]**: a `systemd --user` unit (`filemanager.service`) enabled for the user.
The service runs without a tray if none is available.

##### Startup mode & reconciliation

`ServiceStartupMode` is a global setting (edited in the UI) with three values:

* **`StartAndStopWithProgram`** — default (and the fail-safe zero value). The UI starts the service on
  open and gracefully stops it on close. No autostart entry.
* **`RunOnStartup`** — the service is registered under the per-user HKCU Run key
  (`Software\Microsoft\Windows\CurrentVersion\Run`, value `FileManager.Service`) so it auto-starts at
  Windows login; the UI just connects and leaves it running on close.
* **`StartOnProgramOpen`** — the UI starts the service on open (if not already running) and leaves it
  running after close. No autostart entry.

Only `RunOnStartup` implies an autostart entry.

**Effective Windows startup state.** A startup entry is *effectively registered* only when the HKCU Run
value `FileManager.Service` **exists** *and* is **not disabled** under
`...\CurrentVersion\Explorer\StartupApproved\Run`. Users turn a startup item off two ways — deleting the
Run value, or toggling it off in Task Manager's *Startup* tab (or *Settings ▸ Apps ▸ Startup*), which
leaves the value in place but records a disabled flag in `StartupApproved\Run`. **Both count as "not
registered."**

**Writing the registry (on explicit settings change only).** Changing the setting to `RunOnStartup`
registers the Run entry; changing it to any other mode unregisters it. This is the *only* path that
writes the autostart registry.

**Reconciliation at UI startup (the setting follows the OS).** When the UI starts — and *only* then —
it reconciles the persisted `ServiceStartupMode` against the effective Windows startup state and adjusts
**the setting** to match what the user has done through Windows. It never re-asserts the registry from
the setting:

* If the mode is `RunOnStartup` but the entry is **not** effectively registered (deleted or disabled),
  the mode reverts to `StartAndStopWithProgram`. The entry is **not** re-added — the user's removal
  through Windows wins.
* If the entry **is** effectively registered but the mode is not `RunOnStartup`, the mode is updated to
  `RunOnStartup`.
* Otherwise the setting is left unchanged.

The reconciled setting is persisted. Because reconciliation runs at UI startup only — never at service
startup — the app never overrides a startup choice the user made directly in Windows between launches.
This deliberately supersedes any behavior where the service re-asserts the Run entry from the setting on
every service start, which would fight the user's manual changes.

### 5.4 Concurrency & Locking

* **Execution model:** a single **bounded worker pool** (configurable `MaxWorkers`, default = CPU
  count) runs Jobs concurrently regardless of which Profile they belong to. Within one Job, writes
  to multiple Targets may also use the pool.
* **Same-file collision rule:** the engine maintains a lock keyed by absolute path. A Job acquires
  locks on its source file and each Target temp/final path before acting; a second Job that would
  touch a locked path waits (FIFO) rather than racing. This prevents two Jobs from corrupting a
  shared Target file.
* **Multi-Profile source overlap:** one file may match the Sources and filters of **multiple**
  Profiles; each match produces its own Job, and those Jobs serialize on the shared source-path
  lock. **Ordering between Profiles is deliberately undefined** (first to acquire the lock runs
  first). The hazard is disposition: if one overlapping Profile's `OnSuccess` disposes the source
  (`MoveToTrash` / `MoveToArchive` / `PermanentDelete`), a later Job may find its source gone — it
  then ends as `SKIPPED (SourceDisposed)`, never as an error with side effects. Because the outcome
  depends on scheduling order, **Profile validation warns** whenever two or more active Profiles
  have overlapping Sources and at least one of them disposes sources.
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
  area; it is **restored on rollback** and discarded on success. Maximum safety, extra disk + I/O
  (accounted for by the disk-space preflight, [§4 Phase 1](#phase-1--ingestion-preflight--journal-open)).

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
* **Deletion audit trail:** every source disposition is recorded in a durable, append-only audit
  log (path, action, destination/Trash location, timestamp, Job ID) — the safety net for the
  no-loss goal. (When `Mirror` ships post-v1, its Target deletions join this trail.)
* **In-GUI activity/error view:** recent Jobs with status (success / skip / failure) and drill-down
  into per-Job logs. The engine's global state (running / **paused**, [§3.2.4](#324-global-pause--resume))
  is always visible in the GUI and tray.
* **OS / tray notification on failure:** native notification when a Job fails (`NotifyOnFailure`).
  Skips are log-only unless `Verbosity` is raised.

---

## 8. Dry-Run / Simulation

The GUI offers a dry-run that executes **nothing destructive and writes nothing**. It reports the
full scope of a run: every file that would match (with the deciding filter), every file the
unchanged-file short-circuit would skip, every Transformer command that would execute (fully
token-expanded), every Target path that would be written, and every **deletion and overwrite** that
would occur (including source disposition). Its purpose is to let a user see the complete blast
radius before any real, possibly destructive, execution.

---

## 9. Security Model

The primary attack surface is invoking arbitrary executables with arbitrary arguments and expanded
tokens.

* **Literal argv only (v1):** `Arguments` is always parsed into a fixed argv list and tokens are
  substituted as single, un-split values — filenames containing spaces, quotes, or `$(...)` cannot
  inject shell commands because no shell is ever involved. Users needing shell features write a
  wrapper script and own its quoting ([§4 Phase 3](#phase-3--transformer-chain)). If a `Shell` mode
  ships post-v1, it will be explicit opt-in and visibly marked — and will **not** claim engine-side
  escaping of tokens, because reliable escaping for `cmd.exe` is not achievable and the claim
  itself is a hazard.
* **Executable validation:** `ExecutablePath` must resolve to an existing file; an optional
  per-install **allowlist** can restrict which executables Profiles may invoke.
* **Least privilege:** the Core Service runs as the logged-in user, not elevated; it opens no network
  listener (IPC is a local pipe/socket with per-user scoping).
* **No credential storage:** network Targets rely on OS-mounted/authenticated shares
  ([§10](#10-network-targets)); the tool stores no share credentials.

---

## 10. Network Targets

Network Targets are addressed purely by path (mapped drive / mount point / UNC). The share must be
mounted and authenticated by the OS; the tool stores and supplies no credentials.

**Transient-error retry:** a Target write or verification that fails with a transient I/O error
(share hiccup, brief disconnect) is retried up to **3 times with a 2-second delay** between
attempts. Retries are logged per Job. Only after the final attempt fails does the Job fail and roll
back per [§3.3](#33-transactional-verification--rollback). If a Target path is unreachable outright
at execution time, the same retry-then-rollback applies. The retry policy is deliberately fixed and
minimal in v1; a configurable backoff policy is deferred
([Appendix B](#appendix-b-open-items-to-finalize)).

---

## 11. Non-Functional Requirements

* **Watcher scale:** handle high-churn directories without missing events — on Windows, size the
  `ReadDirectoryChangesW` buffer and recover from buffer-overflow notifications by rescanning.
  **[Linux release]:** manage `inotify` watch limits and degrade to periodic rescan when exceeded.
* **Throughput:** the bounded pool must saturate available I/O without starving the GUI/IPC.
* **Footprint:** idle service should be lightweight (target: minimal CPU when idle, watchers only).
* **Large files:** verification and copy must stream (no whole-file buffering in memory).

## 12. Acceptance Criteria (high level)

* Each topology (1:1, 1:N, M:1, M:N) under `AdditiveArchive` produces the documented Target state.
* A forced failure at each lifecycle phase leaves the source intact and Targets clean (and, under
  `StageOverwrites`, restores replaced files).
* Killing the service mid-Job and restarting it never results in a deleted source with missing
  Target copies (journal recovery).
* A crafted filename containing quotes/`$(...)`/spaces passes through a Transformer chain as a
  literal argument with no shell interpretation.
* **Loop prevention:** with a Target configured inside a watched Source, the engine's own writes
  never trigger a new Job (self-write suppression), and Profile validation surfaced the overlap
  warning at save time.
* **Idempotency:** re-delivering an unchanged file to a Profile with `RenameSuffix` produces zero
  new Target files (logged `SKIPPED (UnchangedAtTarget)`), across watcher re-events and
  `CatchUpOnce` re-evaluations.
* **Overlap safety:** two active Profiles watching the same Source, one of which disposes the
  source, never produce an error with side effects — the later Job ends `SKIPPED (SourceDisposed)`
  — and validation warned about the configuration.
* **Retry:** a Target that fails transiently (fault injection) succeeds within the retry budget
  with no rollback; a persistently failing Target rolls back after 3 attempts.
* Reserved configuration values (`Mirror`, `Shell`, `SizeTimestamp`, `ContentHashDedupe: true`)
  fail Profile validation with a clear "reserved for a future release" error.
* Dry-run produces a report and makes zero filesystem changes.
* Pausing the engine stops new Jobs while in-flight Jobs complete; queued work runs on resume; the
  paused state survives a service restart.

---

## Appendix A: v1 audit mapping (historical)

v2 resolved the v1 audit findings; that mapping is preserved in `spec-draft-v2.md` (Appendix A) and
is not repeated here. All v2 resolutions carry forward into v3 unchanged except where Appendix D
notes otherwise.

## Appendix B: Open items to finalize (detail-level, not blocking)

* Per-OS path-format rules: Windows drive letters vs UNC vs long-path (`\\?\`) support; case
  sensitivity differences; whether `~`/env expansion is allowed in `Path`.
* Exact log/journal/audit file locations and rotation sizes.
* Configurable retry/backoff policy for network Targets (v1 ships the fixed 3×2s policy of §10).
* **Deferred-feature designs (required before each ships):**
  * `Mirror` reconcile pass — trigger timing, full-source enumeration, diffing, journaling, and
    dry-run integration (§3.1.1).
  * `ContentHashDedupe` — whether it hashes against a maintained Target index or computes on
    demand.
  * Sparse MSIX packaging and code signing for the Windows 11 `IExplorerCommand` handler (§5.3).
  * `ArgumentMode: Shell` — scope and safety posture, given that engine-side escaping is explicitly
    not promised (§9).

## Appendix C: Changes from v1

Preserved in `spec-draft-v2.md` (Appendix C); v3 does not alter that history.

## Appendix D: Changes from v2

**Additions (design gaps closed):**

1. **Loop & self-trigger prevention** (§3.2.3): cross-Profile Source/Target overlap and cycle
   validation warnings, self-write suppression in the watcher, unconditional exclusion of
   `.pipeline_tmp/` and staging directories, and a hard error for Target == source.
2. **Idempotency / unchanged-file short-circuit** (§3.4.1, §4 Phase 4): identical files already at
   a Target are skipped (`SKIPPED (UnchangedAtTarget)`) before `ConflictResolution` runs, making
   re-delivery safe (fixes the `RenameSuffix` duplicate-pileup hazard).
3. **Multi-Profile source overlap** (§5.4): defined serialization via the path lock, defined
   `SKIPPED (SourceDisposed)` outcome, and a validation warning for overlapping-with-disposal
   configurations.
4. **Disk-space preflight** (§4 Phase 1): free-space check (temp workspace + per-Target size +
   staging + margin) fails the Job before any write.
5. **Command-line interface** (§2.2): `status`, `list-profiles`, `run`, `pause`, `resume` over the
   existing IPC protocol — v2 referenced a CLI in §1.1/§2 without specifying one.
6. **Global pause/resume** (§3.2.4, §1.1, §7): engine-wide pause with in-flight Jobs completing,
   queued work on resume, and persisted pause state.
7. **Network transient-error retry** (§10): fixed 3 attempts × 2 s delay before rollback, promoted
   from v2's Appendix B.
8. **Release scoping** (§1.2): explicit v1 / Linux-release / post-v1 boundary; Windows-only v1
   ship with a platform-neutral engine.

**Deferrals (specified but reserved past v1):**

1. `SyncMode: Mirror` (§3.1.1) — additionally documented *why*: Mirror deletions are not
   file-arrival-driven, so they require a reconcile-pass design the per-file Job model cannot
   express; that design is an Appendix B prerequisite.
2. `ArgumentMode: Shell` (§4 Phase 3, §9) — v1 is `Literal`-only with the wrapper-script pattern;
   v2's "engine applies platform escaping" claim is **withdrawn** as unachievable for `cmd.exe`.
3. `VerificationMethod: SizeTimestamp` (§3.3) — the caveat-laden Tier 1 is reserved; v1 ships
   `SHA256` (default) and `None`.
4. `ContentHashDedupe` (§5.1) — reserved pending the Target-index design.
5. Windows 11 top-level context menu via `IExplorerCommand` + sparse MSIX (§5.3) — requires code
   signing and high-effort COM/AOT interop; the per-user registry verb is the v1 mechanism.
6. All Linux integration (§1.1, §2.1, §3.2.1, §5.3, §11) — tagged **[Linux release]** as a
   fast-follow; the engine stays platform-neutral so this is packaging/integration work.

**Consistency fixes:**

* §5.1: "schema-validated JSON" reworded — validation is engine code (AOT/reflection-free), not a
  JSON-Schema library; the enum-authority table now marks reserved values, and reserved values fail
  validation explicitly rather than being silently ignored.
* §12 acceptance criteria rewritten to match v1 scope (Mirror/Shell items removed) and extended
  with loop-prevention, idempotency, overlap, retry, reserved-value, and pause criteria.
* §7 audit-trail and §8 dry-run wording updated for the Mirror deferral; dry-run now also reports
  unchanged-file skips.
* Phase-2 filter list no longer mentions content-hash dedupe (reserved).
