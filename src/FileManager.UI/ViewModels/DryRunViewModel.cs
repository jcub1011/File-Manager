using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.UI.Extensions;
using FileManager.UI.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

public sealed record DryRunTargetRow(string Path, DryRunTargetKind Kind, string? Detail)
{
    public string KindText => Kind.GetTitle();

    public bool IsOverwrite => Kind == DryRunTargetKind.WouldOverwrite;
    public bool IsRename => Kind == DryRunTargetKind.WouldRenameTo;
    public bool IsWrite => Kind == DryRunTargetKind.WouldWrite;
    public bool IsSkip => Kind is DryRunTargetKind.WouldSkipConflict or DryRunTargetKind.WouldSkipUnchanged;
    public bool IsUnknown => Kind == DryRunTargetKind.Unknown;
}

public sealed record DryRunFileRow(
    string SourcePath,
    string? SourceRoot,
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

/// <summary>A node in a category's path tree: a directory or file whose counts are the rolled-up
/// totals of everything beneath it. Building the forest over 50k paths is cheap data work; the built-in
/// TreeView virtualizes the realized rows and only materializes a node's children once it is expanded.</summary>
public sealed partial class DryRunTreeNode : ObservableObject
{
    private static readonly char[] Separators = ['\\', '/'];

    private DryRunTreeNode(string name, string fullPath, bool isDirectory, int depth)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Depth = depth;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public int Depth { get; }
    public List<DryRunTreeNode> Children { get; } = [];

    /// <summary>Expand/collapse state, bound two-way to the built-in <c>TreeViewItem.IsExpanded</c>.
    /// Observable so the default-expanded roots (and any future programmatic change) reflect in the
    /// control.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public int FileCount { get; private set; }
    public int OverwriteCount { get; private set; }
    public int RenameCount { get; private set; }
    public int DisposalCount { get; private set; }

    public bool HasChildren => Children.Count > 0;

    // Display helpers for the tree row template (the TreeView draws chevrons + indentation natively).
    public string FileText => IsDirectory ? $"{FileCount:N0} files" : "";
    public bool HasOverwrites => OverwriteCount > 0;
    public bool HasRenames => RenameCount > 0;
    public bool HasDisposals => DisposalCount > 0;
    public string OverwriteText => $"{OverwriteCount:N0} overwrite";
    public string RenameText => $"{RenameCount:N0} rename";
    public string DisposalText => $"{DisposalCount:N0} disposal";

    /// <summary>Builds the top-level forest (one root per drive/UNC head) from the given file rows.
    /// Top-level roots start expanded so the tree opens on something other than a single collapsed line.</summary>
    public static IReadOnlyList<DryRunTreeNode> BuildForest(IReadOnlyList<DryRunFileRow> rows)
    {
        List<DryRunTreeNode> roots = [];
        Dictionary<string, DryRunTreeNode> rootIndex = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<DryRunTreeNode, Dictionary<string, DryRunTreeNode>> childIndex = new();

        foreach (DryRunFileRow row in rows)
        {
            int overwrites = 0, renames = 0;
            foreach (DryRunTargetRow target in row.Targets)
            {
                if (target.IsOverwrite) overwrites++;
                else if (target.IsRename) renames++;
            }
            int disposals = row.IsSourceDisposalDestructive ? 1 : 0;

            string[] segments = row.SourcePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue;

            List<DryRunTreeNode> level = roots;
            Dictionary<string, DryRunTreeNode> index = rootIndex;
            string prefix = "";
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                bool isDirectory = i < segments.Length - 1;
                prefix = prefix.Length == 0 ? segment : $"{prefix}\\{segment}";

                if (!index.TryGetValue(segment, out DryRunTreeNode? node))
                {
                    node = new DryRunTreeNode(segment, prefix, isDirectory, i) { IsExpanded = i == 0 };
                    level.Add(node);
                    index[segment] = node;
                }

                // The leaf is one file; every ancestor contains it, so counts roll up the whole chain.
                node.FileCount++;
                node.OverwriteCount += overwrites;
                node.RenameCount += renames;
                node.DisposalCount += disposals;

                level = node.Children;
                if (!childIndex.TryGetValue(node, out index!))
                    childIndex[node] = index = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        SortRecursive(roots);
        return roots;
    }

    // Directories before files, then alphabetical — a familiar file-explorer ordering.
    private static void SortRecursive(List<DryRunTreeNode> nodes)
    {
        nodes.Sort(static (a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (DryRunTreeNode node in nodes)
            SortRecursive(node.Children);
    }
}

/// <summary>One checkable Source in the dry-run report's focus facet. Toggling <see cref="IsSelected"/>
/// filters which sources' rows are shown in the detail lists/tree — it never re-scans and never changes
/// the blast-radius banner, which always reflects the complete run.</summary>
public sealed partial class SourceFacetRow : ViewModelBase
{
    public required string Root { get; init; }
    /// <summary>Would-process rows originating from this source (banner orientation, not a filtered count).</summary>
    public required int Count { get; init; }
    public string Label => $"{Root}  ({Count:N0})";

    [ObservableProperty] public partial bool IsSelected { get; set; } = true;
}

public sealed partial class DryRunViewModel : ViewModelBase
{
    private readonly IIpcGateway _gateway;
    private readonly TimeSpan _searchDebounce;
    private CancellationTokenSource? _searchCts;
    private bool _applyingReport;
    private List<DryRunFileRow> _processAll = [];
    private List<DryRunFileRow> _processMatches = [];
    private List<DryRunFileRow> _filterSkipsAll = [];
    private List<DryRunFileRow> _unchangedSkipsAll = [];

    public DryRunViewModel(IIpcGateway gateway, TimeSpan? searchDebounce = null)
    {
        _gateway = gateway;
        _searchDebounce = searchDebounce ?? TimeSpan.FromMilliseconds(200);
    }

    [ObservableProperty] public partial Guid? ProfileId { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "";
    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool DestructiveOnly { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
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

    // Reference-swapped rather than mutated in place: assigning a fresh list raises one
    // PropertyChanged and re-binds ItemsSource in a single pass, instead of ~50k CollectionChanged
    // events from a Clear()+Add loop that a non-virtualizing panel would materialize one at a time.
    [ObservableProperty] public partial IReadOnlyList<DryRunFileRow> ProcessFiles { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunFileRow> FilterSkips { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunFileRow> UnchangedSkips { get; private set; } = [];

    /// <summary>Per-source checkboxes for a multi-source report. Empty (and <see cref="ShowSourceFacet"/>
    /// false) for single-source reports, or when a legacy service omitted the per-row source root.</summary>
    [ObservableProperty] public partial IReadOnlyList<SourceFacetRow> SourceFacets { get; private set; } = [];
    [ObservableProperty] public partial bool ShowSourceFacet { get; private set; }

    // Per-category list⇄tree toggles. All default false, so every category opens as a list; flipping one
    // on shows that category's set as a navigable path tree — 50k rows collapse to a handful of directory
    // nodes with rolled-up counts to drill into. The built-in TreeView virtualizes the realized rows.
    [ObservableProperty] public partial bool ShowTree { get; set; }
    [ObservableProperty] public partial bool ShowFilterTree { get; set; }
    [ObservableProperty] public partial bool ShowUnchangedTree { get; set; }

    /// <summary>The root forest for each category's tree, bound to a built-in <c>TreeView</c>. Empty until
    /// that category's toggle is enabled; a node's children are only realized by the control on expand.</summary>
    [ObservableProperty] public partial IReadOnlyList<DryRunTreeNode> ProcessTree { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunTreeNode> FilterTree { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunTreeNode> UnchangedTree { get; private set; } = [];

    public void SetProfile(Guid? profileId, string profileName)
    {
        ProfileId = profileId;
        ProfileName = profileName;
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
            var run = await _gateway.DryRunAsync(profileId, ct);
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

    partial void OnDestructiveOnlyChanged(bool value)
    {
        if (_applyingReport) return;   // ApplyReport does exactly one rebuild at the end
        RebuildVisibleRows();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        _ = DebouncedRebuildAsync(_searchCts.Token);
    }

    // Each toggle builds its category's forest on enable and releases it on disable — a hidden tree can
    // hold a lot of nodes. The forest is built from the category's currently-visible (filtered) rows.
    partial void OnShowTreeChanged(bool value) =>
        ProcessTree = value ? DryRunTreeNode.BuildForest(_processMatches) : [];

    partial void OnShowFilterTreeChanged(bool value) =>
        FilterTree = value ? DryRunTreeNode.BuildForest(FilterSkips) : [];

    partial void OnShowUnchangedTreeChanged(bool value) =>
        UnchangedTree = value ? DryRunTreeNode.BuildForest(UnchangedSkips) : [];

    // A short pause after the last keystroke coalesces typing into one rebuild, so a 50k-row report's
    // list re-filter and tree rebuild don't run per character. Resumes on the UI thread (no
    // ConfigureAwait) per the §8 UI-thread-only mutation rule.
    private async Task DebouncedRebuildAsync(CancellationToken ct)
    {
        try
        {
            if (_searchDebounce > TimeSpan.Zero)
                await Task.Delay(_searchDebounce, ct);
            RebuildVisibleRows();
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
        catch (Exception ex)
        {
            // Last resort: a rebuild fault becomes a logged error rather than an unobserved task fault.
            Log.Error(ex, "Failed to rebuild dry-run rows after a search change");
        }
    }

    internal void ApplyReport(DryRunReport report)
    {
        List<DryRunFileRow> rows = report.Files.Select(static f => new DryRunFileRow(
            f.SourcePath,
            f.SourceRoot,
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
        // Auto-focus the review-worthy set: a run nobody can scroll through 50k rows of is only
        // reviewable if it opens on the overwrites/disposals (spec §8). Benign runs still show
        // everything. Full counts stay in the banner, and the toggle reveals the rest cheaply.
        // Suppress the change-handler's rebuild here; ApplyReport does exactly one at the end (below).
        _applyingReport = true;
        DestructiveOnly = HasDestructiveActions;
        _applyingReport = false;
        GeneratedAtText = $"Generated {report.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        WasTruncated = report.Truncated;
        TruncationNotice = report.Truncated
            ? $"Report truncated: showing the first {report.Files.Count:N0} files — the scan found more. Use the filter to narrow the view."
            : "";

        BuildSourceFacets(rows);

        HasReport = true;
        RebuildVisibleRows();
    }

    /// <summary>Builds the per-source focus checkboxes from the report. Only shown for a multi-source
    /// report (>1 distinct root); a single source needs no facet, and a legacy service that omits the
    /// per-row root (any null) can't be grouped reliably, so the facet stays hidden there too.</summary>
    private void BuildSourceFacets(IReadOnlyList<DryRunFileRow> rows)
    {
        List<SourceFacetRow> facets = [];
        bool anyNullRoot = false;
        List<string> roots = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (DryRunFileRow row in rows)
        {
            if (row.SourceRoot is null) { anyNullRoot = true; break; }
            if (seen.Add(row.SourceRoot))
                roots.Add(row.SourceRoot);
        }

        if (!anyNullRoot && roots.Count > 1)
        {
            Dictionary<string, int> processByRoot = new(StringComparer.OrdinalIgnoreCase);
            foreach (DryRunFileRow row in _processAll)
                if (row.SourceRoot is not null)
                    processByRoot[row.SourceRoot] = processByRoot.GetValueOrDefault(row.SourceRoot) + 1;

            roots.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                SourceFacetRow facet = new() { Root = root, Count = processByRoot.GetValueOrDefault(root) };
                facet.PropertyChanged += OnSourceFacetChanged;
                facets.Add(facet);
            }
        }

        SourceFacets = facets;
        ShowSourceFacet = facets.Count > 0;
    }

    private void OnSourceFacetChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The initial build assigns all-selected under _applyingReport; ApplyReport does the one rebuild.
        if (_applyingReport) return;
        if (e.PropertyName == nameof(SourceFacetRow.IsSelected))
            RebuildVisibleRows();
    }

    private void RebuildVisibleRows()
    {
        string? term = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        // Null when every source is selected (or the facet is hidden) — the common case skips the
        // per-row source check entirely. The banner counts are never touched: they always reflect
        // the complete run, so a source toggle only narrows the visible detail lists/tree.
        HashSet<string>? sources = SelectedSourceFilter();

        IEnumerable<DryRunFileRow> process = DestructiveOnly
            ? _processAll.Where(static r => r.HasDestructiveAction)
            : _processAll;
        if (sources is not null) process = process.Where(r => InSelectedSource(r, sources));
        // The list virtualizes, so bind every match — no truncation. The tree aggregates the same set.
        _processMatches = (term is null ? process : process.Where(r => Matches(r, term))).ToList();
        ProcessFiles = _processMatches;

        FilterSkips = Filter(DestructiveOnly ? [] : _filterSkipsAll, term, sources);
        UnchangedSkips = Filter(DestructiveOnly ? [] : _unchangedSkipsAll, term, sources);

        // Rebuild whichever category trees are currently shown so they track the filtered rows.
        if (ShowTree) ProcessTree = DryRunTreeNode.BuildForest(_processMatches);
        if (ShowFilterTree) FilterTree = DryRunTreeNode.BuildForest(FilterSkips);
        if (ShowUnchangedTree) UnchangedTree = DryRunTreeNode.BuildForest(UnchangedSkips);
    }

    /// <summary>The set of selected source roots to keep, or null when the facet is hidden or every
    /// source is selected (no filtering needed).</summary>
    private HashSet<string>? SelectedSourceFilter()
    {
        if (!ShowSourceFacet || SourceFacets.Count == 0 || SourceFacets.All(f => f.IsSelected))
            return null;
        return SourceFacets.Where(f => f.IsSelected)
            .Select(f => f.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool InSelectedSource(DryRunFileRow row, HashSet<string> sources) =>
        row.SourceRoot is not null && sources.Contains(row.SourceRoot);

    private static IReadOnlyList<DryRunFileRow> Filter(
        IEnumerable<DryRunFileRow> rows, string? term, HashSet<string>? sources)
    {
        if (sources is not null) rows = rows.Where(r => InSelectedSource(r, sources));
        if (term is not null) rows = rows.Where(r => Matches(r, term));
        return rows.ToList();
    }

    /// <summary>Case-insensitive substring match on the source path or any target path.</summary>
    private static bool Matches(DryRunFileRow row, string term) =>
        row.SourcePath.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.Targets.Any(t => t.Path.Contains(term, StringComparison.OrdinalIgnoreCase));

    private void ClearReport()
    {
        _processAll = [];
        _filterSkipsAll = [];
        _unchangedSkipsAll = [];
        _processMatches = [];
        ProcessFiles = [];
        FilterSkips = [];
        UnchangedSkips = [];
        ProcessTree = [];
        FilterTree = [];
        UnchangedTree = [];
        SourceFacets = [];
        ShowSourceFacet = false;
        HasReport = false;
        ErrorMessage = null;
        SearchText = "";
        TotalFiles = ProcessCount = FilterSkipCount = UnchangedSkipCount = 0;
        OverwriteCount = RenameCount = DisposalCount = 0;
        HasDestructiveActions = false;
        GeneratedAtText = "";
        WasTruncated = false;
        TruncationNotice = "";
    }
}
