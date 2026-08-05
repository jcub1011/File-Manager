using FileManager.Contracts.Profiles;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FileManager.UI.ViewModels;

/// <summary>The read-only summary shown before an imported profile file is saved: what trees it
/// reads, where it writes, whether it disposes sources (highlighted — the paths were authored by
/// someone else), and any transformer steps with their executables. Immutable snapshot, no
/// commands; the dialog returns the user's Import/Skip choice via ShowDialog&lt;bool&gt;.</summary>
public sealed class ImportPreviewViewModel
{
    public required string FileName { get; init; }
    public required string ProfileName { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public required IReadOnlyList<string> Targets { get; init; }
    public required string DispositionText { get; init; }
    /// <summary>True when a run disposes source files (trash/permanent delete) — the dialog styles
    /// the disposition as a danger banner so it cannot be skimmed past.</summary>
    public required bool IsDestructive { get; init; }
    public required string VerificationText { get; init; }
    public required string SyncModeText { get; init; }
    public required IReadOnlyList<string> TransformerLines { get; init; }
    public bool HasTransformers => TransformerLines.Count > 0;

    public static ImportPreviewViewModel From(string fileName, Profile profile) => new()
    {
        FileName = fileName,
        ProfileName = profile.Name,
        Sources = profile.Sources.Select(s => s.Path).ToList(),
        Targets = profile.Targets.Select(t => t.Path).ToList(),
        // Shared with the Preview tab's approval footer, so the two cannot describe the same policy
        // differently — see SourceDisposition.
        DispositionText = SourceDisposition
            .Describe(profile.Policies.OnSuccess, profile.Policies.ArchiveFolder).Text,
        IsDestructive = SourceDisposition
            .Describe(profile.Policies.OnSuccess, profile.Policies.ArchiveFolder).Destroys,
        VerificationText = $"Verification: {profile.Policies.VerificationMethod}",
        SyncModeText = $"Sync mode: {profile.SyncMode}",
        TransformerLines = profile.Transformers is { Count: > 0 } steps
            ? steps.OrderBy(s => s.Step)
                .Select(s => $"Step {s.Step} \"{s.Name}\" runs: {s.ExecutablePath} {s.Arguments}".TrimEnd())
                .ToList()
            : [],
    };
}
