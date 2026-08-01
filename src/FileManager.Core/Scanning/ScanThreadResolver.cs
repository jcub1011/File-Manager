using System;
using FileManager.Contracts.Settings;

namespace FileManager.Core.Scanning;

/// <summary>The single place that turns <see cref="ScanThreadingSettings"/> into concrete worker
/// counts. Replaces the removed per-profile <c>DryRunConcurrency</c>: scan (enumeration) concurrency
/// is now global-only, and the per-drive cap follows the precedence specific-drive → drive-type →
/// per-drive default. Kept pure/stateless so it is trivially unit-testable without I/O.</summary>
internal static class ScanThreadResolver
{
    /// <summary>Absolute ceiling on the <em>auto</em> scan-thread formula, so the derived value cannot
    /// spawn a pathological worker count on a many-core box. An explicit user pin is honored as-is
    /// (floored at 1).
    ///
    /// <para>The cost this bounds is NOT thread stacks, despite what this comment used to say. The
    /// shipped FileManager.Service.exe reserves 1.5 MB of stack per thread but COMMITS 4 KB
    /// (<c>SizeOfStackReserve</c> = 1,572,864, <c>SizeOfStackCommit</c> = 4,096); Windows reserves
    /// address space and commits lazily, and the walk is iterative (<c>ScanWorkState</c> queues, not
    /// recursion), so realistic private commit is tens of KB per thread. Reading "~1 MB of stack each"
    /// sends you optimizing the wrong thing.</para>
    ///
    /// <para>What actually scales with this number is directory materialization:
    /// <c>ScanScheduler</c> holds an entire directory level per in-flight worker, and a parked
    /// directory keeps that queue alive. At ~320 B per entry a 1,000-entry directory is ~320 KB, so
    /// the in-flight plus parked set is the real memory term.</para></summary>
    private const int MaxAutoScanThreads = 256;

    /// <summary>Global ceiling on concurrently-enumerating scan workers. Auto = ProcessorCount * 8
    /// (enumeration is I/O-bound; heavy oversubscription overlaps blocking directory reads), bounded by
    /// <see cref="MaxAutoScanThreads"/> so the auto value cannot spawn a pathological thread count.</summary>
    public static int ResolveMaxScanThreads(ScanThreadingSettings s) =>
        s.MaxScanThreads.Resolve(Math.Min(Environment.ProcessorCount * 8, MaxAutoScanThreads));

    /// <summary>Evaluation/hashing worker count. Auto = ProcessorCount - 1 (reserve a core; the phase
    /// is stat + existence probe + hashing). <see cref="ThreadBudget.Resolve"/> floors at 1.</summary>
    public static int ResolveMaxHashThreads(ScanThreadingSettings s) =>
        s.MaxHashThreads.Resolve(Environment.ProcessorCount - 1);

    /// <summary>The per-volume scan-thread cap for a volume, by precedence specific-drive override →
    /// drive-type override → <see cref="ScanThreadingSettings.PerDriveDefault"/> (each resolving its
    /// own "auto" to ProcessorCount * 4). Clamped to the global scan ceiling so a single drive can
    /// never demand more workers than exist.</summary>
    public static int ResolvePerDriveCap(ScanThreadingSettings s, string volumeKey, DriveClass driveClass)
    {
        ThreadBudget? specific = s.SpecificDriveOverrides.TryGetValue(NormalizeKey(volumeKey), out ThreadBudget sv) ? sv : null;
        ThreadBudget? byType = s.DriveTypeOverrides.TryGetValue(driveClass, out ThreadBudget tv) ? tv : null;
        int perDrive = (specific ?? byType ?? s.PerDriveDefault).Resolve(Environment.ProcessorCount * 4);
        return Math.Min(perDrive, ResolveMaxScanThreads(s));
    }

    /// <summary>Canonical form of a specific-drive override key so it matches
    /// <see cref="Platform.IVolumeInfoProvider.GetVolumeKey"/> output ("c:", "\\server\share").
    /// Delegates to <see cref="VolumeKeys.Normalize"/> — the rule lives in Contracts so the settings UI
    /// (which cannot reference Core) applies exactly the same one.</summary>
    internal static string NormalizeKey(string key) => VolumeKeys.Normalize(key);
}
