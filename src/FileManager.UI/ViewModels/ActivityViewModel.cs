using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FileManager.UI.ViewModels;

/// <summary>One row of the activity feed. Unlike <see cref="ProfileListItem"/> — which is replaced
/// wholesale on refresh — a row is MUTATED in place as progress and completion events arrive for its
/// job, so it is an observable class rather than a record.
/// <para>Status glyph and colour are exposed as resource-key STRINGS resolved in XAML by
/// <see cref="Converters.IconConverters"/>, the same approach the dry-run rows use: the view model
/// keeps no Avalonia dependency, so its tests run without an Application.</para></summary>
public sealed partial class ActivityRow : ObservableObject
{
    /// <summary>Outcome value for a job that has started but not yet reported a terminal event. Not a
    /// wire value — the service only ever sends terminal outcomes.</summary>
    public const string RunningOutcome = "Running";

    public required Guid JobId { get; init; }
    public required Guid ProfileId { get; init; }
    public required string SourcePath { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>File name for the row's headline; the full path goes in the tooltip.</summary>
    public string FileName
    {
        get
        {
            string name = Path.GetFileName(SourcePath);
            return name.Length > 0 ? name : SourcePath;
        }
    }

    public string StartedText => StartedAtUtc.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>Resolved by the shell, which holds the profile list; the wire DTO carries only ids.</summary>
    [ObservableProperty]
    public partial string? ProfileName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusIconKey))]
    [NotifyPropertyChangedFor(nameof(StatusColorKey))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(HasResidualPaths))]
    public partial string Outcome { get; set; } = RunningOutcome;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? SkipReason { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Paths a failed rollback could not revert — the one outcome demanding user action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResidualPaths))]
    [NotifyPropertyChangedFor(nameof(ResidualPathsText))]
    public partial IReadOnlyList<string> ResidualPaths { get; set; } = [];

    [ObservableProperty]
    public partial string? DurationText { get; set; }

    /// <summary>Live phase/target caption from job-progress, or null once the job is terminal.</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    public bool IsRunning => Outcome == RunningOutcome;
    public bool HasResidualPaths => ResidualPaths.Count > 0;
    public string ResidualPathsText => string.Join(Environment.NewLine, ResidualPaths);

    /// <summary>Outcome arrives as a wire STRING, so this is a tolerant switch rather than a reuse of
    /// EnumTitleExtensions (whose GetTitle is constrained to real enums). An unrecognized value is
    /// shown verbatim instead of being mapped to something wrong.</summary>
    public string StatusText => Outcome switch
    {
        RunningOutcome => "Running",
        "Succeeded" => "Succeeded",
        "Skipped" => $"Skipped — {SkipReasonText}",
        "Failed" => "Failed",
        "RollbackFailed" => "Failed — rollback incomplete",
        _ => Outcome,
    };

    public string StatusIconKey => Outcome switch
    {
        RunningOutcome => "IconOverwrite",
        "Succeeded" => "IconCheckmark",
        "Skipped" => "IconSkip",
        "Failed" or "RollbackFailed" => "IconWarning",
        _ => "IconQuestion",
    };

    public string StatusColorKey => Outcome switch
    {
        RunningOutcome => "Brush.Info",
        "Succeeded" => "Brush.Success",
        "Skipped" => "Brush.Muted",
        "Failed" or "RollbackFailed" => "Brush.Danger",
        _ => "Brush.Muted",
    };

    private string SkipReasonText => SkipReason switch
    {
        "Filtered" => "filtered out",
        "SourceDisposed" => "source no longer there",
        "UnchangedAtAllTargets" => "already up to date",
        null or "" => "no reason given",
        _ => SkipReason,
    };
}

/// <summary>The in-GUI activity/error view (spec §7): recent jobs with success/skip/failure status and
/// drill-down into the per-job log. Live rows come from the engine event stream; because that stream is
/// lossy by design, <see cref="ReconcileAsync"/> re-seeds from get-recent-jobs on every (re)connect and
/// on panel open.</summary>
public sealed partial class ActivityViewModel(IIpcGateway gateway) : ViewModelBase
{
    /// <summary>Rows kept in the panel. The service's ring holds 500; the panel needs far less, and a
    /// bounded collection keeps the list virtualization-friendly.</summary>
    internal const int MaxRows = 200;

    /// <summary>How many jobs to request when re-seeding.</summary>
    internal const int ReconcileCount = 100;

