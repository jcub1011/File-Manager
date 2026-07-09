# Decision: Keep the shipping executables on NativeAOT (not JIT)

**Status:** Accepted
**Last updated:** 2026-07-09
**Applies to:** `FileManager.Service`, `FileManager.UI`
**Related:** [`../specs/architecture-v1.md` §1 — AOT constraints](../specs/architecture-v1.md#1-layering--dependency-rules)

---

## Context

Both shipping executables publish with NativeAOT (`PublishAot=true`). Two questions were raised:

1. Could the **UI** run under normal JIT while the **Service** stays AOT?
2. Would moving the **Service** to JIT be worth the tradeoffs — the concern being that JIT's
   tiered compilation / dynamic PGO might speed up the dry-run engine, weighed against extra memory
   on a persistent background process?

Mixing modes is **technically trivial**: `PublishAot` is a per-project publish setting, each exe is
published independently and carries its own runtime, and the two communicate only as separate
processes over the named-pipe IPC (length-prefixed JSON). The only shared compile-time code is
`FileManager.Contracts`, which is already AOT-safe (source-gen JSON, no reflection); a JIT consumer
of an AOT-safe library has no problem, since AOT-safety is a superset of JIT's rules. Note also that
`PublishAot` is inert outside `dotnet publish` — the everyday `dotnet build`/`dotnet run`/F5 inner
loop already runs under JIT, so AOT is not a factor in day-to-day build speed.

So the question was never "is it possible" but "is it worth it." That reduces to end-user runtime
characteristics, which we measured.

## Decision

**Keep both executables on NativeAOT.** Do not move the UI or the Service to JIT.

## Rationale

### UI (analysis)

For the UI process there is no end-user performance upside to JIT and a small downside:

- AOT gives faster cold start (no JIT warm-up) and lower memory — both user-visible on a desktop app.
- JIT's one real edge — better steady-state throughput via dynamic PGO — does not apply, because the
  UI does almost no compute. All heavy work (file operations, dry-run engine) runs in the Service.
- Staying AOT also keeps the UI a self-contained native file with no .NET runtime prerequisite,
  consistent with the Service.

The only concrete JIT benefit would have been faster *publish* builds — a developer convenience,
invisible to users, and irrelevant to the inner dev loop (already JIT).

### Service (measured)

We published the Service both ways and drove **identical** dry-runs (24,059 files, SHA-256
verification) through each over the real named-pipe IPC — 7 timed iterations after a warm-up — while
sampling the live service process's memory (with an isolated data store).

| Metric | AOT (current) | JIT self-contained | JIT delta |
|---|---|---|---|
| Idle working set | 19.1 MB | 41.0 MB | **+21.9 MB (+115%)** |
| Idle private commit | 7.5 MB | 11.2 MB | +3.7 MB (+49%) |
| Peak working set (under load) | 250 MB | 318 MB | +68 MB (+27%) |
| Dry-run median | 713.5 ms | 711.3 ms | −2.2 ms (−0.3%) |
| Dry-run mean | 739.2 ms | 744.9 ms | +5.7 ms (+0.8%) |
| Dry-run min | 704.0 ms | 701.5 ms | −2.5 ms |
| On-disk size | ~9 MB (native exe) | ~81 MB (bundled runtime) | +72 MB |

**No measurable throughput benefit.** Dry-run times are statistically identical — the median differs
by 0.3%, inside run-to-run noise, and the mean marginally favors AOT. This matches the workload:
enumerating tens of thousands of files is I/O-bound, so JIT's codegen advantage has nothing to bite on.

**A real, permanent memory cost.** JIT more than doubles the idle working set (+22 MB) — and idle is
where a persistent background service spends nearly all its time — plus ~4 MB private commit at rest,
~68 MB at peak, and ~72 MB on disk.

For the Service the trade is strictly negative: 2× idle memory on a background process to gain nothing
measurable.

## Reproducing the measurement

- Publish AOT: `dotnet publish src/FileManager.Service -c Release -r win-x64` (native link needs the
  MSVC/C++ Desktop toolchain on `PATH`; on this machine the ILC linker discovery via `vswhere` only
  resolved after initializing the environment with `vcvars64.bat`).
- Publish JIT: same, plus `-p:PublishAot=false -p:PublishTrimmed=false --self-contained true`.
- Isolate the service's data root by pointing `%LOCALAPPDATA%` at a throwaway dir (`EnginePaths.Default()`
  reads it) and give each run a unique `FILEMANAGER_PIPE_NAME`; run the two builds sequentially (the
  single-instance mutex `Local\FileManager.Service.<user>` is per-user, independent of the pipe name).
- Drive the workload with a small console harness that references `FileManager.Contracts`, connects via
  `ServiceLauncher.ConnectOrStartAsync`, saves a minimal `Profile` (`SchemaVersion = 2`, Sources → the
  test tree) with `SaveProfileRequest { AcknowledgeWarnings = true }`, then times
  `RequestAsync<DryRunResponse>(new DryRunRequest { ProfileId })`.

## Caveats

- Single dev machine; numbers are indicative, not a rigorous multi-run study. The relative delta is the
  point, not the absolute milliseconds.
- The dry-run ran against an empty target, so the SHA-256 comparison path was not heavily exercised.
  This does not change the decision: SHA-256 uses the same hardware intrinsics under both modes, and the
  JIT-sensitive work (enumeration/planning) was measured directly.

## When to revisit

Re-measure **only** if the Service gains a genuinely CPU-bound, long-running operation (not I/O-bound
file work) whose hot path could benefit from dynamic PGO. Even then, scope the re-measurement to that
specific path, and weigh any gain against the fixed idle-memory cost above.
