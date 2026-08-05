using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.UI.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>One run in the job queue.
///
/// <para>A run, not a file — that is the whole distinction from <see cref="ActivityRow"/>. The activity
/// panel is the per-file feed; this is the level a user thinks in and the only level with a start, an end,
/// a total, and something to pause. Individual file jobs cannot be attributed to a run over the wire at all
/// (<c>JobSummaryDto</c> carries a <c>ProfileId</c>, never a <c>RunId</c> — a deliberate decision, since
/// the trigger queue coalesces across runs and a run id would misattribute the survivor), so a row's
/// progress comes from <c>run-progress</c>'s own <c>Completed</c>/<c>Total</c>.</para>
///
/// <para>MUTATED in place as events arrive, like <see cref="ActivityRow"/> and unlike
/// <c>ProfileListItem</c>: a row that was replaced wholesale would lose selection and jump in the list on
/// every progress sample. Status glyph and colour are exposed as resource-key STRINGS resolved in XAML by
/// <c>IconConverters</c>, so this type keeps no Avalonia dependency and its tests run without an
/// Application.</para></summary>
public sealed partial class JobQueueRow : ObservableObject
{
    public required Guid RunId { get; init; }
    public required Guid ProfileId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Clock behind <see cref="IsOld"/>. Injected so a test can advance it rather than waiting
    /// out the real threshold.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>When the run reached its terminal state; null while it is still live.
    /// <para>A finished run is now retained until it is discarded, so the queue has to be able to say HOW
    /// finished: a run that ended a minute ago and one that ended yesterday are both <c>Closed</c>.</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOld), nameof(FinishedAgeText), nameof(HasFinishedAge))]
    public partial DateTimeOffset? ClosedAtUtc { get; set; }

    /// <summary>From the wire, never resolved locally: a run planned from an unsaved draft has a profile
    /// that exists in no catalog, so a client-side lookup returns null for exactly the runs the GUI starts.</summary>
    [ObservableProperty] public partial string ProfileName { get; set; } = "";

