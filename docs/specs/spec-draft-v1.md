# Functional & Technical Specification: Cross-Platform File Pipeline Utility

---

## 1. Product Overview

The File Pipeline Utility is a lightweight, rule-based file synchronization, aggregation, transformation, and automation engine designed for Windows and Linux. It operates as a background service that sits in the system task tray, offering users a graphical user interface (GUI) for configuration and previewing, alongside operating system context-menu integration.

The core purpose of this tool is to automate complex, multi-point file-routing and processing workflows (e.g., transcoding audio/video, compiling project assets, distributing build artifacts, or aggregating data folders) while introducing strict verification layers to eliminate accidental file loss.

---

## 2. System Architecture

The utility uses a completely decoupled architecture, separating ingestion (Sources), processing (Transformers), and distribution (Targets). This allows the application to remain agnostic to specific operating system file managers while handling complex, non-linear topologies over an asynchronous, thread-safe execution queue.

```
                   ┌─────────────────────────────┐
                   │     OS File Explorer        │
                   │  (Windows / Linux Context)  │
                   └──────────────┬──────────────┘
                                  │ Passes payload (Path + Action)
                                  ▼
┌──────────────────┐    IPC    ┌─────────────────────────────┐  Reads / Writes  ┌──────────────────┐
│  Management GUI  │◄─────────►│     Background Service      │◄────────────────►│  JSON Templates  │
└──────────────────┘           │    (Engine & Task Tray)     │                  └──────────────────┘
                               └──────────────┬──────────────┘
                                              │ Executes Lifecycle Pipeline
                                              ▼
                        ┌──────────────────────────────────────────┐
                        │ Sources ──► Transformers ──► Targets     │
                        └──────────────────────────────────────────┘

```

1. **The Core Engine (Headless Background Service):** Manages file system watching across arbitrary roots, coordinates concurrent file transfer streams, sequences external process execution loops, and handles error rollback strategies.
2. **The Shell Integration Layer:** Platform-specific registry scripts or desktop actions that pass directory or file payloads to the execution engine via command-line flags or IPC channels.
3. **The Configuration GUI:** An administrative dashboard used to mutate configuration schemas, visualize active background engine states, and run functional simulations.

---

## 3. Core Functional Requirements

### 3.1 Advanced Topology Configurations

The execution engine must route file state representations natively across four distinct directory topologies:

* **One-to-One (1:1):** Classic mirroring or moving from a single source to a single destination.
* **One-to-Many (1:$N$ - Distribution):** A single absolute source directory distributes its payload simultaneously to $N$ independent target directories.
* **Many-to-One ($M$:1 - Aggregation):** Flattens and combines the contents of $M$ independent source directories into a single target directory.
* **Many-to-Many ($M$:$N$ - Consolidated Mirroring):** Aggregates the contents of $M$ independent source directories and mirrors that unified, consolidated state across $N$ target directories.

### 3.2 Automated Execution Triggers

* **Manual Shell Invocation:** Triggered explicitly via the operating system's right-click context menu on a selected file or folder.
* **Reactive File Watcher:** Continuous background monitoring of configured source directories. It must implement a configurable settling delay (debouncing) per source to ensure file writes are closed before ingestion begins.
* **Scheduled / Interval:** Automated evaluation loops based on crontab styles or elapsed intervals.

### 3.3 Transactional Verification & Rollback

* **Integrity Auditing:** Before executing any destructive source cleanups, the engine must cross-examine files post-transfer using size/timestamp matching (Tier 1) or cryptographic byte-stream checksums like SHA-256 (Tier 2).
* **Rollback Protocol:** If any target copy, pipeline transformer, or hash verification fails mid-execution, the pipeline aborts, logs the event, and deletes orphaned or partially copied files from the targets, keeping the original source files untouched.

---

## 4. End-to-End Processing Workflow

Every ingested asset progresses through a strict, linear lifecycle state machine. The file is intercepted at ingestion, sandboxed during transformation, and replicated across distribution endpoints.

```
 [Source Folder] ──► 1. Ingestion (Watcher / Context)
                           │
                           ▼
                     2. Filter Matching (Include/Exclude Rules)
                           │
                           ▼
                     3. Transformer Layer (Sequential execution chain)
                           ├── Step 1: FFMPEG Transcode ──► (Outputs tmp_1.mp3)
                           ├── Step 2: Metadata Tagger  ──► (Outputs tmp_2.mp3)
                           └── Step 3: Script Renamer   ──► (Outputs final.mp3)
                           │
                           ▼
                     4. Target Distribution (Parallel copy to Targets [A, B...])
                           │
                           ▼
                     5. Integrity Verification (SHA-256 Checksum validation)
                           │
                           ▼
                     6. Post-Success Trigger (Atomic Source Deletion)

```

### Phase 1: Ingestion & Lifecycle Initialization

When a trigger fires, the engine locks the file metadata, generates a unique job tracking ID, and queries the active JSON template configuration.

### Phase 2: Filter Screening

The payload is evaluated against global and path-specific inclusion/exclusion rules (e.g., file extensions, regex matching, or file size maximums). Files that fail screening are quietly skipped, and the pipeline terminates gracefully.

