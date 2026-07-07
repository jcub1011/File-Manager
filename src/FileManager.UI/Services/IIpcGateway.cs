using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
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
    Task<Result<DryRunReport, IpcError>> DryRunAsync(Guid profileId, string? scopePath, CancellationToken ct = default);
}