    /// <summary>The run's phase as a wire string — <c>Planning</c>, <c>Waiting</c>,
    /// <c>AwaitingApproval</c>, <c>Executing</c>, <c>Closed</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusIconKey), nameof(StatusColorKey),
        nameof(IsPausable), nameof(IsCancellable), nameof(IsAwaitingApproval), nameof(IsFinished),
        nameof(ShowProgress), nameof(IsIndeterminate))]
    public partial string Phase { get; set; } = nameof(RunPhaseNames.Planning);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusIconKey), nameof(StatusColorKey))]
    public partial string Outcome { get; set; } = "None";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusIconKey), nameof(StatusColorKey),
        nameof(PauseLabel), nameof(PauseIconKey))]
    public partial bool Paused { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressFraction), nameof(IsIndeterminate))]
    public partial int Completed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressFraction), nameof(IsIndeterminate),
        nameof(ShowProgress))]
    public partial int Total { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial int Deleted { get; set; }

    /// <summary>Live scan counts while planning; the only figures a plan has before it has a denominator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial long ScannedSources { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial long ScannedDestinations { get; set; }

    /// <summary>What the plan says it will do, once it is planned. Drives the awaiting-approval summary.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial int PlannedCopies { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial int PlannedDeletes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial long PlannedCopyBytes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasProblem))]
    public partial string? PlanError { get; set; }

    /// <summary>Set when this window planned the run, so its plan has been SHOWN to the user.
    /// <para>Gates Approve. A run this window did not plan gets "View plan" instead — the two-phase design
    /// exists so nobody approves work they have not looked at, and offering a bare Approve on a row that is
    /// only a name and a count would quietly undo that.</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApproveHere))]
    public partial bool PlanWasShown { get; set; }

    public bool IsAwaitingApproval => Phase == nameof(RunPhaseNames.AwaitingApproval);
    public bool IsFinished => Phase == nameof(RunPhaseNames.Closed);

    /// <summary>How long a finished run stays "recent" before the row is dimmed as history.
    ///
    /// <para>Ten minutes, which is exactly how long a finished run used to SURVIVE — the engine forgot it
    /// at this mark. The number kept its meaning ("this run is no longer what is going on right now") and
    /// lost its teeth: it dims the row instead of deleting the record. Deliberately not a setting; the
    /// setting is how long a finished run is KEPT, which is a different question.</para></summary>
    public static readonly TimeSpan OldAfter = TimeSpan.FromMinutes(10);

    /// <summary>Whether this row is history rather than news. Drives the muted styling.</summary>
    public bool IsOld =>
        ClosedAtUtc is { } closed && Time.GetUtcNow() - closed >= OldAfter;

    public bool HasFinishedAge => ClosedAtUtc is not null;

    /// <summary>How long ago the run finished, in prose. Shown for every finished row, not just old ones —
    /// now that runs are kept, "when" is as much a part of the row as "what".</summary>
    public string FinishedAgeText
    {
        get
        {
            if (ClosedAtUtc is not { } closed)
                return "";
            TimeSpan age = Time.GetUtcNow() - closed;
            return age < TimeSpan.FromMinutes(1)
                ? "finished just now"
                : age < TimeSpan.FromHours(1)
                    ? $"finished {(int)age.TotalMinutes} min ago"
                    : age < TimeSpan.FromDays(1)
                        ? $"finished {(int)age.TotalHours} hr ago"
                        : $"finished {closed.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
    }

    /// <summary>Recomputes the age-derived members. Called on a timer by the shell, because nothing else
    /// changes when time passes.</summary>
    public void RefreshAge()
    {
        if (ClosedAtUtc is null)
            return;
        OnPropertyChanged(nameof(IsOld));
        OnPropertyChanged(nameof(FinishedAgeText));
    }

    /// <summary>Approve/Discard are offered inline only for a plan this window showed.</summary>
    public bool CanApproveHere => IsAwaitingApproval && PlanWasShown;

    /// <summary>A run parked for approval is doing nothing, so there is nothing to hold. A finished one is
    /// past holding.</summary>
    public bool IsPausable => !IsFinished && !IsAwaitingApproval;

    public bool IsCancellable => !IsFinished;

    /// <summary>Show a determinate bar only while executing against a known denominator. Planning has no
    /// total by construction — computing one is what it is doing.</summary>
    public bool ShowProgress => !IsFinished;
    public bool IsIndeterminate => !IsFinished && Total == 0;

    public double ProgressFraction => Total > 0 ? Math.Min(1.0, (double)Completed / Total) : 0;

    public string PauseLabel => Paused ? "Resume" : "Pause";
    public string PauseIconKey => Paused ? "IconPlay" : "IconPause";

    public bool HasProblem => PlanError is not null
        || Outcome is "CompletedWithProblems" or "PlanFailed";

    /// <summary>The phase in prose. <c>Paused</c> wins over the phase: it is what the user did and the
    /// reason the counters below have stopped moving.</summary>
    public string StatusText
    {
        get
        {
            if (Paused)
                return "Paused";
            return Phase switch
            {
                nameof(RunPhaseNames.Waiting) => "Waiting to start",
                nameof(RunPhaseNames.Planning) => "Working out what this will do",
                nameof(RunPhaseNames.AwaitingApproval) => "Waiting for approval",
                nameof(RunPhaseNames.Executing) => "Running",
                nameof(RunPhaseNames.Closed) => ClosedText,
                _ => Phase,
            };
        }
    }

    private string ClosedText => Outcome switch
    {
        "Succeeded" => "Finished",
        "CompletedWithProblems" => "Finished with problems",
        "Cancelled" => "Cancelled",
        "PlanFailed" => PlanError is { } error ? $"Could not be planned — {error}" : "Could not be planned",
        _ => "Finished",
    };

    /// <summary>Resource keys, resolved in XAML — see the class remarks. A tolerant switch rather than a
    /// reuse of the enum title helpers, because the phase and outcome both arrive as wire STRINGS.</summary>
    public string StatusIconKey
    {
        get
        {
            if (Paused)
                return "IconPause";
            return Phase switch
            {
                nameof(RunPhaseNames.Waiting) => "IconSkip",
                nameof(RunPhaseNames.Planning) => "IconSearch",
                nameof(RunPhaseNames.AwaitingApproval) => "IconQuestion",
                nameof(RunPhaseNames.Executing) => "IconOverwrite",
                nameof(RunPhaseNames.Closed) => Outcome switch
                {
                    "Succeeded" => "IconCheckmark",
                    "Cancelled" => "IconSkip",
                    _ => "IconWarning",
                },
                _ => "IconQuestion",
            };
        }
    }

    public string StatusColorKey
    {
        get
        {
            if (Paused)
                return "Brush.Warning";
            return Phase switch
            {
                nameof(RunPhaseNames.Waiting) => "Brush.Muted",
                nameof(RunPhaseNames.Planning) or nameof(RunPhaseNames.Executing) => "Brush.Info",
                nameof(RunPhaseNames.AwaitingApproval) => "Brush.Warning",
                nameof(RunPhaseNames.Closed) => Outcome switch
                {
                    "Succeeded" => "Brush.Success",
                    "Cancelled" => "Brush.Muted",
                    _ => "Brush.Danger",
                },
                _ => "Brush.Muted",
            };
        }
    }

    /// <summary>The counts line, which says something different in every phase — a planning run has no
    /// denominator, a parked one has only a forecast, and a running one has both.</summary>
    public string ProgressText => Phase switch
    {
        nameof(RunPhaseNames.Waiting) => "Queued behind other previews",
        nameof(RunPhaseNames.Planning) => ScannedDestinations > 0
            ? $"Scanned {ScannedSources:N0} source file(s), {ScannedDestinations:N0} at the destination"
            : ScannedSources > 0
                ? $"Scanned {ScannedSources:N0} source file(s)"
                : "Starting the scan…",
        nameof(RunPhaseNames.AwaitingApproval) => PlannedSummary,
        nameof(RunPhaseNames.Executing) => Total > 0
            ? $"{Completed:N0} of {Total:N0} file(s)" + (Deleted > 0 ? $", {Deleted:N0} removed" : "")
            : "Starting…",
        _ => "",
    };

    private string PlannedSummary
    {
        get
        {
            string copies = $"{PlannedCopies:N0} to copy or update ({ByteSize.Format(PlannedCopyBytes)})";
            return PlannedDeletes > 0 ? $"{copies}, {PlannedDeletes:N0} to remove" : copies;
        }
    }

    public string StartedText => StartedAtUtc.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>The <c>RunPhase</c> names as they cross the wire, so the rows above switch on a symbol rather
/// than a bare literal.
/// <para>Not a reference to Core's <c>RunPhase</c> enum: the UI may only reference
/// <c>FileManager.Contracts</c> (§1 rule 3), and the phase deliberately crosses IPC as a string so a new
/// member is not a breaking wire change. <c>Waiting</c> has no enum member at all — it is a display
/// distinction over <c>Planning</c> that the coordinator publishes.</para></summary>
internal static class RunPhaseNames
{
    public const string Planning = nameof(Planning);
    public const string Waiting = nameof(Waiting);
    public const string AwaitingApproval = nameof(AwaitingApproval);
    public const string Executing = nameof(Executing);
    public const string Closed = nameof(Closed);
}

/// <summary>The job queue: every run the engine knows about, with live progress, per-run pause and cancel.
///
/// <para>Lives in a NON-MODAL window so the main window stays usable — a queue you have to dismiss before
/// you can edit a profile is a dialog, not a queue. It is owned by the shell rather than the window, so it
/// keeps receiving events while the window is closed; a queue that only counted what happened while you
/// were watching would be worse than none.</para>
///
/// <para>Live rows come from the engine event stream, which is bounded and drop-oldest, so
/// <see cref="ReconcileAsync"/> re-seeds from <c>get-runs</c> on window open and on every (re)connect —
/// the same contract <see cref="ActivityViewModel"/> follows against <c>get-recent-jobs</c>.</para></summary>
public sealed partial class JobQueueViewModel(IIpcGateway gateway, TimeProvider? time = null) : ViewModelBase
{
    /// <summary>Clock handed to every row, for the finished-age captions. Injected so a test can advance it
    /// instead of waiting out the ten-minute threshold.</summary>
    private readonly TimeProvider time = time ?? TimeProvider.System;

    /// <summary>Rows kept.
    ///
    /// <para>Matches the engine's own <c>MaxRetainedClosedRuns</c> backstop. It used to be 100 against a
    /// ten-minute engine-side window, which no bound could realistically reach; now that finished runs are
    /// retained until discarded, a lower cap here would mean the user could not SEE — let alone discard —
    /// the runs the engine is still holding, which is the one thing the queue is for.</para></summary>
    internal const int MaxRows = 500;

    public ObservableCollection<JobQueueRow> Runs { get; } = [];

    [ObservableProperty] public partial JobQueueRow? SelectedRun { get; set; }

    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRuns))]
    public partial bool HasRuns { get; set; }

    public bool HasNoRuns => !HasRuns;

    /// <summary>How many runs are not finished — the number worth putting on the shell's queue button.</summary>
    [ObservableProperty] public partial int ActiveCount { get; set; }

    /// <summary>Set by the shell: whether it planned this run, so the row can offer Approve rather than
    /// "View plan". See <see cref="JobQueueRow.PlanWasShown"/>.</summary>
    public Func<Guid, bool>? IsOwnRun { get; set; }

    /// <summary>Set by the shell: answers a parked run (approve or discard) exactly as the Preview tab's
    /// footer does, so both routes go through one path.</summary>
    public Func<Guid, bool, Task>? AnswerRun { get; set; }

    /// <summary>Set by the shell: selects the run's profile and loads its plan into the Preview tab. The
    /// route by which a run this window did not plan can still be looked at before being approved.</summary>
    public Func<JobQueueRow, Task>? ViewPlan { get; set; }

    [RelayCommand]
    public async Task RefreshAsync() => await ReconcileAsync();

    /// <summary>Re-seeds from <c>get-runs</c>. Called on window open and on every (re)connect, because the
    /// event stream drops frames under back-pressure.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var result = await gateway.GetRunsAsync(ct);
        if (result.IsCanceled)
            return;
        if (result.TryGetError(out IpcError? error))
        {
            // Leave the existing rows alone: a transient outage must not blank the queue.
            ErrorMessage = $"Could not load the run queue: {error.Message}";
            return;
        }
        result.TryGetValue(out IReadOnlyList<RunSummaryDto>? runs);
        ErrorMessage = null;

        // Unlike the activity feed's ring, get-runs IS the complete picture — the coordinator holds every
        // run, closed ones included, until it prunes them. So a row the response omits has been pruned and
        // should go, which makes this a full replace rather than a merge. Rows are still MUTATED where the
        // ids match, so selection and scroll position survive a reconcile.
        Guid selectedId = SelectedRun?.RunId ?? Guid.Empty;
        Dictionary<Guid, JobQueueRow> existing = [];
        foreach (JobQueueRow row in Runs)
            existing[row.RunId] = row;

        Runs.Clear();
        foreach (RunSummaryDto run in runs!)
        {
            // A get-runs that was already in flight when the user pressed Discard can still name the run.
            // Without this the reconcile puts the row back, which is the same resurrection the event
            // handlers guard against, arriving by a different route.
            if (_discarded.Contains(run.RunId))
                continue;
            if (existing.TryGetValue(run.RunId, out JobQueueRow? row))
                Apply(row, run);
            else
                row = RowFrom(run);
            Runs.Add(row);
        }
        TrimAndFlag();
        SelectedRun = FindRow(selectedId);
    }

    /// <summary>A run finished planning: its work list is frozen and it is waiting to be approved.</summary>
    public void OnRunPlanned(RunPlannedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        // A discarded run's own closing events must not put its row back — see _discarded.
        if (_discarded.Contains(evt.RunId))
            return;
        JobQueueRow row = FindRow(evt.RunId) ?? Insert(new JobQueueRow
        {
            RunId = evt.RunId,
            ProfileId = evt.ProfileId,
            StartedAtUtc = evt.AtUtc,
            Time = time,
            ProfileName = evt.ProfileName,
            PlanWasShown = IsOwnRun?.Invoke(evt.RunId) ?? false,
        });
        if (evt.ProfileName.Length > 0)
            row.ProfileName = evt.ProfileName;
        row.PlannedCopies = evt.PlannedCopies;
        row.PlannedDeletes = evt.PlannedDeletes;
        row.PlannedCopyBytes = evt.PlannedCopyBytes;
        row.PlanError = evt.Error;
        // A plan that FAILED is already closed engine-side, so the row must not sit offering to approve
        // something that no longer exists.
        row.Phase = evt.Error is null ? RunPhaseNames.AwaitingApproval : RunPhaseNames.Closed;
        if (evt.Error is not null)
        {
            row.Outcome = "PlanFailed";
            row.ClosedAtUtc = evt.AtUtc;   // a failed plan is already closed engine-side
        }
        // Re-asked here rather than trusted from insert time: planning is detached service-side, so this
        // event can beat the shell's own run-profile reply and the ownership answer changes a moment later.
        row.PlanWasShown = IsOwnRun?.Invoke(evt.RunId) ?? false;
        TrimAndFlag();
    }

    /// <summary>A progress sample. Creates a row when the run-planned frame was lost — unlike the activity
    /// feed, which ignores progress for a job it never saw start: a run is long-lived and coarse enough that
    /// a synthesized row is far better than a queue silently missing an executing run.</summary>
    public void OnRunProgress(RunProgressEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_discarded.Contains(evt.RunId))
            return;
        JobQueueRow row = FindRow(evt.RunId) ?? Insert(new JobQueueRow
        {
            RunId = evt.RunId,
            ProfileId = Guid.Empty,
            StartedAtUtc = evt.AtUtc,
            Time = time,
            ProfileName = "",
            PlanWasShown = IsOwnRun?.Invoke(evt.RunId) ?? false,
        });
        // Never over a terminal phase: run-completed is authoritative, and a late progress sample must not
        // resurrect a finished run as running.
        if (row.IsFinished)
            return;
        row.Phase = evt.Phase;
        row.Paused = evt.Paused;
        row.Completed = evt.Completed;
        row.Total = evt.Total;
        row.Deleted = evt.Deleted;
        row.ScannedSources = evt.ScannedSources;
        row.ScannedDestinations = evt.ScannedDestinations;
        TrimAndFlag();
    }

    /// <summary>A run reached its terminal state — the only authoritative statement of what it did.</summary>
    public void OnRunCompleted(RunCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        // The one that lingers longest: a discarded EXECUTING run keeps unwinding and closes only once its
        // in-flight jobs finish, so this can arrive well after the row went.
        if (_discarded.Contains(evt.RunId))
            return;
        JobQueueRow row = FindRow(evt.RunId) ?? Insert(new JobQueueRow
        {
            RunId = evt.RunId,
            ProfileId = evt.ProfileId,
            StartedAtUtc = evt.AtUtc,
            Time = time,
            ProfileName = "",
        });
        row.Phase = RunPhaseNames.Closed;
        row.Outcome = evt.Outcome;
        // The terminal event's own stamp, which is when the run actually closed — so a row ages from the
        // moment the work ended rather than from whenever this window happened to hear about it.
        row.ClosedAtUtc = evt.AtUtc;
        row.Completed = evt.Succeeded + evt.Skipped + evt.Failed;
        row.Deleted = evt.Deleted;
        // A finished run cannot be paused, so the flag must not outlive it — a cancelled-while-paused run
        // would otherwise keep reading "Paused" forever.
        row.Paused = false;
        TrimAndFlag();
    }

    /// <summary>Pauses or resumes the row's run. Optimistic: the flag flips now and the reconcile/progress
    /// stream corrects it, because the round trip is slower than a user expects a toggle to be.</summary>
    [RelayCommand]
    private async Task TogglePauseAsync(JobQueueRow? row)
    {
        if (row is null || !row.IsPausable)
            return;
        bool paused = !row.Paused;
        row.Paused = paused;
        try
        {
            var result = await gateway.SetRunPausedAsync(row.RunId, paused);
            if (result.IsCanceled)
                return;
            if (result.TryGetError(out IpcError? error))
            {
                row.Paused = !paused;   // put the toggle back; it did not take
                // RUN_NOT_FOUND means the run finished or was pruned while the click was in flight — a
                // normal race for a view fed by a lossy stream, so it is not worth a banner.
                if (error.Code != "RUN_NOT_FOUND")
                    ErrorMessage = $"Could not {(paused ? "pause" : "resume")} the run: {error.Message}";
                else
                    await ReconcileAsync();
            }
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a command has no exception boundary of its own.
            Log.Error(ex, "Setting run {RunId} paused={Paused} failed", row.RunId, paused);
            row.Paused = !paused;
            ErrorMessage = $"Could not {(paused ? "pause" : "resume")} the run: {ex.Message}";
        }
    }

    /// <summary>Cancels the row's run.
    /// <para>Note what this does and does not do, which the view's copy must state: work not yet started is
    /// dropped, jobs already in flight ALWAYS finish (I-ATOMIC-JOB), and a Mirror run's deletion phase is
    /// skipped entirely.</para></summary>
    [RelayCommand]
    private async Task CancelRunAsync(JobQueueRow? row)
    {
        if (row is null || !row.IsCancellable)
            return;
        try
        {
            var result = await gateway.CancelRunAsync(row.RunId);
            if (result.IsCanceled)
                return;
            if (result.TryGetError(out IpcError? error))
            {
                if (error.Code != "RUN_NOT_FOUND")
                    ErrorMessage = $"Could not cancel the run: {error.Message}";
                await ReconcileAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Cancelling run {RunId} failed", row.RunId);
            ErrorMessage = $"Could not cancel the run: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApproveAsync(JobQueueRow? row)
    {
        if (row is null || !row.CanApproveHere || AnswerRun is null)
            return;
        await AnswerRun(row.RunId, true);
    }

    /// <summary>Removes the row's run from the engine — the only user-driven deletion.
    ///
    /// <para>Offered on EVERY row, including a parked plan (declining a plan is discarding it) and a
    /// finished one (which is otherwise kept until auto-delete reaps it, or forever when that is off).</para>
    ///
    /// <para>A run that is still LIVE is confirmed first: discarding it cancels real work. The row goes at
    /// once, but jobs already in flight still finish (I-ATOMIC-JOB), which is what the confirmation text
    /// has to say.</para></summary>
    [RelayCommand]
    private async Task DiscardAsync(JobQueueRow? row)
    {
        if (row is null)
            return;
        try
        {
            if (!row.IsFinished && ConfirmDiscard is not null
                && !await ConfirmDiscard(DiscardPrompt(row)))
                return;

            // BEFORE the call, not after: the engine removes the run synchronously and its closing events
            // are published from the run's own detached task, so they can reach this window while the await
            // below is still suspended. Remembering the id first is what makes the filter airtight.
            RememberDiscard(row.RunId);
            var result = await gateway.DiscardRunAsync(row.RunId);
            if (result.IsCanceled)
            {
                Forget(row.RunId);
                return;
            }
            if (result.TryGetError(out IpcError? error))
            {
                // RUN_NOT_FOUND means it was already gone — which is the outcome the user wanted, so the
                // row still goes. Anything else is a real failure, and the run is still there: stop
                // filtering its events, or its row would sit frozen at whatever it last said.
                if (error.Code != "RUN_NOT_FOUND")
                {
                    Forget(row.RunId);
                    ErrorMessage = $"Could not discard the run: {error.Message}";
                    return;
                }
            }
            // Removed locally rather than waiting for a reconcile: there is no run-discarded event, and a
            // row that lingers after the user pressed Discard reads as a failure.
            Runs.Remove(row);
            if (ReferenceEquals(SelectedRun, row))
                SelectedRun = null;
            DiscardedRun?.Invoke(row.RunId);
            TrimAndFlag();
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a command has no exception boundary of its own.
            Log.Error(ex, "Discarding run {RunId} failed", row.RunId);
            Forget(row.RunId);
            ErrorMessage = $"Could not discard the run: {ex.Message}";
        }
    }

    private static string DiscardPrompt(JobQueueRow row) => row.IsAwaitingApproval
        ? $"Discard the plan for \"{row.ProfileName}\"? Nothing has been changed and nothing will be."
        : $"Discard the run for \"{row.ProfileName}\"? It will be cancelled and removed from the queue. "
          + "Files already being copied will finish, and a Mirror run will remove nothing.";

    /// <summary>Set by the composition root to confirm discarding a run that is still live. Null proceeds,
    /// so headless tests are not blocked — mirrors <c>MainWindowViewModel.ConfirmDeleteProfile</c>.</summary>
    public Func<string, Task<bool>>? ConfirmDiscard { get; set; }

    /// <summary>Set by the shell, and called with the id of a run the user discarded, so anything else
    /// holding it (the retained-preview store, the Preview tab) can let go.</summary>
    public Action<Guid>? DiscardedRun { get; set; }

    /// <summary>How many discarded run ids are remembered. Bounded only so the set cannot grow for the
    /// session; the events it filters all arrive within moments of the discard, except a discarded
    /// EXECUTING run's terminal event, which waits for its in-flight jobs. Sixty-four is far more than a
    /// user discards while one run drains.</summary>
    private const int MaxRememberedDiscards = 64;

    // Runs the user discarded, so their own terminal events cannot resurrect them.
    //
    // THE BUG THIS FIXES: every event handler below synthesizes a row for a run it does not know, which is
    // right for a lossy stream — a queue silently missing an executing run is far worse than one extra row.
    // But a discarded run publishes its OWN closing events after the removal: cancelling a planning run
    // makes PlanAsync publish run-planned (with the cancellation as its error) and run-completed, and both
    // arrived at a queue that had just dropped the row and duly put it back. From the user's side the
    // Discard button cancelled the job and left the entry sitting there.
    private readonly HashSet<Guid> _discarded = [];
    private readonly Queue<Guid> _discardOrder = new();

    private void RememberDiscard(Guid runId)
    {
        if (!_discarded.Add(runId))
            return;
        _discardOrder.Enqueue(runId);
        while (_discardOrder.Count > MaxRememberedDiscards)
            _discarded.Remove(_discardOrder.Dequeue());
    }

    /// <summary>Stops filtering a run's events, for a discard that did not take. The run is still there, so
    /// its row must go back to updating rather than sitting frozen at whatever it last said. The id stays in
    /// <see cref="_discardOrder"/>, which only bounds the set — a stale entry there evicts something
    /// harmlessly early and nothing more.</summary>
    private void Forget(Guid runId) => _discarded.Remove(runId);

    /// <summary>Re-evaluates every finished row's age. Called on the shell's minute tick, so a row dims
    /// and its caption advances without the user touching anything.</summary>
    public void RefreshAges()
    {
        foreach (JobQueueRow row in Runs)
            row.RefreshAge();
    }

    /// <summary>Shows a parked run's plan on the Preview tab. The route by which a run this window did not
    /// plan becomes approvable — by being looked at first.</summary>
    [RelayCommand]
    private async Task ViewPlanAsync(JobQueueRow? row)
    {
        if (row is null || ViewPlan is null)
            return;
        await ViewPlan(row);
    }

    [RelayCommand]
    public void DismissError() => ErrorMessage = null;

    private JobQueueRow? FindRow(Guid runId)
    {
        foreach (JobQueueRow row in Runs)
            if (row.RunId == runId)
                return row;
        return null;
    }

    // Newest first, matching get-runs' own order and the activity feed's.
    private JobQueueRow Insert(JobQueueRow row)
    {
        Runs.Insert(0, row);
        return row;
    }

    private JobQueueRow RowFrom(RunSummaryDto run)
    {
        JobQueueRow row = new()
        {
            RunId = run.RunId,
            ProfileId = run.ProfileId,
            StartedAtUtc = run.StartedAtUtc,
            Time = time,
            ProfileName = run.ProfileName,
        };
        Apply(row, run);
        return row;
    }

    private void Apply(JobQueueRow row, RunSummaryDto run)
    {
        if (run.ProfileName.Length > 0)
            row.ProfileName = run.ProfileName;
        row.Phase = run.Phase;
        row.Outcome = run.Outcome;
        row.ClosedAtUtc = run.ClosedAtUtc;
        row.Paused = run.Paused;
        row.PlannedCopies = run.PlannedCopies;
        row.PlannedDeletes = run.PlannedDeletes;
        row.PlannedCopyBytes = run.PlannedCopyBytes;
        row.Completed = run.Succeeded + run.Skipped + run.Failed;
        row.Total = run.PlannedCopies;
        row.Deleted = run.Deleted;
        row.PlanError = run.PlanError;
        row.PlanWasShown = IsOwnRun?.Invoke(run.RunId) ?? false;
    }

    private void TrimAndFlag()
    {
        // Trim from the tail, which is the OLDEST — and prefer to drop finished rows, so a long session
        // never evicts a live run in favour of a closed one it happens to be newer than.
        while (Runs.Count > MaxRows)
        {
            int victim = -1;
            for (int i = Runs.Count - 1; i >= 0; i--)
            {
                if (Runs[i].IsFinished)
                {
                    victim = i;
                    break;
                }
            }
            Runs.RemoveAt(victim >= 0 ? victim : Runs.Count - 1);
        }

        int active = 0;
        foreach (JobQueueRow row in Runs)
            if (!row.IsFinished)
                active++;
        ActiveCount = active;
        HasRuns = Runs.Count > 0;
    }
}
