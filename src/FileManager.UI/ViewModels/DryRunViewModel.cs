using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

public sealed record DryRunTargetRow(string Path, DryRunTargetKind Kind, string? Detail)
{
    public string KindText => Kind switch
    {
        DryRunTargetKind.WouldWrite => "write",
        DryRunTargetKind.WouldOverwrite => "OVERWRITE",
        DryRunTargetKind.WouldRenameTo => "rename",
        DryRunTargetKind.WouldSkipConflict => "skip (conflict)",
        DryRunTargetKind.WouldSkipUnchanged => "skip (unchanged)",
        _ => "unknown",
    };

    public bool IsOverwrite => Kind == DryRunTargetKind.WouldOverwrite;
    public bool IsRename => Kind == DryRunTargetKind.WouldRenameTo;
    public bool IsWrite => Kind == DryRunTargetKind.WouldWrite;
    public bool IsSkip => Kind is DryRunTargetKind.WouldSkipConflict or DryRunTargetKind.WouldSkipUnchanged;
    public bool IsUnknown => Kind == DryRunTargetKind.Unknown;
}

public sealed record DryRunFileRow(
    string SourcePath,
    DryRunFileDisposition Disposition,
    string? DecidingFilter,
    string? SourceDisposition,
    IReadOnlyList<DryRunTargetRow> Targets)
{
    /// <summary>Anything other than keeping the source is destructive from the source's view.</summary>
    public bool IsSourceDisposalDestructive =>
        SourceDisposition is not null && SourceDisposition != nameof(Contracts.Profiles.OnSuccessAction.KeepSource);

    public bool HasDestructiveAction =>
        IsSourceDisposalDestructive || Targets.Any(t => t.IsOverwrite);

    public bool HasSourceDisposition => SourceDisposition is not null;
}

public sealed partial class DryRunViewModel : ViewModelBase
{
    private readonly IIpcGateway _gateway;
    private readonly IFolderPicker _folderPicker;
    private List<DryRunFileRow> _processAll = [];
    private List<DryRunFileRow> _filterSkipsAll = [];
    private List<DryRunFileRow> _unchangedSkipsAll = [];

    public DryRunViewModel(IIpcGateway gateway, IFolderPicker folderPicker)
    {
        _gateway = gateway;
        _folderPicker = folderPicker;
    }

