# Milestone 7 — Configuration GUI, dry-run & observability

## Goal

Give users a desktop application to create/edit Profiles, watch the engine work, and preview a run's
full blast radius before executing it. Repurpose the existing Avalonia shell (App bootstrap, MVVM
base, `IFileSystemService` for path picking) and replace the file-browser views with a Profile
editor, an activity/error view backed by the Service over IPC, and a dry-run view backed by a
side-effect-free simulation pass.

## Spec references

- §2 — Configuration GUI component (create/edit Profiles, live state, dry-run).
- §6.1 — blocking warning for `None`+`PermanentDelete`; non-blocking for `None`+`MoveToTrash`.
- §7 — in-GUI activity/error view (recent Jobs, status, per-Job drill-down); OS/tray failure
  notifications (`NotifyOnFailure`); skips log-only unless verbosity raised.
- §8 — dry-run / simulation: report every matching file (+ deciding filter), every fully
  token-expanded Transformer command, every Target path, every deletion/overwrite; **zero**
  filesystem changes.

## Scope

**In scope**
- Repurpose `FileManager` Avalonia app into `FileManager.Gui`: keep App/DI/MVVM bootstrap and
  `IFileSystemService` (now a path picker); drop the browser `MainWindow` views.
- Profile editor: full §5.1 schema with validation feedback; the §6.1 safety warnings (blocking vs
  non-blocking) driven by `SafetyAnalyzer` (M3); `Mirror` clearly flagged as destructive (§3.1.1).
- IPC client (`FileManager.Contracts`) to the Service: live engine state, activity/error view with
  per-Job drill-down into logs (§7), Profile list/reload.
- Dry-run **engine**: a side-effect-free simulation reusing M1 filter screening (with recorded
  deciding filter), M2 token expansion (command preview), M3 Mirror/overwrite/disposition planning —
  writing nothing, executing nothing. Exposed via the `DryRunRequest`/`DryRunReport` IPC messages.
- Dry-run **view**: renders the report (matches, commands, Target writes, deletions/overwrites).
- Failure notifications via OS/tray (`NotifyOnFailure`).

**Out of scope (owning milestone)**
- Shell context-menu registration + manual-invoke profile prompt → M8 (the prompt may reuse a GUI
  dialog, but the registration is M8).

## Tasks

- [ ] Rename/repurpose the GUI project; remove browser ViewModels/Views; keep App, `ViewModelBase`,
      `IFileSystemService`/`FileSystemService` (path picker), `FileSystemEntry`.
- [ ] `IpcClient` in the GUI: connect to pipe/socket, request/response + event subscription;
      reconnect handling when the service restarts.
- [ ] Profile editor view + view-model: bind the full schema; inline validation using
      `ProfileValidator` (M0); save to `profiles/*.json` and trigger a service reload. Support
      opening **pre-seeded with a path** (a new Source/Target seeded from an invoked path) so M8's
      manual "Create Profile…" action can hand off into it.
- [ ] Safety warnings UI: blocking modal for `None`+`PermanentDelete` (cannot save until resolved or
      explicitly acknowledged per spec), non-blocking banner for `None`+`MoveToTrash`, destructive
      `Mirror` flag.
- [ ] Activity/error view: live list of recent Jobs (success/skip/failure) + drill-down to per-Job
      log; subscribe to `JobEvent` stream.
- [ ] `DryRunEngine` in `FileManager.Core` (`Simulation/`): produce a `DryRunReport` reusing the real
      filter/token/planning code paths in a no-write mode; never invokes Transformers or touches the
      filesystem destructively.
- [ ] Wire `DryRunRequest`/`DryRunReport` in the Service (M6 messages) to `DryRunEngine`.
- [ ] Dry-run view rendering the report sections.
- [ ] Notification service: OS notifications on Job failure; respect `Verbosity` for skips.
- [ ] Tests: dry-run makes zero filesystem changes (assert no writes/deletes on a sandbox);
      command-preview tokens match what M2 would run; the §6.1 blocking warning appears for the bad
      combo; activity view reflects pushed `JobEvent`s.

## Proposed structure

```
src/FileManager.Gui/                        (repurposed from src/FileManager)
  App.axaml(.cs), ViewModels/ViewModelBase.cs
  Ipc/IpcClient.cs
  Views/ProfileEditorView.axaml(.cs), ActivityView.axaml(.cs), DryRunView.axaml(.cs), MainWindow...
  ViewModels/ProfileEditorViewModel.cs, ActivityViewModel.cs, DryRunViewModel.cs, MainWindowViewModel.cs
  Services/PathPickerService.cs (wraps IFileSystemService), NotificationService.cs
src/FileManager.Core/Simulation/
  DryRunEngine.cs, DryRunReport.cs
```

## Acceptance criteria

- A user can create, edit, validate, and save a Profile; the running service picks it up on reload.
- Setting `VerificationMethod=None` with `OnSuccess=PermanentDelete` raises a blocking warning;
  `MoveToTrash` raises a non-blocking one; `Mirror` is visibly flagged destructive.
- The activity view shows success/skip/failure for recent Jobs with per-Job log drill-down, updating
  live from the event stream.
- A dry-run produces the full report (matches + deciding filters, expanded commands, Target writes,
  deletions/overwrites) and makes **zero** filesystem changes (direct §12 criterion).
- A failed Job raises an OS/tray notification when `NotifyOnFailure` is set.

## Dependencies

M6 (IPC server + message shapes + running engine to talk to).

## Risks / open items

- The dry-run engine must share the *exact* matching/expansion code with the live engine to avoid
  drift between preview and reality — factor those as pure functions reused by both.
- Avalonia 12 + AOT: keep compiled bindings, avoid reflection in view-models (the scaffold already
  uses CommunityToolkit source generators).
