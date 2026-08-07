using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Runs;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>Answers with one run's snapshot directory and nothing else — the only thing the plan
/// handlers ask a coordinator for. Shared by the stream and page handler tests so both drive the same
/// double rather than two that could drift on what "unknown run" means.</summary>
internal sealed class SingleSnapshotCoordinator(Guid runId, string directory) : IRunCoordinator
{
    public string? SnapshotDirectory(Guid id) => id == runId ? directory : null;

    public Result<RunHandle, string> Begin(Profile profile, string? scopePath) => "not used";
    public Result Approve(Guid id, bool approve, bool acknowledgeWarnings = false) => Result.Success();
    public Result Cancel(Guid id) => Result.Success();
    public Result Discard(Guid id) => Result.Success();
    public RunStatus? GetStatus(Guid id) => null;
    public void Settled(Guid id, JobCompletion? completion) { }
    public Profile? PlannedProfile(Guid id) => null;
    public void Coalesced(Guid id) { }
    public IReadOnlyList<RunSummaryDto> ListRuns() => [];
    public Result SetPaused(Guid id, bool paused) => Result.Success();
    public Task StopAsync() => Task.CompletedTask;
}
