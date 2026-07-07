using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Core.Profiles;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class ListProfilesHandler(IProfileCatalog catalog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.ListProfiles;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        List<ProfileSummary> summaries = [];
        foreach (Profile profile in catalog.All)
            summaries.Add(new ProfileSummary(profile.Id, profile.Name, profile.Active, TriggerSummary(profile)));
        IpcResponse response = new ProfileListResponse { Profiles = summaries };
        return Task.FromResult(response);
    }

    private static string TriggerSummary(Profile profile)
    {
        List<string> parts = [];
        if (profile.Triggers.ManualShell)
            parts.Add("Manual");
        if (profile.Triggers.Watcher)
            parts.Add("Watcher");
        if (profile.Triggers.Schedule is { Enabled: true } schedule)
            parts.Add($"Schedule ({schedule.Cron})");
        return parts.Count > 0 ? string.Join(", ", parts) : "None";
    }
}
