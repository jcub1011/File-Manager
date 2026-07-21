using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>Whether the profile was written, plus every validation issue the save produced.
/// Saved is false when an Error is present, or a BlockingWarning was not acknowledged.</summary>
public sealed record SaveOutcome(bool Saved, IReadOnlyList<ValidationIssue> Issues);

/// <summary>The UI's seam to the service: typed methods over the IPC client so viewmodels are
/// testable against a fake and never touch transport concerns.</summary>
public interface IIpcGateway
{
    Task<Result<EngineStatusSnapshot, IpcError>> GetStatusAsync(CancellationToken ct = default);
    Task<Result<IReadOnlyList<ProfileSummary>, IpcError>> ListProfilesAsync(CancellationToken ct = default);
    Task<Result<Profile, IpcError>> GetProfileAsync(Guid profileId, CancellationToken ct = default);
    Task<Result<SaveOutcome, IpcError>> SaveProfileAsync(Profile profile, bool acknowledgeWarnings, CancellationToken ct = default);
    Task<Result<bool, IpcError>> DeleteProfileAsync(Guid profileId, CancellationToken ct = default);
    /// <summary>Runs a streamed dry run. <paramref name="progress"/> (when supplied) receives
    /// throttled discovery updates as the service reports them; construct the
    /// <see cref="Progress{T}"/> on the UI thread so reports marshal there automatically.
    /// When <paramref name="draft"/> is supplied, the service previews that in-memory profile
    /// (unsaved edits) directly instead of resolving <paramref name="profileId"/> against the
    /// persisted catalog.</summary>
    Task<Result<DryRunReport, IpcError>> DryRunAsync(
        Guid profileId, IProgress<DryRunProgress>? progress = null, Profile? draft = null,
        CancellationToken ct = default);
    Task<Result<GlobalSettings, IpcError>> GetSettingsAsync(CancellationToken ct = default);
    Task<Result<GlobalSettings, IpcError>> SaveSettingsAsync(GlobalSettings settings, CancellationToken ct = default);
    /// <summary>Relocates the profiles storage directory, optionally moving the existing files.
    /// Returns the persisted settings (their <see cref="GlobalSettings.ProfilesDirectory"/> reflects
    /// the new, normalized location) plus the moved/skipped file outcome — skipped files mean a
    /// same-name copy already at the destination won, which the caller must surface.</summary>
    Task<Result<RelocateProfilesResponse, IpcError>> RelocateProfilesAsync(
        string newDirectory, bool moveExisting, CancellationToken ct = default);
    /// <summary>Asks the service to shut itself down (StartAndStopWithProgram mode on UI close).</summary>
    Task<Result<bool, IpcError>> ShutdownServiceAsync(CancellationToken ct = default);
}
