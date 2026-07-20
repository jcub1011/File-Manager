using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FileManager.Contracts.Settings;

/// <summary>A scan/hash thread-count knob that is either "auto" (let the engine derive it from the
/// core count) or an explicit pin. <see cref="Value"/> null == auto; the default value (an omitted
/// JSON member) therefore reads back as auto. Round-trips as <c>{}</c> for auto or <c>{"Value":N}</c>
/// for a pin (the source generator drops the null under <c>WhenWritingNull</c>).</summary>
public readonly record struct ThreadBudget(int? Value)
{
    /// <summary>Defer to the per-level auto formula. Equal to <c>default(ThreadBudget)</c>.</summary>
    public static readonly ThreadBudget Auto = new((int?)null);

    public static ThreadBudget Explicit(int workers) => new(workers);

    [JsonIgnore]
    public bool IsAuto => Value is null;

    /// <summary>The effective worker count: the explicit pin, or <paramref name="autoValue"/> when
    /// auto. Floored at 1 so a resolved budget is always usable as a degree of parallelism.</summary>
    public int Resolve(int autoValue) => Math.Max(1, Value ?? autoValue);
}

/// <summary>Machine-level scan/hash concurrency (replaces the removed per-profile concurrency
/// override). Directory enumeration is bounded by a process-wide scan scheduler using
/// <see cref="MaxScanThreads"/> globally and a per-volume cap resolved by precedence
/// specific-drive → drive-type → <see cref="PerDriveDefault"/>. The evaluation/hashing phase uses
/// the independent <see cref="MaxHashThreads"/>.</summary>
public sealed record ScanThreadingSettings
{
    /// <summary>Global ceiling on concurrently-enumerating scan worker threads. Auto = ProcessorCount * 8.</summary>
    public ThreadBudget MaxScanThreads { get; init; } = ThreadBudget.Auto;

    /// <summary>Ceiling on dry-run evaluation (stat + hash) workers. Auto = ProcessorCount - 1.</summary>
    public ThreadBudget MaxHashThreads { get; init; } = ThreadBudget.Auto;

    /// <summary>Per-volume scan-thread cap when no drive-type or specific-drive override applies.
    /// Auto = ProcessorCount * 4. Never exceeds <see cref="MaxScanThreads"/> after resolution.</summary>
    public ThreadBudget PerDriveDefault { get; init; } = ThreadBudget.Auto;

    private readonly Dictionary<DriveClass, ThreadBudget>? _driveTypeOverrides;
    private readonly Dictionary<string, ThreadBudget>? _specificDriveOverrides;

    /// <summary>Per-volume caps keyed by physical medium; override <see cref="PerDriveDefault"/> for
    /// matching volumes. Empty by default (every volume falls to the per-drive default). Never null:
    /// an absent member deserializes to the shared empty map.</summary>
    public Dictionary<DriveClass, ThreadBudget> DriveTypeOverrides
    {
        get => _driveTypeOverrides ?? EmptyDriveTypeMap;
        init => _driveTypeOverrides = value;
    }

    /// <summary>Per-volume caps keyed by normalized volume key ("c:", "\\server\share"); highest
    /// precedence. Empty by default. Never null (see <see cref="DriveTypeOverrides"/>).</summary>
    public Dictionary<string, ThreadBudget> SpecificDriveOverrides
    {
        get => _specificDriveOverrides ?? EmptySpecificMap;
        init => _specificDriveOverrides = value;
    }

    private static readonly Dictionary<DriveClass, ThreadBudget> EmptyDriveTypeMap = new();
    private static readonly Dictionary<string, ThreadBudget> EmptySpecificMap = new();

    public static ScanThreadingSettings Default { get; } = new();
}
