using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using System;

namespace FileManager.Core.DryRun;

/// <summary>Shared resolution of the dry-run worker count from a profile's concurrency override and
/// the global setting, so the batched engine (<see cref="DryRunEngine"/>) and the streaming handler
/// agree on how a Manual pin — or Inherit deferring to the global — maps to a worker count for the
/// destination sweep.</summary>
internal static class DryRunConcurrency
{
    /// <summary>The pinned sweep worker count, or null when the effective mode is Automatic — in which
    /// case the projector auto-scales the degree of parallelism to the target medium (local vs.
    /// network). The Manual + no-count edge falls back to <see cref="AutoWorkers"/>, matching the
    /// evaluation phase.</summary>
    public static int? ResolveManualWorkers(Profile profile, GlobalSettings global)
    {
        ConcurrencyOverride c = profile.Concurrency;
        return c.Mode switch
        {
            ConcurrencyMode.Manual => Math.Max(1, c.ManualWorkers ?? AutoWorkers()),
            ConcurrencyMode.Automatic => null,
            _ => global.DryRunConcurrencyMode == ConcurrencyMode.Manual   // Inherit
                ? Math.Max(1, global.DryRunManualWorkers ?? AutoWorkers())
                : null,
        };
    }

    /// <summary>Evaluation-phase default worker count: reserve one core and keep an 8-worker ceiling —
    /// per-file evaluation is I/O-bound (stat + existence probe + up to two SHA-256 hashes), so beyond
    /// ~8 concurrent hashers the disk, not the CPU, is the bottleneck. Floor of 1 covers single-core
    /// machines.</summary>
    public static int AutoWorkers() => Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));
}
