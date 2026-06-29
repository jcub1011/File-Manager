# Milestone 2 — Transformer pipeline

## Goal

Insert the ordered **Transformer chain** (§4 Phase 3) between filter screening and Target
distribution: run each configured external CLI step in an isolated per-Job temp workspace, carry the
working file forward through `NewFile`/`InPlace` steps, expand step tokens, and pass arguments either
as a safe literal argv list or via a shell. A step failure aborts the chain (full rollback semantics
land in M3; M2 wires the abort hook and cleans the temp workspace).

## Spec references

- §4 Phase 3 — token expansion, `OutputMode`, `ArgumentMode`, process invocation, success check.
- §5.2 — step tokens (`$step_input_path`, `$step_output_path`, `$source_root_path`, filename tokens),
  `$name` delimiter, `$$` escape, case-sensitivity.
- §9 — default-literal-argv security posture; `Shell` mode as marked opt-in with escaping.

## Scope

**In scope**
- Per-Job temp workspace `<Profile temp root>/.pipeline_tmp/<JobId>/`; a working copy of the source
  is made on entry (the original source is never mutated).
- Sequential step execution honoring `Step` order.
- `OutputMode`: `NewFile` (writes `$step_output_path` named with `ExpectedOutputExtension`, becomes
  next input) and `InPlace` (mutates `$step_input_path`, same file carries forward).
- `ArgumentMode`: `Literal` (parse `Arguments` into a fixed argv; tokens substituted as single
  un-split values; no shell) and `Shell` (passed to `cmd.exe` / `/bin/sh`; platform escaping applied
  to substituted tokens).
- Token expansion in step context (each token → exactly one value).
- Child-process invocation with `TimeoutSeconds`; `stdout`/`stderr` captured to the Job log.
- Success check: exit code `0` or a member of `SuccessExitCodes`; otherwise abort.
- Intermediate-file cleanup after each successful step; abort hook + temp-workspace teardown.

**Out of scope (owning milestone)**
- Full transactional rollback that reverts already-written Targets / restores staged originals → M3
  (M2 exposes the abort signal and ensures the temp workspace is removed).
- Executable allowlist / path validation → M9 (M2 does a basic "executable exists" check).

## Tasks

- [ ] `TempWorkspace` allocator/cleaner under the Profile temp root; per-Job subdir keyed by Job ID.
- [ ] Working-copy creation on chain entry (streamed copy); chain operates only on the copy.
- [ ] `TransformerRunner` driving the ordered steps and threading the current working file.
- [ ] `TokenExpander` extension for step tokens; `$$` escape; case-sensitive names; one value per
      token. Reuse the filename-token logic from M1.
- [ ] `ArgumentParser` for `Literal` mode: tokenize `Arguments` into argv, substitute tokens as
      single elements (never re-split on spaces/quotes); never invoke a shell.
- [ ] `Shell` mode: build the shell command, apply platform-specific escaping to substituted token
      values, mark as higher-risk in logs.
- [ ] `ProcessInvoker`: launch child process (argv or shell), enforce `TimeoutSeconds` (kill on
      timeout incl. child tree), capture `stdout`/`stderr` with size caps to the Job log.
- [ ] `NewFile` vs `InPlace` output handling + `ExpectedOutputExtension` resolution; free the prior
      step's intermediate on success.
- [ ] Abort path: on non-success exit or timeout, signal Job abort, capture diagnostics, tear down
      the temp workspace (Target-level revert deferred to M3).
- [ ] Basic executable existence check before launch (full validation/allowlist in M9).
- [ ] Tests: 2-step chain (NewFile→InPlace) like the §5.1 sample; timeout abort; non-zero exit abort;
      `Literal`-mode immunity to a filename containing quotes / `$(...)`; extension-changing step
      keeps tokens correct.

## Proposed structure

```
src/FileManager.Core/Transformers/
  ITransformerRunner.cs + TransformerRunner.cs, TempWorkspace.cs,
  IProcessRunner.cs + SystemProcessRunner.cs,
  ArgumentParser.cs (Literal), ShellCommandBuilder.cs (Shell), StepResult.cs
src/FileManager.Core/Tokens/
  TokenExpander.cs (extended with step tokens)
src/FileManager.Core/Jobs/
  JobEngine.cs (Phase-3 integration + abort hook)
```

Per the [engine service & dependency pattern](README.md#engine-service--dependency-pattern),
`TransformerRunner` is an `ITransformerRunner` injected into `JobEngine`, and process launching sits
behind `IProcessRunner` (real `SystemProcessRunner`; tests inject a `FakeProcessRunner`). The
argument/shell builders (`ArgumentParser`, `ShellCommandBuilder`) and the token expander stay
`static` — they are pure functions.

## Acceptance criteria

- The §5.1 sample chain (ffmpeg `NewFile` → mid3v2 `InPlace`) runs end to end on a stub executable,
  producing the expected working file fed into Target distribution.
- A step exiting non-zero or exceeding `TimeoutSeconds` aborts the chain, leaves the source untouched,
  and removes the temp workspace.
- A filename containing `"; rm -rf $(pwd)"`-style content cannot break out under `Literal` mode
  (argv stays a single argument) — direct precursor to the §12 injection-immunity criterion.
- `stdout`/`stderr` of each step are present in the Job log.

## Dependencies

M1 (Job lifecycle, token base, file IO).

## Risks / open items

- Child-process tree kill on timeout differs Windows vs Linux — use a job object / process group so
  grandchildren are also terminated.
- `Shell` mode escaping is inherently best-effort; it stays opt-in and visibly marked, with the
  hardening review folded into M9 §9.
