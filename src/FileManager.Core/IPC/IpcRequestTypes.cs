using FileManager.Contracts.IPC;

namespace FileManager.Core.IPC;

/// <summary>Maps a request instance to its wire discriminator — the dispatch-table key. An
/// exhaustive compile-time switch (no reflection, AOT-safe); a unit test pins this table to the
/// [JsonDerivedType] attributes on IpcRequest so the two can never drift apart.</summary>
public static class IpcRequestTypes
{
    public const string GetStatus = "get-status";
    public const string ListProfiles = "list-profiles";
    public const string GetProfile = "get-profile";
    public const string SaveProfile = "save-profile";
    public const string DeleteProfile = "delete-profile";
    public const string ValidateProfile = "validate-profile";
    public const string GetMatching = "get-matching";
    public const string RunProfile = "run-profile";
    public const string SetPaused = "set-paused";
    public const string DryRun = "dry-run";
    public const string DryRunStream = "dry-run-stream";
    public const string GetRecentJobs = "get-recent-jobs";
    public const string GetJobLog = "get-job-log";
    public const string Subscribe = "subscribe";
    public const string GetSettings = "get-settings";
    public const string UpdateSettings = "update-settings";
    public const string RelocateProfiles = "relocate-profiles";
    public const string Shutdown = "shutdown";
    public const string ApproveRun = "approve-run";
    public const string CancelRun = "cancel-run";
    public const string GetRunPlanStream = "get-run-plan-stream";
    public const string GetRunPlanPage = "get-run-plan-page";
    public const string GetRunPlanView = "get-run-plan-view";
    public const string GetRuns = "get-runs";
    public const string GetRunDetail = "get-run-detail";
    public const string SetRunPaused = "set-run-paused";
    public const string DiscardRun = "discard-run";

    public static string DiscriminatorOf(IpcRequest request) => request switch
    {
        GetStatusRequest => GetStatus,
        ListProfilesRequest => ListProfiles,
        GetProfileRequest => GetProfile,
        SaveProfileRequest => SaveProfile,
        DeleteProfileRequest => DeleteProfile,
        ValidateProfileRequest => ValidateProfile,
        GetMatchingProfilesRequest => GetMatching,
        RunProfileRequest => RunProfile,
        SetPausedRequest => SetPaused,
        DryRunRequest => DryRun,
        DryRunStreamRequest => DryRunStream,
        GetRecentJobsRequest => GetRecentJobs,
        GetJobLogRequest => GetJobLog,
        SubscribeEventsRequest => Subscribe,
        GetSettingsRequest => GetSettings,
        UpdateSettingsRequest => UpdateSettings,
        RelocateProfilesRequest => RelocateProfiles,
        ShutdownRequest => Shutdown,
        ApproveRunRequest => ApproveRun,
        CancelRunRequest => CancelRun,
        GetRunPlanStreamRequest => GetRunPlanStream,
        GetRunPlanPageRequest => GetRunPlanPage,
        GetRunPlanViewRequest => GetRunPlanView,
        GetRunsRequest => GetRuns,
        GetRunDetailRequest => GetRunDetail,
        SetRunPausedRequest => SetRunPaused,
        DiscardRunRequest => DiscardRun,
        _ => throw new System.ArgumentOutOfRangeException(nameof(request),
            $"unmapped request type {request.GetType().Name} — add it here and to IpcRequest's [JsonDerivedType] table"),
    };
}