### Phase 3: Chained Transformer Processing

If the profile contains an array of `Transformers`, the engine creates an isolated, local temporary workspace (`.pipeline_tmp/`) and begins executing external command-line interfaces (CLIs) sequentially:

1. **Contextual Token Expansion:** The engine expands dynamic string variables (like `$step_input_path` and `$step_output_path`) into absolute path strings mapping to the active sandboxed environment.
2. **Process Invocation:** The engine launches the external executable as a child process tree with forced timeout constraints. Standard Output (`stdout`) and Standard Error (`stderr`) are piped to the main application logs.
3. **Exit-Code Verification:** The engine intercepts the process return value. If the exit code matches `0`, the stage succeeds. The engine purges transient intermediate assets from the previous step and treats the new output file as the input payload for the next step.
4. **Short-Circuit Protection:** If any process returns a non-zero exit code or hits a timeout wall, the chain fractures, the engine aborts further steps, and the Rollback Protocol triggers.

### Phase 4: Target Distribution

Once the final transformer step finishes (or immediately after ingestion if no transformers are assigned), the final file state is broadcasted to all configured `TargetDirectories` in parallel threads. Filename collisions at the targets are resolved dynamically via the active profile's conflict resolution policy (e.g., *Overwrite If Newer*, *Rename Suffix*, or *Source Priority Override*).

### Phase 5: Transactional Integrity Auditing

The engine halts until all target writes are complete. It then runs cryptographic or structural hash verifications across the distribution layer.

### Phase 6: Post-Success Cleanup

Upon comprehensive validation across all targets, the pipeline executes the profile's deletion policy (e.g., `DeleteSourceToRecycleBin` or FreeDesktop Trash integration). The original source files are removed safely, clearing the ingestion folder.

---

## 5. Technical & Operational Specifications

### 5.1 Declarative Configuration Schema (JSON)

All automation profiles are structured using serializeable, schema-validated JSON files to ensure complete language and runtime agnosticism.

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "ProfileId": "e2a3c4b5-1234-4a5b-8c9d-0123456789ab",
  "Name": "Chained Audio Optimization Pipeline",
  "Active": true,
  "Sources": [
    {
      "Path": "/absolute/path/to/dropzone/raw",
      "MonitorChanges": true,
      "SettleDelaySeconds": 2
    }
  ],
  "Transformers": [
    {
      "Step": 1,
      "Name": "FFMPEG Audio Transcoder",
      "ExecutablePath": "/usr/bin/ffmpeg",
      "Arguments": "-i \"$step_input_path\" -b:a 320k \"$step_output_path\"",
      "ExpectedOutputExtension": ".mp3",
      "TimeoutSeconds": 120
    },
    {
      "Step": 2,
      "Name": "ID3 Metadata Tagger CLI",
      "ExecutablePath": "/usr/bin/mid3v2",
      "Arguments": "--artist=\"Production\" \"$step_input_path\"",
      "ExpectedOutputExtension": ".mp3",
      "TimeoutSeconds": 30
    }
  ],
  "Targets": [
    { "Path": "/absolute/path/to/local/archive" },
    { "Path": "/absolute/path/to/network/vault" }
  ],
  "Policies": {
    "ConflictResolution": "RenameSuffix",
    "VerificationMethod": "SHA256",
    "OnSuccess": "MoveToRecycleBin",
    "OnFailure": "AbortAndCleanTargets"
  },
  "Filters": {
    "Include": ["*.wav", "*.flac"],
    "Exclude": [".DS_Store", "Thumbs.db"]
  }
}

```

### 5.2 Environmental Variable Tokens

Transformers use dynamic system tokens that adjust their string value relative to the running step in the chain:

* `$source_root_path`: The initial directory path where the execution request started.
* `$step_input_path`: The absolute path of the file entering the *active* transformer block.
* `$step_output_path`: The absolute path target where the active utility must write its result inside the temporary cache.
* `$filename_current`: The base name of the file entering the current step, excluding path wrappers.

### 5.3 Operating System Integration Specifications

#### Windows Target

* **Shell Context Integration:** Registered via command verbs inside the Windows Registry under `HKEY_CLASSES_ROOT\Directory\shell` and `HKEY_CLASSES_ROOT\AllFilesystemObjects\shell`.
* **Soft Deletion:** Maps directly to the Win32 `IFileOperation` system shell wrapper to ensure files deleted via post-success triggers populate the native Windows Recycle Bin.

#### Linux Target

* **Shell Context Integration:** Registered via `.desktop` file specifications placed inside the local file manager action paths (e.g., `~/.local/share/file-manager/actions/` or Nautilus scripts paths).
* **Soft Deletion:** Conforms to the FreeDesktop Trash Specification, routing dropped payloads directly into `~/.local/share/Trash/`.

### 5.4 Concurrency & Locking

* **Sequential Job Isolation:** The engine queue forces a strict sequential loop for overlapping files caught on the same profile watcher to mitigate resource starvation and multi-thread collisions over shared hardware sectors.
* **Exclusive Access Assertion:** Prior to execution, the engine must assert read/write mutex locks on target files to block intermediate file modifications during cryptographic checksum calculations.