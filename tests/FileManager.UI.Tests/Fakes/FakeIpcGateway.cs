using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;

namespace FileManager.UI.Tests.Fakes;

/// <summary>Scripted gateway: tests assign the result fields and inspect the recorded calls.</summary>
internal sealed class FakeIpcGateway : IIpcGateway
{
    public List<(Profile Profile, bool Acknowledge)> SaveCalls { get; } = [];
    public List<Guid> DeleteCalls { get; } = [];
    public List<(Guid ProfileId, string? Scope)> DryRunCalls { get; } = [];
    public List<GlobalSettings> SaveSettingsCalls { get; } = [];

    public Result<EngineStatusSnapshot, IpcError> StatusResult { get; set; } =
        new EngineStatusSnapshot(false, 0, 0, 0, null);

    public Result<IReadOnlyList<ProfileSummary>, IpcError> ListResult { get; set; } =
        Result<IReadOnlyList<ProfileSummary>, IpcError>.Success([]);

    public Result<Profile, IpcError> GetResult { get; set; } =
        new IpcError("PROFILE_NOT_FOUND", "not scripted");

    public Result<SaveOutcome, IpcError> SaveResult { get; set; } =
        new SaveOutcome(true, []);

    public Result<bool, IpcError> DeleteResult { get; set; } = true;

    public Result<DryRunReport, IpcError> DryRunResult { get; set; } =
        new DryRunReport(Guid.Empty, DateTimeOffset.UnixEpoch, []);

    public Result<GlobalSettings, IpcError> GetSettingsResult { get; set; } = GlobalSettings.Default;
    public Result<GlobalSettings, IpcError> SaveSettingsResult { get; set; } = GlobalSettings.Default;
    public Result<bool, IpcError> ShutdownResult { get; set; } = true;
    public int ShutdownCalls { get; private set; }

    /// <summary>When set, DryRunAsync awaits this before returning (cancellation tests).</summary>
    public TaskCompletionSource? DryRunGate { get; set; }

    /// <summary>When set, DryRunAsync throws it (unexpected-exception tests).</summary>
    public Exception? DryRunException { get; set; }

    public Task<Result<EngineStatusSnapshot, IpcError>> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(StatusResult);

    public Task<Result<IReadOnlyList<ProfileSummary>, IpcError>> ListProfilesAsync(CancellationToken ct = default) =>
        Task.FromResult(ListResult);

    public Task<Result<Profile, IpcError>> GetProfileAsync(Guid profileId, CancellationToken ct = default) =>
        Task.FromResult(GetResult);

    public Task<Result<SaveOutcome, IpcError>> SaveProfileAsync(
        Profile profile, bool acknowledgeWarnings, CancellationToken ct = default)
    {
        SaveCalls.Add((profile, acknowledgeWarnings));
        return Task.FromResult(SaveResult);
    }

    public Task<Result<bool, IpcError>> DeleteProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        DeleteCalls.Add(profileId);
        return Task.FromResult(DeleteResult);
    }

    public async Task<Result<DryRunReport, IpcError>> DryRunAsync(
        Guid profileId, string? scopePath, CancellationToken ct = default)
    {
        DryRunCalls.Add((profileId, scopePath));
        if (DryRunException is not null)
            throw DryRunException;
        if (DryRunGate is not null)
        {
            try
            {
                await DryRunGate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Mirror the real gateway: cancellation is a Canceled result, never a throw.
                return Result<DryRunReport, IpcError>.Canceled();
            }
        }
        return DryRunResult;
    }

    public Task<Result<GlobalSettings, IpcError>> GetSettingsAsync(CancellationToken ct = default) =>
        Task.FromResult(GetSettingsResult);

    public Task<Result<GlobalSettings, IpcError>> SaveSettingsAsync(GlobalSettings settings, CancellationToken ct = default)
    {
        SaveSettingsCalls.Add(settings);
        return Task.FromResult(SaveSettingsResult);
    }

    public Task<Result<bool, IpcError>> ShutdownServiceAsync(CancellationToken ct = default)
    {
        ShutdownCalls++;
        return Task.FromResult(ShutdownResult);
    }
}

internal sealed class FakeFolderPicker(string? result = null) : IFolderPicker
{
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(result);
}
