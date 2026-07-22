# Sync test environments

PowerShell scripts that scaffold small, self-contained folder+file environments
plus a valid schema-v2 `Profile` JSON for exercising each File-Manager sync
feature by hand. Each script builds **one category** into its own subfolder.

Re-running any script **wipes and rebuilds only its own category folder** back
to the initial state. The scripts never touch anything above this directory —
every delete is guarded to stay strictly below the scripts folder.

## Prerequisites

- Windows PowerShell 5.1 or PowerShell 7+.
- If your execution policy blocks unsigned scripts, run them without changing
  machine policy:

  ```powershell
  powershell -ExecutionPolicy Bypass -File .\New-AdditiveArchiveEnv.ps1
  ```

  (or `Unblock-File .\*.ps1` once, or `Set-ExecutionPolicy -Scope Process Bypass`).

## Quick start

From this folder:

```powershell
# Build one environment:
.\New-AdditiveArchiveEnv.ps1

# ...or build/reset every environment at once:
.\Reset-AllEnvironments.ps1
```

Run a script again any time to reset that environment (it deletes its category
folder and rebuilds it). Nothing outside this folder is ever created or removed.

## What each script produces

| Script | Folder | Exercises | Profiles |
|---|---|---|---|
| `New-AdditiveArchiveEnv.ps1` | `additive-archive\` | AdditiveArchive happy path; nested source, empty target → all `New`; PreserveStructure | 1 |
| `New-MirrorEnv.ps1` | `mirror\` | Mirror sweep; target has matching files + **orphans** → dry-run previews `Deleted` | 1 |
| `New-ConflictResolutionEnv.ps1` | `conflict-resolution\` | Same-relative-path collisions with newer/older timestamps | 4 (Overwrite, OverwriteIfNewer, RenameSuffix, Skip) |
| `New-FilterMultiSourceDispositionEnv.ps1` | `filters-multisource-disposition\` | Include/exclude globs, min-size, max-depth; 2 sources → 1 target (Flatten); source disposition | 2 (MoveToArchive, MoveToTrash) |
| `New-DuplicateContentEnv.ps1` | `duplicate-content\` | Differently-named files with **identical content hashes** (in-folder, cross-folder, cross-side) | 2 (XXH3-128, SHA256) |

## Layout of a built environment

Each category folder is a **self-contained `EnginePaths.Root`** — the profile
plus the source/target trees it points at all live together:

```
<category>\
  profiles\
    <guid>.json      one or more Profiles (Sources/Targets are absolute
                     paths into the folders below)
  sources\           (or sourceA\, sourceB\ for the multi-source category)
  targets\
  archive\           (filters-multisource-disposition only)
```

Profiles are named `<guid>.json` to match the engine's on-disk convention;
`ProfileStore.LoadAll` reads every `*.json` in `profiles\`, which is why the
conflict and filter categories can ship several profiles side by side.

## Running a sync against an environment

There is no CLI that takes a config path. The engine loads profiles from
`<EnginePaths.Root>\profiles\` and runs them over IPC. Two ways to drive one:

- **Point the engine's root at a category folder.** Inject
  `new EnginePaths { Root = "...\\<category>" }` (as the test suites do) or copy a
  category's `profiles\*.json` into your real root
  (`%LOCALAPPDATA%\FileManager\profiles\`). The absolute source/target paths in
  the JSON keep working from anywhere.
- **Trigger a `DryRun`.** `DryRun` is the working end-to-end path today;
  `RunProfile` still returns `NOT_IMPLEMENTED`, so use dry-run to preview.

Expected dry-run classifications per environment:

- **additive-archive** — every source file `New`; nested folders recreated under
  the target (PreserveStructure).
- **mirror** — matching files unchanged; `stale-orphan.txt` and
  `old\deep-orphan.txt` previewed as `Deleted` (a real Mirror run would recycle
  them; deletion is not executed in v1).
- **conflict-resolution** — depends on the profile: `Overwrite` overwrites all;
  `OverwriteIfNewer` overwrites `newer.txt` but keeps `older.txt`; `RenameSuffix`
  writes suffixed copies; `Skip` leaves the target untouched.
- **filters-multisource-disposition** — only `*.txt` files ≥ 10 bytes within
  `MaxDepth` are `Processed`; `scratch.tmp`, `notes.md`, `tiny.txt`, and
  `deep\deeper\toodeep.txt` are `SkippedByFilter`; both sources flatten into the
  one target.
- **duplicate-content** — identical-content files are handled independently
  (no dedupe in v1): `clone.txt` copies as `New`, the differently-named
  `existing\original.txt` is `Untouched` (listed because `ScanDestination=true`).

### A note on content-hash dedupe

`ContentHashDedupe` is a **reserved** flag — it must stay `false` (setting it
`true` fails validation), so `duplicate-content` does not dedupe today. The file
layout is exactly the fixture the dedupe feature will need once it lands: verify
with `Get-FileHash` that the differently-named files share a hash.

## Cleanup

Re-run a script to reset its environment, or delete a category folder outright
(e.g. `Remove-Item .\mirror -Recurse`). Everything stays inside this directory.