    // Supersedes in-flight log fetches so a slow response cannot land on a newer selection
    // (same guard shape as DryRunViewModel's report epoch).
    private int _logEpoch;

    // The job whose log is currently shown. Reconcile rebuilds every row from the wire DTO, so the
    // selected job comes back as a NEW ActivityRow instance for the same JobId — which fired
    // OnSelectedJobChanged and re-fetched an identical log on every reconnect, Refresh and panel open.
    private Guid _loadedLogJobId;
    private bool _logLoaded;

    public ObservableCollection<ActivityRow> Jobs { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    public partial ActivityRow? SelectedJob { get; set; }

    /// <summary>Set when there is no log to show — a normal state for a job skipped before its journal
    /// opened, NOT an error.</summary>
    [ObservableProperty]
    public partial string? LogStatus { get; set; }

    /// <summary>Real failures only, so the danger banner stays meaningful.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>Informational engine notices (engine-warning, a folder run that matched nothing).</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial bool HasJobs { get; set; }

    /// <summary>Set by the shell so a row can show a profile name; the wire DTO carries only ids.</summary>
    public Func<Guid, string?>? ProfileNameLookup { get; set; }

    /// <summary>Set by the shell to its activity-panel toggle, so the panel's own close button works
    /// without reaching up the visual tree for the shell view model (mirrors
    /// <see cref="ProfileListViewModel.CreateProfileCommand"/>).</summary>
    public ICommand? HideCommand { get; set; }

    [RelayCommand]
    public async Task RefreshAsync() => await ReconcileAsync();

    /// <summary>Re-seeds the feed from the service's recent-jobs ring. Called on (re)connect and on
    /// panel open, because the event stream drops frames under back-pressure.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var result = await gateway.GetRecentJobsAsync(ReconcileCount, ct);
        if (result.IsCanceled)
            return;
        if (result.TryGetError(out IpcError? error))
        {
            // Leave the existing rows alone: a transient outage must not blank the panel.
            ErrorMessage = $"Could not load recent jobs: {error.Message}";
            return;
        }
        result.TryGetValue(out IReadOnlyList<JobSummaryDto>? jobs);
        ErrorMessage = null;
        Notice = null;   // a re-seed is a fresh picture; a stale notice must not outlive it

        // get-recent-jobs is the COMPLETED-job ring, so a naive full replace would delete rows the
        // response cannot know about. Keep EVERY row it does not mention — not just the running ones:
        // a job that finished while this request was in flight is answered from the ring BEFORE its
        // summary is recorded, yet its completion event can reach us first, and an IsRunning-only
        // filter dropped exactly that row. The ring holds 500 against our 100, so nothing accumulates.
        Guid selectedId = SelectedJob?.JobId ?? Guid.Empty;
        HashSet<Guid> fromService = [.. jobs!.Select(j => j.JobId)];
        List<ActivityRow> preserved = [.. Jobs.Where(r => !fromService.Contains(r.JobId))];

        // Preserved rows go FIRST. The collection is newest-first (both event handlers Insert(0, …)),
        // and these are the newest rows by construction — appending them put the live job below a
        // hundred finished ones, out of view, and made it the first casualty of the tail trim.
        Jobs.Clear();
        foreach (ActivityRow row in preserved)
            Jobs.Add(row);
        foreach (JobSummaryDto job in jobs!)
            Jobs.Add(RowFrom(job));
        TrimAndFlag();

        SelectedJob = Jobs.FirstOrDefault(r => r.JobId == selectedId);
    }

    /// <summary>A job entered the pipeline: prepend a live row.</summary>
    public void OnJobStarted(JobStartedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (Jobs.Any(r => r.JobId == evt.JobId))
            return;
        Jobs.Insert(0, new ActivityRow
        {
            JobId = evt.JobId,
            ProfileId = evt.ProfileId,
            SourcePath = evt.SourcePath,
            StartedAtUtc = evt.AtUtc,
            ProfileName = ProfileNameLookup?.Invoke(evt.ProfileId),
        });
        TrimAndFlag();
    }

