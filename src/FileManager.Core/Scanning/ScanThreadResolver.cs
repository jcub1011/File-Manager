using System;
using FileManager.Contracts.Settings;

namespace FileManager.Core.Scanning;

/// <summary>The single place that turns <see cref="ScanThreadingSettings"/> into concrete worker
/// counts. Replaces the removed per-profile <c>DryRunConcurrency</c>: scan (enumeration) concurrency
/// is now global-only, and the per-drive cap follows the precedence specific-drive → drive-type →
/// per-drive default. Kept pure/stateless so it is trivially unit-testable without I/O.</summary>
internal static class ScanThreadResolver
{
    /// <summary>Absolute ceiling on the <em>auto</em> scan-thread formula. Each scan worker is a
    /// dedicated LongRunning thread (~1 MB of stack), so ProcessorCount * 8 would spawn 512 threads on
    /// a 64-core box. This caps the derived value; an explicit user pin is honored as-is (floored at 1).
    /// No effect at or below 32 cores.</summary>
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