    [ObservableProperty] public partial Guid? ProfileId { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "";
    [ObservableProperty] public partial string ScopePath { get; set; } = "";
    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool DestructiveOnly { get; set; }
    [ObservableProperty] public partial string GeneratedAtText { get; set; } = "";
    [ObservableProperty] public partial bool WasTruncated { get; set; }
    [ObservableProperty] public partial string TruncationNotice { get; set; } = "";

    // Blast-radius banner numbers (spec §8: deletions and overwrites are the report's whole point).
    [ObservableProperty] public partial int TotalFiles { get; set; }
    [ObservableProperty] public partial int ProcessCount { get; set; }
    [ObservableProperty] public partial int FilterSkipCount { get; set; }
    [ObservableProperty] public partial int UnchangedSkipCount { get; set; }
    [ObservableProperty] public partial int OverwriteCount { get; set; }
    [ObservableProperty] public partial int RenameCount { get; set; }
    [ObservableProperty] public partial int DisposalCount { get; set; }
    [ObservableProperty] public partial bool HasDestructiveActions { get; set; }

    public bool CanRun => ProfileId is not null;

    public ObservableCollection<DryRunFileRow> ProcessFiles { get; } = [];
    public ObservableCollection<DryRunFileRow> FilterSkips { get; } = [];
    public ObservableCollection<DryRunFileRow> UnchangedSkips { get; } = [];

    public void SetProfile(Guid? profileId, string profileName)
    {
        ProfileId = profileId;
        ProfileName = profileName;
        ScopePath = "";
        ClearReport();
        OnPropertyChanged(nameof(CanRun));
    }

    [RelayCommand(IncludeCancelCommand = true)]
    public async Task RunAsync(CancellationToken ct)
    {
        if (ProfileId is not Guid profileId)
            return;
        ErrorMessage = null;

        try
        {
            var run = await _gateway.DryRunAsync(
                profileId, string.IsNullOrWhiteSpace(ScopePath) ? null : ScopePath.Trim(), ct);
            if (run.IsCanceled)
            {
                Log.Debug("Dry run for profile {ProfileId} cancelled by the user", profileId);
                ErrorMessage = "Dry run cancelled.";
                return;
            }
            if (run.TryGetError(out IpcError? error))
            {
                // A transport drop carries no context of its own — point at the service log.
                ErrorMessage = error.Code == "IPC_TRANSPORT"
                    ? $"Dry run failed: {error.Message}. The connection to the background service was lost unexpectedly — see the service log in %LOCALAPPDATA%\\FileManager\\logs."
                    : $"Dry run failed: {error.Message}";
                return;
            }
            run.TryGetValue(out DryRunReport? report);
            ApplyReport(report!);
        }
        catch (OperationCanceledException)
        {
            // Defensive backstop — the gateway returns Canceled rather than throwing.
            Log.Debug("Dry run for profile {ProfileId} cancelled by the user", profileId);
            ErrorMessage = "Dry run cancelled.";
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a logged error banner instead of an
            // unobserved command fault.
            Log.Error(ex, "Dry run for profile {ProfileId} failed unexpectedly", profileId);
            ErrorMessage = $"Dry run failed unexpectedly: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task BrowseScopeAsync()
    {
        string? picked = await _folderPicker.PickFolderAsync("Limit the dry run to a folder (must be under a Source)");
        if (picked is not null)
            ScopePath = picked;
    }

    partial void OnDestructiveOnlyChanged(bool value) => RebuildVisibleRows();

    internal void ApplyReport(DryRunReport report)
    {
        List<DryRunFileRow> rows = report.Files.Select(static f => new DryRunFileRow(
            f.SourcePath,
            f.Disposition,
            f.DecidingFilter,
            f.SourceDisposition,
            f.Targets.Select(static t => new DryRunTargetRow(t.TargetPath, t.Kind, t.Detail)).ToList()))
            .ToList();

        _processAll = [];
        _filterSkipsAll = [];
        _unchangedSkipsAll = [];
        foreach (DryRunFileRow row in rows)
        {
            switch (row.Disposition)
            {
                case DryRunFileDisposition.WouldProcess: _processAll.Add(row); break;
                case DryRunFileDisposition.WouldSkipFilter: _filterSkipsAll.Add(row); break;
                case DryRunFileDisposition.WouldSkipUnchanged: _unchangedSkipsAll.Add(row); break;
            }
        }

        TotalFiles = rows.Count;
        ProcessCount = _processAll.Count;
        FilterSkipCount = _filterSkipsAll.Count;
        UnchangedSkipCount = _unchangedSkipsAll.Count;
        OverwriteCount = rows.Sum(static r => r.Targets.Count(static t => t.IsOverwrite));
        RenameCount = rows.Sum(static r => r.Targets.Count(static t => t.IsRename));
        DisposalCount = rows.Count(static r => r.IsSourceDisposalDestructive);
        HasDestructiveActions = OverwriteCount > 0 || DisposalCount > 0;
        GeneratedAtText = $"Generated {report.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        WasTruncated = report.Truncated;
        TruncationNotice = report.Truncated
            ? $"Report truncated: showing the first {report.Files.Count:N0} files — the scan found more. Set a scope folder to narrow the simulation."
            : "";

        HasReport = true;
        RebuildVisibleRows();
    }

    private void RebuildVisibleRows()
    {
        Fill(ProcessFiles, DestructiveOnly ? _processAll.Where(static r => r.HasDestructiveAction) : _processAll);
        Fill(FilterSkips, DestructiveOnly ? [] : _filterSkipsAll);
        Fill(UnchangedSkips, DestructiveOnly ? [] : _unchangedSkipsAll);
    }

    private static void Fill(ObservableCollection<DryRunFileRow> collection, IEnumerable<DryRunFileRow> rows)
    {
        collection.Clear();
        foreach (DryRunFileRow row in rows)
            collection.Add(row);
    }

    private void ClearReport()
    {
        _processAll = [];
        _filterSkipsAll = [];
        _unchangedSkipsAll = [];
        ProcessFiles.Clear();
        FilterSkips.Clear();
        UnchangedSkips.Clear();
        HasReport = false;
        ErrorMessage = null;
        TotalFiles = ProcessCount = FilterSkipCount = UnchangedSkipCount = 0;
        OverwriteCount = RenameCount = DisposalCount = 0;
        HasDestructiveActions = false;
        GeneratedAtText = "";
        WasTruncated = false;
        TruncationNotice = "";
    }
}