    /// <summary>A job reached a terminal outcome. Mutates the live row in place so it does not jump in
    /// the list; falls back to prepending when the job-started frame was lost.</summary>
    public void OnJobFinished(JobSummaryDto job, string? error, IReadOnlyList<string>? residualPaths)
    {
        ArgumentNullException.ThrowIfNull(job);
        ActivityRow? row = Jobs.FirstOrDefault(r => r.JobId == job.JobId);
        if (row is null)
        {
            row = new ActivityRow
            {
                JobId = job.JobId,
                ProfileId = job.ProfileId,
                SourcePath = job.SourcePath,
                StartedAtUtc = job.StartedAtUtc,
                ProfileName = ProfileNameLookup?.Invoke(job.ProfileId),
            };
            Jobs.Insert(0, row);
        }
        row.Outcome = job.Outcome;
        row.SkipReason = job.SkipReason;
        row.Error = error;
        row.ResidualPaths = residualPaths ?? [];
        row.DurationText = FormatDuration(job.Duration);
        row.ProgressText = null;
        TrimAndFlag();
    }

    /// <summary>Advisory intra-job progress. A frame for a job we never saw start is ignored — it is
    /// not worth synthesizing a row from a lossy signal.</summary>
    public void OnProgress(JobProgressEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ActivityRow? row = Jobs.FirstOrDefault(r => r.JobId == evt.JobId);
        if (row is null || !row.IsRunning)
            return;
        row.ProgressText = evt.TargetCount > 0 && evt.Phase == JobPhase.Distributing
            ? $"{evt.Phase} · {evt.TargetsCompleted}/{evt.TargetCount} targets"
            : evt.Phase.ToString();
    }

    public void ShowNotice(string message) => Notice = message;

    /// <summary>Dismisses the notice bar. Without this the bar had no writer that ever cleared it, so
    /// one "nothing matched" message stayed on screen for the rest of the session.</summary>
    [RelayCommand]
    public void DismissNotice() => Notice = null;

    partial void OnSelectedJobChanged(ActivityRow? value) => _ = LoadLogSafeAsync(value);

    private async Task LoadLogSafeAsync(ActivityRow? row)
    {
        // Same job, already resolved once → nothing to fetch. A reconcile swapped the row INSTANCE, not
        // the selection, and re-fetching an identical log on every reconnect/Refresh/panel-open is pure
        // round-trip. A failed attempt leaves _logLoaded false, so the next selection change retries.
        if (row is not null && row.JobId == _loadedLogJobId && _logLoaded)
            return;

        int epoch = ++_logEpoch;
        _loadedLogJobId = row?.JobId ?? Guid.Empty;
        _logLoaded = false;
        LogLines.Clear();
        if (row is null)
        {
            LogStatus = null;
            return;
        }
        LogStatus = "Loading…";
        try
        {
            var result = await gateway.GetJobLogAsync(row.JobId);
            if (epoch != _logEpoch || result.IsCanceled)
                return;                                     // a newer selection won
            if (result.TryGetError(out IpcError? error))
            {
                // Jobs skipped before their journal opened never write a log file. That is normal — and
                // it is a settled answer, so it counts as loaded. Any OTHER code (a log that exists but
                // could not be read) is a real failure the user must see.
                bool absent = error.Code == "JOB_LOG_NOT_FOUND";
                LogStatus = absent
                    ? "No log for this job — it was skipped before any work began."
                    : null;
                if (!absent)
                    ErrorMessage = $"Could not load the job log: {error.Message}";
                _logLoaded = absent;
                return;
            }
            result.TryGetValue(out IReadOnlyList<string>? lines);
            foreach (string line in lines!)
                LogLines.Add(line);
            LogStatus = LogLines.Count == 0 ? "The job log is empty." : null;
            _logLoaded = true;
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): this runs fire-and-forget from a property change,
            // so it has no exception boundary of its own.
            Log.Error(ex, "Loading the job log for {JobId} failed", row.JobId);
            if (epoch == _logEpoch)
                ErrorMessage = $"Could not load the job log: {ex.Message}";
        }
    }

    private ActivityRow RowFrom(JobSummaryDto job) => new()
    {
        JobId = job.JobId,
        ProfileId = job.ProfileId,
        SourcePath = job.SourcePath,
        StartedAtUtc = job.StartedAtUtc,
        ProfileName = ProfileNameLookup?.Invoke(job.ProfileId),
        Outcome = job.Outcome,
        SkipReason = job.SkipReason,
        DurationText = FormatDuration(job.Duration),
    };

    private void TrimAndFlag()
    {
        while (Jobs.Count > MaxRows)
            Jobs.RemoveAt(Jobs.Count - 1);
        HasJobs = Jobs.Count > 0;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration < TimeSpan.FromSeconds(1)
            ? $"{duration.TotalMilliseconds:F0} ms"
            : $"{duration.TotalSeconds:F1} s";
}
