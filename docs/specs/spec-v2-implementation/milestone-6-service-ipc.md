# Milestone 6 — Core Service host, IPC, autostart & tray

## Goal

Package the engine into the long-lived **Core Service** the spec describes: a headless per-user
background process that owns the watchers, scheduler, worker pool, and journal, and exposes a
local-only IPC endpoint for the GUI and shell integration. Add per-OS autostart and an optional tray
indicator that the service never depends on.

## Spec references

- §1.1 — process model: per-user logon process (Windows) / `systemd --user` (Linux); tray optional;
  service never depends on the tray.
- §2 — three-component architecture; Shell→Service handoff incl. start-if-not-running + Payload queue;
  IPC is the canonical path, CLI flag only as fallback launcher.
- §2.1 — IPC transport: named pipe (`\\.\pipe\filemanager-<user>`) / Unix socket
  (`$XDG_RUNTIME_DIR/filemanager.sock`); length-prefixed JSON; no network listener.
- §5.3 — installation & autostart (Windows logon task; Linux `filemanager.service` user unit).

## Scope

**In scope**
- `FileManager.Service` host process wiring engine + watchers + scheduler + worker pool + journal
  recovery (M4) on startup.
- IPC server over named pipe (Windows) / Unix domain socket (Linux), per-user scoped, no network
  listener; length-prefixed JSON framing using `FileManager.Contracts` DTOs (from M0).
- Request/response + event-push protocol: enqueue Payload, query engine state, stream activity/Job
  events, get/reload Profiles. (Message *shapes* defined in Contracts; consumed by M7/M8.)
- Shell→Service handoff: if the service is not running, the shell entry starts it and the Payload is
  queued (the CLI fallback launcher lives in `FileManager.Shell`, invoked here).
- Autostart registration: Windows per-user logon startup task; Linux `systemd --user` unit
  `filemanager.service` enabled for the user.
- Optional tray indicator attached when a tray exists; service runs headless without it.
- Graceful shutdown: drain the worker pool, flush journal/audit.

**Out of scope (owning milestone)**
- The GUI client → M7. The shell registrations themselves (context-menu entries) → M8 (M6 provides
  the Payload-submit IPC path they call).

## Tasks

- [ ] Define IPC message DTOs in `FileManager.Contracts`: `SubmitPayload`, `EngineStateQuery`/
      `EngineState`, `JobEvent`, `ListProfiles`/`ReloadProfiles`, `DryRunRequest`/`DryRunReport`
      (shape only; engine in M7). Source-gen JSON, length-prefix framing helper.
- [ ] `IpcServer`: `NamedPipeServerStream` (Windows) / `UnixDomainSocketServer` (Linux) with per-user
      ACL/permissions; accept loop; per-connection read/dispatch; event broadcast to subscribers.
- [ ] `ServiceHost` (`IHostedService` / generic host): load `ServiceConfig` (M0; `MaxWorkers`,
      allowlist, log/journal/audit locations) → start journal recovery → start watchers + scheduler +
      pool → start IPC server; orchestrate graceful shutdown.
- [ ] `PayloadQueue` bridging IPC submissions and shell handoffs into the Job queue.
- [ ] Single-instance guard (one service per user) keyed off the pipe/socket name.
- [ ] CLI fallback launcher entrypoint (start service if not running, then submit Payload) in
      `FileManager.Shell` (thin), invoked over IPC once the service is up.
- [ ] Autostart installers: Windows logon task registration (per-user, no admin); Linux systemd user
      unit file + enable helper.
- [ ] Optional tray: detect tray availability; show status + open-GUI + pause/resume; absence is a
      no-op (service unaffected).
- [ ] Tests: IPC round-trip (submit → state reflects queued Job); no TCP/network listener is opened
      (port scan asserts none); start-if-not-running handoff; clean shutdown drains the pool.

## Proposed structure

```
src/FileManager.Contracts/Messages/
  SubmitPayload.cs, EngineState.cs, JobEvent.cs, ProfileMessages.cs, DryRunMessages.cs
  Framing.cs (length-prefixed JSON), ContractsJsonContext.cs
src/FileManager.Service/
  Program.cs, ServiceHost.cs, PayloadQueue.cs, SingleInstanceGuard.cs
src/FileManager.Service/Ipc/
  IpcServer.cs, NamedPipeTransport.cs, UnixSocketTransport.cs, ConnectionDispatcher.cs
src/FileManager.Service/Autostart/
  WindowsLogonTask.cs, LinuxSystemdUserUnit.cs, filemanager.service (template)
src/FileManager.Service/Tray/
  TrayIndicator.cs (optional), TrayAvailability.cs
src/FileManager.Shell/
  FallbackLauncher.cs
```

## Acceptance criteria

- The service starts at logon (Windows task / Linux systemd user unit), runs headless without a tray,
  and recovers any `OPEN` Jobs on startup (M4).
- A client can connect over the pipe/socket, submit a Payload, and observe the resulting Job in the
  engine state and event stream; no network port is opened.
- Invoking the shell fallback launcher when the service is down starts it and the Payload is
  processed once it is up.
- Stopping the service drains in-flight Jobs and flushes the journal/audit.

## Dependencies

M0 (`FileManager.Contracts`), M5 (watchers, scheduler, pool, locking) — all the long-lived loops the
host wires together.

## Risks / open items

- Unix socket and named-pipe ACLs must restrict to the current user (least privilege, §9); verify
  permissions on creation.
- Tray support varies (GNOME without an extension has none) — the detection path must degrade
  silently per §1.1.
