using FileManager.Contracts.IPC;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests.TestData;

/// <summary>Drives a preview the way the shell does, for the many cases that are really about the ROWS
/// rather than the run's lifecycle.
///
/// <para>A preview is a real run's planning phase: the engine freezes a work list, publishes
/// <c>run-planned</c>, and the view model streams that snapshot back through <c>get-run-plan-stream</c>.
/// So a row-projection test needs a plan on the fake's plan stream and a <see cref="RunPlannedEvent"/> to
/// carry its totals — two lines of ceremony that would otherwise be copied into ~40 tests.</para></summary>
internal static class RunPlans
{
    /// <summary>A plan event for <paramref name="runId"/>. Counts default to non-zero so the footer path
    /// is the one exercised: a plan with nothing to do is a case the shell answers itself, and the tests
    /// that care about it say so explicitly.</summary>
    internal static RunPlannedEvent Planned(
        Guid runId, Guid profileId,
        int copies = 1, int deletes = 0, long copyBytes = 1024, long deleteBytes = 0,
        bool truncated = false, string? error = null, DateTimeOffset? plannedAtUtc = null) =>
        new()
        {
            // The plan's own timestamp, and what a client measures staleness against. Defaults to the epoch
            // so a test that does not care is deterministic; a staleness test passes its clock's now.
            AtUtc = plannedAtUtc ?? DateTimeOffset.UnixEpoch,
            RunId = runId,
            ProfileId = profileId,
            PlannedCopies = copies,
            PlannedDeletes = deletes,
            PlannedCopyBytes = copyBytes,
            PlannedDeleteBytes = deleteBytes,
            Truncated = truncated,
            Error = error,
        };

    /// <summary>Scripts <paramref name="gateway"/>'s plan stream with whatever report the test already
    /// built and ingests it, exactly as <c>MainWindowViewModel.ShowRunPlanAsync</c> would. Returns the run
    /// id so a test can assert what was approved.</summary>
    internal static async Task<Guid> PreviewAsync(
        DryRunViewModel viewModel, FakeIpcGateway gateway,
        int copies = 1, int deletes = 0, bool truncated = false, DateTimeOffset? plannedAtUtc = null)
    {
        Guid runId = Guid.NewGuid();
        // The report the test scripted onto DryRunResult is the rows it expects to see; the plan stream is
        // simply where they now arrive from.
        gateway.RunPlanResults[runId] = gateway.DryRunResult;
        viewModel.BeginPlanning();
        await viewModel.LoadPlanAsync(Planned(
            runId, viewModel.ProfileId ?? Guid.NewGuid(), copies, deletes,
            truncated: truncated, plannedAtUtc: plannedAtUtc));
        return runId;
    }
}
