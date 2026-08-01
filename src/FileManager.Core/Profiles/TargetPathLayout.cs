using FileManager.Contracts.Profiles;
using System.IO;

namespace FileManager.Core.Profiles;

/// <summary>Where one source file lands under each of a profile's target roots (spec §3.1.2).
/// <para>THE single definition of that rule. The live plan builder (<see cref="Jobs.JobPlanFactory"/>)
/// and the dry-run engine (<see cref="DryRun.DryRunEngine"/>) each carried a verbatim copy, and a
/// dry run whose path resolution drifts from the real run silently stops predicting it — which is the
/// entire point of the dry run. Resolve the layout once per file, then ask it per target.</para></summary>
public readonly struct TargetPathLayout
{
    // The path fragment appended to each target root: the bare file name when flattening, otherwise
    // the source's path relative to its watched root.
    private readonly string _leaf;

    private TargetPathLayout(bool flattens, string leaf)
    {
        Flattens = flattens;
        _leaf = leaf;
    }

    /// <summary>True when targets receive a flat drop rather than a mirrored tree.</summary>
    public bool Flattens { get; }

    /// <summary>Resolves the layout for one source file. An M:1 topology (more than one Source) forces
    /// Flatten regardless of <see cref="Profile.TargetLayout"/>, because a mirrored tree from several
    /// roots has no single structure to mirror.</summary>
    public static TargetPathLayout For(Profile profile, string sourcePath, string relativePath)
    {
        bool flattens = profile.TargetLayout == TargetLayout.Flatten || profile.Sources.Count > 1;
        // PreserveStructure never allocates the bare file name; Flatten never keeps the relative path.
        return new TargetPathLayout(flattens, flattens ? Path.GetFileName(sourcePath) : relativePath);
    }

    /// <summary>The final path this file resolves to under <paramref name="targetRoot"/>.</summary>
    public string Resolve(string targetRoot) => Path.Combine(targetRoot, _leaf);
}
