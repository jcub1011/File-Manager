using System.Collections.Generic;

namespace FileManager.UI.Services;

/// <summary>One volume the user can pick in the settings drive-override editor.</summary>
/// <param name="VolumeKey">The canonical override key — exactly what
/// <c>VolumeKeys.Normalize</c> produces, so a picked drive matches the key the engine derives from a
/// scanned path.</param>
/// <param name="Label">What the user reads: <c>"C: — Windows (Local disk)"</c>.</param>
public sealed record DriveOption(string VolumeKey, string Label);

/// <summary>The machine's mounted volumes, behind an interface so view models stay testable and
/// Avalonia-free (mirrors <see cref="IFolderPicker"/>).</summary>
public interface ISystemDrives
{
    /// <summary>The ready volumes, in drive-letter order. Never throws and never null: a volume that
    /// cannot be queried is logged and skipped, because a disconnected network drive must not take the
    /// whole picker down.</summary>
    IReadOnlyList<DriveOption> List();
}
