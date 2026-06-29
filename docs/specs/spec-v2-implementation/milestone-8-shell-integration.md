# Milestone 8 — OS shell integration

## Goal

Let users trigger Profiles straight from their file manager's right-click menu on both platforms,
handing the selected path to the running service over IPC. Implement the manual shell trigger with
the spec's always-prompt profile chooser, and ship the platform registrations: a modern Windows 11
top-level context entry (with a Windows 10 / classic fallback) and per-file-manager Linux actions.

## Spec references

- §3.2 — Manual Shell Invocation: right-click on file/folder; recursive descent on folders subject to
  `MaxDepth`; **always prompt** the user to pick which Profile to run (even when one matches).
- §5.3 Windows — context menu under `HKCU\...\shell` for `Directory`, `Directory\Background`,
  `AllFilesystemObjects`; top-level entry via `IExplorerCommand` packaged as a sparse MSIX; legacy
  registry verb fallback for Windows 10 / classic menu.
- §5.3 Linux — registrations for detected file managers: Nautilus (extension/scripts), Dolphin
  (ServiceMenu `.desktop`), Nemo/Thunar custom actions; each maps to a Payload over the IPC socket.
- Appendix B — sparse MSIX packaging + signing details for the `IExplorerCommand` handler.

## Scope

**In scope**
- Manual-invocation flow: shell entry → Payload (path + action) → service (start-if-not-running from
  M6) → **profile chooser prompt** → Job(s). Folder invocation descends recursively honoring
  `MaxDepth`.
- Profile chooser UI (reuse a GUI dialog from M7, or a lightweight standalone chooser) surfaced when
  a manual Payload arrives; never auto-runs without a choice. Lists matching Profiles plus an
  always-available "Create Profile…" action so the no-match case is never an empty dead end.
- Windows: `IExplorerCommand` COM handler in a sparse MSIX package for a top-level Win11 entry;
  HKCU registry verbs as the Win10/classic fallback for `Directory`, `Directory\Background`,
  `AllFilesystemObjects`.
- Linux: detect installed file managers and register the appropriate action type for each
  (Nautilus / Dolphin / Nemo / Thunar), each invoking the shell entry that sends the IPC Payload.
- Installer/registration + unregistration helpers (per-user, no admin where possible).

**Out of scope (owning milestone)**
- The IPC Payload-submit path and start-if-not-running launcher → already in M6 (consumed here).
- Soft-delete (`IFileOperation`/FreeDesktop Trash) → already in M3 (reused, not re-implemented).

## Tasks

- [ ] Define the manual-invocation Payload (path, action, recursive flag) and route it through M6's
      `SubmitPayload`; service-side, mark manual Payloads as "needs profile choice".
- [ ] Profile chooser: when a manual Payload arrives, prompt (GUI dialog) listing Profiles whose
      sources/filters match the path; require an explicit pick (no silent auto-run, §3.2). The dialog
      **always** includes a **"Create Profile…"** action (even when the matching list is empty —
      e.g. a path outside every configured Source), which opens the M7 Profile editor pre-seeded with
      the invoked path, so the prompt is never a dead end.
- [ ] Folder recursion honoring `MaxDepth`, enumerating eligible files into per-file Jobs.
- [ ] Windows `IExplorerCommand` handler (COM, AOT-compatible) returning the verb; sparse MSIX
      packaging manifest; signing notes (Appendix B).
- [ ] Windows registry fallback: HKCU verbs for the three node types; install/uninstall scripts.
- [ ] Linux: file-manager detection; Nautilus extension/script, Dolphin ServiceMenu `.desktop`,
      Nemo/Thunar custom action files; each shells out to the launcher with the selected path(s).
- [ ] Registration/unregistration tooling integrated with the autostart installer (M6).
- [ ] Tests/manual checks: right-click on a file and a folder on each platform produces an IPC
      Payload and the profile prompt; recursion respects `MaxDepth`; uninstall removes all entries.

## Proposed structure

```
src/FileManager.Shell/Windows/
  ExplorerCommandHandler.cs (IExplorerCommand COM), RegistryVerbs.cs,
  msix/AppxManifest.xml (sparse package)
src/FileManager.Shell/Linux/
  FileManagerDetector.cs, NautilusAction.cs, DolphinServiceMenu.cs, NemoThunarActions.cs,
  templates/*.desktop, *.nemo_action
src/FileManager.Shell/
  ShellPayload.cs, RegistrationInstaller.cs
src/FileManager.Service/  (chooser routing)
  ManualInvocationRouter.cs
src/FileManager.Gui/Views/
  ProfileChooserDialog.axaml(.cs)
```

## Acceptance criteria

- Right-clicking a file or folder on Windows 11 shows a top-level entry (via `IExplorerCommand`); on
  Windows 10 / classic menu the registry-verb fallback appears.
- Right-clicking in the detected Linux file managers shows the action and sends the path to the
  service.
- Manual invocation always prompts for a Profile (even when exactly one matches) and never runs
  without an explicit choice; when no Profile matches the path, the prompt still appears with a
  working "Create Profile…" action; folder invocation descends recursively within `MaxDepth`.
- Uninstall/unregister removes every shell entry on both platforms.

## Dependencies

M6 (IPC Payload submission + launcher), M7 (chooser dialog UI; GUI shell).

## Risks / open items

- Sparse MSIX packaging and code-signing (Appendix B) is the highest-uncertainty piece — may require
  a developer/publisher certificate; plan a signing step in CI/release (M9).
- `IExplorerCommand` must be AOT/trim-safe COM (no reflection); validate against analyzers.
- Linux file-manager coverage is explicit per FM (not a single assumed `.desktop`); unsupported FMs
  fall back to the CLI launcher.
