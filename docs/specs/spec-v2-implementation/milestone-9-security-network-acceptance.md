# Milestone 9 — Security, network targets, non-functional & acceptance

## Goal

Harden, finish, and prove the system. Lock down the executable-invocation attack surface, finalize
network-target behavior, validate the non-functional requirements, and build the end-to-end
acceptance test suite that demonstrates every §12 criterion. This milestone also produces per-OS
release packaging.

## Spec references

- §9 — security model: default literal argv (done M2); executable validation + optional per-install
  allowlist; least privilege; no network listener; per-user IPC scoping; `Shell`-mode escaping audit.
- §10 — network targets: path-only addressing (mapped drive / mount / UNC); fail-and-rollback on
  unreachable Target; no stored credentials. (Retry/backoff is an Appendix B candidate.)
- §11 — non-functional: throughput (saturate I/O without starving GUI/IPC), idle footprint,
  large-file streaming (no whole-file buffering). Watcher scale already in M5.
- §12 — acceptance criteria (all bullets).
- Appendix B — finalize remaining open items (path-format rules, retry/backoff decision, MSIX signing
  in release, dedupe index strategy).

## Scope

**In scope**
- Executable validation: `ExecutablePath` must resolve to an existing file; optional per-install
  allowlist restricting which executables Profiles may invoke; reject/flag otherwise.
- `Shell`-mode escaping audit + tests confirming injection resistance; confirm `Literal` immunity end
  to end (§12).
- Least-privilege confirmation: service runs unelevated; IPC pipe/socket per-user ACL verified; no
  network listener (assert).
- Network targets: path-only; on unreachable Target at execution, the Job fails for that file and
  rolls back (M3). Decide and document retry/backoff (implement a minimal bounded retry or explicitly
  defer, per Appendix B).
- Non-functional validation: throughput benchmark (pool saturates I/O, IPC/GUI stay responsive);
  idle-footprint measurement; assert streaming copy/verify on a multi-GB file (no large memory
  growth).
- Acceptance suite (`tests/FileManager.Integration.Tests`) covering every §12 bullet.
- Per-OS packaging/release: Windows (AOT publish + sparse MSIX from M8, signed) and Linux (AOT publish
  + systemd user unit from M6); release CI job.
- Finalize Appendix B open items and update the spec/docs cross-references.

**Out of scope (owning milestone)**
- New engine features — M9 hardens, validates, and packages what M0–M8 built.

## Tasks

- [ ] `ExecutableValidator` (existence + optional allowlist read from `ServiceConfig`, M0); integrate
      into the transformer runner (M2) pre-launch; surface violations as validation/Job errors.
- [ ] Security review of `Shell` escaping across Windows/Linux; add adversarial filename tests.
- [ ] Assert no network listener (integration test scanning for open ports during a run); verify
      pipe/socket per-user permissions.
- [ ] Network-target handling: unreachable-at-execution → fail + rollback path; implement/justify the
      retry/backoff decision (Appendix B) and document it.
- [ ] Benchmarks: throughput under the worker pool with IPC/GUI responsiveness check; idle CPU
      footprint; large-file streaming memory ceiling.
- [ ] Acceptance integration tests, one per §12 bullet:
      - each topology (1:1/1:N/M:1/M:N) × each `SyncMode` produces the documented Target state,
        including Mirror deletions routed to Trash;
      - a forced failure at each lifecycle phase leaves the source intact and Targets clean (and,
        under `StageOverwrites`, restores replaced files);
      - killing the service mid-Job and restarting never yields a deleted source with missing copies;
      - `Literal` mode is immune to a crafted filename with quotes / `$(...)`;
      - dry-run produces a report and makes zero filesystem changes.
- [ ] Release packaging: AOT publish profiles per OS; bundle MSIX (signed) + systemd unit; release CI.
- [ ] Update Appendix B items to "resolved" with the chosen policies; refresh docs.

## Proposed structure

```
src/FileManager.Core/Security/
  ExecutableValidator.cs, Allowlist.cs
src/FileManager.Core/Network/
  NetworkTargetGuard.cs (reachability + optional retry/backoff)
tests/FileManager.Integration.Tests/
  TopologyMatrixTests.cs, PhaseFailureRollbackTests.cs, CrashRecoveryTests.cs,
  LiteralInjectionTests.cs, DryRunZeroWriteTests.cs, NoNetworkListenerTests.cs
benchmarks/FileManager.Benchmarks/
  ThroughputBenchmark.cs, LargeFileStreamingBenchmark.cs
build/release/
  publish-windows.ps1, publish-linux.sh, release.yml (CI)
```

## Acceptance criteria

All §12 criteria pass as automated tests:

- Each topology and `SyncMode` produces the documented Target state (incl. Mirror→Trash).
- A forced failure at every lifecycle phase leaves the source intact and Targets clean; `StageOverwrites`
  restores replaced files.
- Service kill mid-Job + restart never produces a deleted source with missing Target copies.
- `Literal` argument mode is immune to a crafted filename containing quotes / `$(...)`.
- Dry-run produces a report and makes zero filesystem changes.

Plus: executable allowlist blocks a disallowed executable; no network listener is opened; an
unreachable network Target fails-and-rolls-back; large-file copy/verify stays within the streaming
memory ceiling; signed release artifacts are produced for Windows and Linux.

## Dependencies

M0–M8 (hardens, validates, and packages the complete system).

## Risks / open items

- MSIX signing requires a certificate; secure it in the release pipeline.
- Retry/backoff for transient network errors is a genuine product decision (Appendix B) — implement a
  conservative bounded retry or explicitly defer; document either way.
- Throughput vs GUI/IPC responsiveness is a tuning target; the benchmark gates regressions rather
  than asserting a fixed absolute number.
