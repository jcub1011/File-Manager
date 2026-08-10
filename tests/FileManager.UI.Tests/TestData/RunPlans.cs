using FileManager.Contracts.DryRun;
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

    /// <summary>Puts whatever report the test built behind the paged verbs and opens it, exactly as
    /// <c>MainWindowViewModel.ShowRunPlanAsync</c> would. Returns the run id so a test can assert what was
    /// approved.
    ///
    /// <para>Both tabs' first pages are materialized before returning. A paged list answers a miss with a
    /// placeholder and fetches in the background, so a test that indexed straight into
    /// <c>VisibleRows</c> would otherwise be asserting against "…" — the real UI resolves this by
    /// rendering the arrival a moment later, and a test resolves it by waiting for it.</para></summary>
    internal static async Task<Guid> PreviewAsync(
        DryRunViewModel viewModel, FakeIpcGateway gateway,
        int copies = 1, int deletes = 0, bool truncated = false, DateTimeOffset? plannedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(gateway);
        Guid runId = Guid.NewGuid();
        // The report the test scripted onto DryRunResult is the rows it expects to see; the paged plan is
        // simply where they now come from.
        gateway.DryRunResult.TryGetValue(out DryRunReport? plan);
        if (plan is not null)
            gateway.ServePlan(runId, plan);
        gateway.RunPlanResults[runId] = gateway.DryRunResult;
        viewModel.BeginPlanning();
        await viewModel.LoadPlanAsync(Planned(
            runId, viewModel.ProfileId ?? Guid.NewGuid(), copies, deletes,
            truncated: truncated, plannedAtUtc: plannedAtUtc));
        await SettleAsync(viewModel);
        return runId;
    }

    /// <summary>A view model with <paramref name="plan"/> open, over a gateway serving it through the
    /// paged verbs. The one-liner for a test that is about what a plan LOOKS like rather than about the
    /// preview's lifecycle.</summary>
    internal static async Task<(DryRunViewModel ViewModel, FakeIpcGateway Gateway, Guid RunId)> OpenAsync(
        DryRunReport plan)
    {
        FakeIpcGateway gateway = new() { DryRunResult = plan };
        // Zero debounce keeps search-driven rebuilds prompt so tests can await PendingRebuild and assert.
        DryRunViewModel viewModel = new(gateway, searchDebounce: TimeSpan.Zero);
        viewModel.SetProfile(Guid.NewGuid(), "P");
        Guid runId = await PreviewAsync(viewModel, gateway);
        return (viewModel, gateway, runId);
    }

    /// <summary>Waits for both tabs' visible rows to stop being placeholders. Call after anything that
    /// republishes a list — a filter change, a search, a fresh load.</summary>
    internal static async Task SettleAsync(DryRunViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        await viewModel.Sources.PendingRebuild;
        await viewModel.Destinations.PendingRebuild;
        await SettleAsync(viewModel.Sources.VisibleRows, r => r.FileName);
        await SettleAsync(viewModel.Destinations.VisibleRows, r => r.FileName);
    }

    /// <summary>Touches the first page's worth of rows — which is what starts their fetch, since a paged
    /// list only requests what it is asked to render — then waits until none of them reads as a
    /// placeholder.</summary>
    private static async Task SettleAsync<T>(IReadOnlyList<T> rows, Func<T, string> nameOf, int timeoutMs = 5000)
    {
        int window = Math.Min(rows.Count, PagedDryRunRowStore.PageRows);
        if (window == 0)
            return;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            bool pending = false;
            for (int i = 0; i < window; i++)
            {
                if (nameOf(rows[i]) == DryRunRowStore.PlaceholderName)
                    pending = true;
            }
            if (!pending)
                return;
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"a dry-run page did not arrive within {timeoutMs}ms");
            await Task.Delay(5);
        }
    }
}
