using Avalonia.Media;
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

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Row / node models shared by the two tabs (Sources, Destinations).
// ─────────────────────────────────────────────────────────────────────────────────────────────

public sealed record DryRunTargetRow(string Path, string? TargetRoot, OperationKind Kind, string? Detail)
{
    public string KindText => Kind.GetTitle();

    public bool IsOverwrite => Kind == OperationKind.Overwrite;
    public bool IsRename => Kind == OperationKind.Rename;
    public bool IsWrite => Kind == OperationKind.New;
    public bool IsSkip => Kind is OperationKind.SkipConflict or OperationKind.SkipUnchanged;
    public bool IsUnknown => Kind == OperationKind.Unknown;
}

/// <summary>A source file in the Sources tab. Carries its own pills: Untouched (nothing happens to
/// the source — filtered out or unchanged), Processed, and Deleted (a destructive source
/// disposition). A processed-and-deleted file shows both the Processed and Deleted pills.</summary>
public sealed record DryRunFileRow(
    string SourcePath,
    string? SourceRoot,
    OperationKind Disposition,
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

    public bool IsProcessed => Disposition == OperationKind.Processed;
    public bool IsFilterSkipped => Disposition == OperationKind.SkippedByFilter;
    public bool IsUnchangedSkipped => Disposition == OperationKind.SkippedUnchanged;

    /// <summary>Untouched: nothing happens to this source file — it was filtered out or is unchanged.</summary>
    public bool IsUntouched => IsFilterSkipped || IsUnchangedSkipped;
    /// <summary>Deleted: the source would be removed (moved to trash/archive or permanently deleted).</summary>
    public bool IsDeleted => IsSourceDisposalDestructive;

    /// <summary>Distinct destination roots this file's targets land under — the Sources-tab
    /// "filter by destination" facet keys.</summary>
    public IReadOnlyList<string> TargetRoots => Targets
        .Where(t => t.TargetRoot is not null)
        .Select(t => t.TargetRoot!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Filter-skipped rows are visually de-emphasized by dimming the row's content (path +
    /// targets); the pills stay fully legible, so it binds this on the content only.</summary>
    public double ContentOpacity => IsFilterSkipped ? 0.55 : 1.0;

    public bool Matches(string term) =>
        SourcePath.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Targets.Any(t => t.Path.Contains(term, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How a destination-side file is affected in the Destinations tab. A display-oriented
/// projection of the destination <see cref="OperationKind"/>s (e.g. both New and a conflict Rename
/// present as <see cref="New"/>; the several skip/keep kinds collapse to <see cref="Untouched"/>).</summary>
public enum DestinationRowKind { Untouched, New, Overwritten, Deleted, Unknown }

/// <summary>A resulting destination file in the Destinations tab.</summary>
public sealed record DryRunDestinationRow(
    string TargetPath,
    string TargetRoot,
    string? SourceRoot,
    DestinationRowKind Kind,
    string? Detail)
{
    public bool IsUntouched => Kind == DestinationRowKind.Untouched;
    public bool IsNew => Kind == DestinationRowKind.New;
    public bool IsOverwritten => Kind == DestinationRowKind.Overwritten;
    public bool IsDeleted => Kind == DestinationRowKind.Deleted;
    public bool IsUnknown => Kind == DestinationRowKind.Unknown;

    public bool HasSource => SourceRoot is not null;

    public string StatusText => Kind switch
    {
        DestinationRowKind.Untouched => "Untouched",
        DestinationRowKind.New => "New",
        DestinationRowKind.Overwritten => "Overwritten",
        DestinationRowKind.Deleted => "Deleted",
        _ => "Unknown",
    };

    public bool Matches(string term) =>
        TargetPath.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (SourceRoot?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Tree: a single generic path-forest whose nodes carry rolled-up, colour-coded summary pills.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A colour palette shared by the tree-node pills. Mirrors the chip colours in
/// DryRunView.axaml so the tree and the list read the same.</summary>
internal static class DryRunPalette
{
    public static readonly IBrush Untouched = new SolidColorBrush(Color.Parse("#1E88E5")); // blue
    public static readonly IBrush Processed = new SolidColorBrush(Color.Parse("#2E7D32")); // green
    public static readonly IBrush New = Processed;
    public static readonly IBrush Overwritten = new SolidColorBrush(Color.Parse("#FFA000")); // amber
    public static readonly IBrush Deleted = new SolidColorBrush(Color.Parse("#E53935")); // red
    public static readonly IBrush Unknown = new SolidColorBrush(Color.Parse("#757575")); // grey
    public static readonly IBrush White = Brushes.White;
    public static readonly IBrush Black = Brushes.Black;
}

/// <summary>One rendered pill on a tree node.</summary>
public sealed record TreePill(string Text, IBrush Background, IBrush Foreground);

/// <summary>Declares a pill category for a tree: its label, colours, and the display order.</summary>
public sealed record TreePillSpec(string Kind, string Label, IBrush Background, IBrush Foreground);

/// <summary>A node in a path tree whose counts are the rolled-up totals of everything beneath it,
/// rendered as coloured summary pills. Building the forest over tens of thousands of paths is cheap
/// data work; the built-in TreeView virtualizes the realized rows and only materializes a node's
/// children once it is expanded.</summary>
public sealed partial class DryRunTreeNode : ObservableObject
{
    private static readonly char[] Separators = ['\\', '/'];

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

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

    /// <summary>Expand/collapse state, bound two-way to the built-in <c>TreeViewItem.IsExpanded</c>.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public bool HasChildren => Children.Count > 0;

    /// <summary>The rolled-up summary pills, in the tree's declared spec order. Built after the
    /// whole forest is assembled.</summary>
    public IReadOnlyList<TreePill> Pills { get; private set; } = [];

    /// <summary>Builds the top-level forest (one root per drive/UNC head) from arbitrary rows. Each
    /// leaf contributes one or more (kind, count) increments that roll up the whole ancestor chain;
    /// pills are then rendered in <paramref name="specs"/> order for every kind with a non-zero
    /// count. Top-level roots start expanded so the tree opens on something to see.</summary>
    public static IReadOnlyList<DryRunTreeNode> BuildForest<T>(
        IReadOnlyList<T> rows,
        Func<T, string> pathSelector,
        Func<T, IReadOnlyList<(string Kind, int Increment)>> categorizer,
        IReadOnlyList<TreePillSpec> specs)
    {
        List<DryRunTreeNode> roots = [];
        Dictionary<string, DryRunTreeNode> rootIndex = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<DryRunTreeNode, Dictionary<string, DryRunTreeNode>> childIndex = new();

        foreach (T row in rows)
        {
            IReadOnlyList<(string Kind, int Increment)> increments = categorizer(row);
            string[] segments = pathSelector(row).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
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

                // The leaf is one row; every ancestor contains it, so counts roll up the whole chain.
                foreach ((string kind, int increment) in increments)
                    node._counts[kind] = node._counts.GetValueOrDefault(kind) + increment;

                level = node.Children;
                if (!childIndex.TryGetValue(node, out index!))
                    childIndex[node] = index = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        SortRecursive(roots);
        BuildPillsRecursive(roots, specs);
        return roots;
    }

    private static void BuildPillsRecursive(List<DryRunTreeNode> nodes, IReadOnlyList<TreePillSpec> specs)
    {
        foreach (DryRunTreeNode node in nodes)
        {
            List<TreePill> pills = [];
            foreach (TreePillSpec spec in specs)
            {
                int count = node._counts.GetValueOrDefault(spec.Kind);
                if (count > 0)
                    pills.Add(new TreePill($"{count:N0} {spec.Label}", spec.Background, spec.Foreground));
            }
            node.Pills = pills;
            BuildPillsRecursive(node.Children, specs);
        }
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

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Facets (source / destination filters) shared by both tabs.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One checkable filter in a tab's source or destination facet. Toggling
/// <see cref="IsSelected"/> narrows which rows the tab shows — it never re-scans and never changes
/// the summary counts, which always reflect the complete run.</summary>
public sealed partial class DryRunFacetRow : ViewModelBase
{
    public required string Key { get; init; }
    public required int Count { get; init; }
    public string Label => $"{Key}  ({Count:N0})";

    [ObservableProperty] public partial bool IsSelected { get; set; } = true;
}

/// <summary>Ordering shared by both tabs so a file sits in the same position in the Sources and
/// Destinations lists — otherwise the differing root prefixes (e.g. <c>C:\src\…</c> vs
/// <c>D:\dst\…</c>) scatter the same logical file to different rows and defeat side-by-side
/// comparison. The key is the path relative to its root; rows are ordered by that, then by root.</summary>
internal static class DryRunSort
{
    public static string RelativeKey(string path, string? root) =>
        string.IsNullOrEmpty(root) ? path : System.IO.Path.GetRelativePath(root, path);
}

internal static class DryRunFacets
{
    /// <summary>Builds facet rows from per-key counts, sorted by key. Returns an empty list when
    /// there is nothing to distinguish (0 or 1 key) — a single value needs no filter.</summary>
    public static List<DryRunFacetRow> Build(
        IReadOnlyDictionary<string, int> countsByKey, PropertyChangedEventHandler onChanged)
    {
        if (countsByKey.Count <= 1)
            return [];
        List<DryRunFacetRow> facets = [];
        foreach (string key in countsByKey.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            DryRunFacetRow facet = new() { Key = key, Count = countsByKey[key] };
            facet.PropertyChanged += onChanged;
            facets.Add(facet);
        }
        return facets;
    }

    /// <summary>The selected keys to keep, or null when the facet is empty or everything is selected
    /// (the common case, which skips the per-row check entirely).</summary>
    public static HashSet<string>? SelectedKeys(IReadOnlyList<DryRunFacetRow> facets)
    {
        if (facets.Count == 0 || facets.All(f => f.IsSelected))
            return null;
        return facets.Where(f => f.IsSelected).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Tabs.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The Sources tab: every scanned source file, filterable by source and by destination,
/// as a flat list or a rolled-up path tree. Summary counts (Untouched / Processed / Deleted) always
/// reflect the whole run.</summary>
public sealed partial class DryRunSourcesTab : ViewModelBase
{
    private static readonly IReadOnlyList<TreePillSpec> TreeSpecs =
    [
        new("untouched", "untouched", DryRunPalette.Untouched, DryRunPalette.White),
        new("processed", "processed", DryRunPalette.Processed, DryRunPalette.White),
        new("deleted", "deleted", DryRunPalette.Deleted, DryRunPalette.White),
    ];

    private readonly TimeSpan _searchDebounce;
    private CancellationTokenSource? _searchCts;
    private bool _applying;
    private List<DryRunFileRow> _all = [];
    private List<DryRunFileRow> _visible = [];

    public DryRunSourcesTab(TimeSpan searchDebounce) => _searchDebounce = searchDebounce;

    [ObservableProperty] public partial IReadOnlyList<DryRunFileRow> VisibleRows { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunTreeNode> Tree { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunFacetRow> SourceFacets { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunFacetRow> DestinationFacets { get; private set; } = [];
    [ObservableProperty] public partial bool ShowSourceFacet { get; private set; }
    [ObservableProperty] public partial bool ShowDestinationFacet { get; private set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ShowTree { get; set; }

    // Summary counts over the whole run (not the filtered view).
    [ObservableProperty] public partial int UntouchedCount { get; private set; }
    [ObservableProperty] public partial int ProcessedCount { get; private set; }
    [ObservableProperty] public partial int DeletedCount { get; private set; }

    public void Load(IReadOnlyList<DryRunFileRow> rows)
    {
        _applying = true;
        _all = rows.ToList();
        UntouchedCount = _all.Count(r => r.IsUntouched);
        ProcessedCount = _all.Count(r => r.IsProcessed);
        DeletedCount = _all.Count(r => r.IsDeleted);

        SourceFacets = DryRunFacets.Build(CountBy(_all.Where(r => r.SourceRoot is not null), r => r.SourceRoot!), OnFacetChanged);
        ShowSourceFacet = SourceFacets.Count > 0;
        DestinationFacets = DryRunFacets.Build(CountByMany(_all, r => r.TargetRoots), OnFacetChanged);
        ShowDestinationFacet = DestinationFacets.Count > 0;

        SearchText = "";
        ShowTree = false;
        _applying = false;
        Rebuild();
    }

    public void Clear()
    {
        _applying = true;
        _all = [];
        _visible = [];
        VisibleRows = [];
        Tree = [];
        SourceFacets = DestinationFacets = [];
        ShowSourceFacet = ShowDestinationFacet = false;
        SearchText = "";
        ShowTree = false;
        UntouchedCount = ProcessedCount = DeletedCount = 0;
        _applying = false;
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_applying) return;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        _ = DebouncedRebuildAsync(_searchCts.Token);
    }

    partial void OnShowTreeChanged(bool value)
    {
        if (_applying) return;
        Tree = value ? BuildTree(_visible) : [];
    }

    private void OnFacetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        if (e.PropertyName == nameof(DryRunFacetRow.IsSelected))
            Rebuild();
    }

    private async Task DebouncedRebuildAsync(CancellationToken ct)
    {
        try
        {
            if (_searchDebounce > TimeSpan.Zero)
                await Task.Delay(_searchDebounce, ct);
            Rebuild();
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
        catch (Exception ex)
        {
            // Last resort: a rebuild fault becomes a logged error rather than an unobserved task fault.
            Log.Error(ex, "Failed to rebuild the Sources tab after a search change");
        }
    }

    private void Rebuild()
    {
        string? term = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        HashSet<string>? sources = DryRunFacets.SelectedKeys(SourceFacets);
        HashSet<string>? destinations = DryRunFacets.SelectedKeys(DestinationFacets);

        IEnumerable<DryRunFileRow> rows = _all;
        if (sources is not null) rows = rows.Where(r => r.SourceRoot is not null && sources.Contains(r.SourceRoot));
        if (destinations is not null) rows = rows.Where(r => r.TargetRoots.Any(destinations.Contains));
        if (term is not null) rows = rows.Where(r => r.Matches(term));

        // Order by path-relative-to-root (then root) so a file lines up with the same file in the
        // Destinations tab. The key is computed once per row rather than on every comparison.
        _visible = rows
            .Select(r => (Row: r, Key: DryRunSort.RelativeKey(r.SourcePath, r.SourceRoot)))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Row.SourceRoot ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Row)
            .ToList();
        VisibleRows = _visible;
        if (ShowTree) Tree = BuildTree(_visible);
    }

    private static IReadOnlyList<DryRunTreeNode> BuildTree(IReadOnlyList<DryRunFileRow> rows) =>
        DryRunTreeNode.BuildForest(rows, static r => r.SourcePath, static r =>
        {
            List<(string, int)> cats = [];
            if (r.IsUntouched) cats.Add(("untouched", 1));
            if (r.IsProcessed) cats.Add(("processed", 1));
            if (r.IsDeleted) cats.Add(("deleted", 1));
            return cats;
        }, TreeSpecs);

    private static Dictionary<string, int> CountBy<T>(IEnumerable<T> items, Func<T, string> key)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (T item in items)
            counts[key(item)] = counts.GetValueOrDefault(key(item)) + 1;
        return counts;
    }

    private static Dictionary<string, int> CountByMany<T>(IEnumerable<T> items, Func<T, IEnumerable<string>> keys)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (T item in items)
            foreach (string k in keys(item))
                counts[k] = counts.GetValueOrDefault(k) + 1;
        return counts;
    }
}

/// <summary>The Destinations tab: the resulting destination structure — files a run would add
/// (New), overwrite (Overwritten), leave in place (Untouched), or (under Mirror) delete (Deleted) —
/// filterable by source and by destination, as a flat list or a rolled-up path tree.</summary>
public sealed partial class DryRunDestinationsTab : ViewModelBase
{
    private static readonly IReadOnlyList<TreePillSpec> TreeSpecs =
    [
        new("untouched", "untouched", DryRunPalette.Untouched, DryRunPalette.White),
        new("new", "new", DryRunPalette.New, DryRunPalette.White),
        new("overwritten", "overwritten", DryRunPalette.Overwritten, DryRunPalette.Black),
        new("deleted", "deleted", DryRunPalette.Deleted, DryRunPalette.White),
        new("unknown", "unknown", DryRunPalette.Unknown, DryRunPalette.White),
    ];

    private const string NoSourceKey = "(no source)";

    private readonly TimeSpan _searchDebounce;
    private CancellationTokenSource? _searchCts;
    private bool _applying;
    private List<DryRunDestinationRow> _all = [];
    private List<DryRunDestinationRow> _visible = [];

    public DryRunDestinationsTab(TimeSpan searchDebounce) => _searchDebounce = searchDebounce;

    [ObservableProperty] public partial IReadOnlyList<DryRunDestinationRow> VisibleRows { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunTreeNode> Tree { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunFacetRow> SourceFacets { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunFacetRow> DestinationFacets { get; private set; } = [];
    [ObservableProperty] public partial bool ShowSourceFacet { get; private set; }
    [ObservableProperty] public partial bool ShowDestinationFacet { get; private set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ShowTree { get; set; }

    [ObservableProperty] public partial int UntouchedCount { get; private set; }
    [ObservableProperty] public partial int NewCount { get; private set; }
    [ObservableProperty] public partial int OverwrittenCount { get; private set; }
    [ObservableProperty] public partial int DeletedCount { get; private set; }

    public void Load(IReadOnlyList<DryRunDestinationRow> rows)
    {
        _applying = true;
        _all = rows.ToList();
        UntouchedCount = _all.Count(r => r.IsUntouched);
        NewCount = _all.Count(r => r.IsNew);
        OverwrittenCount = _all.Count(r => r.IsOverwritten);
        DeletedCount = _all.Count(r => r.IsDeleted);

        SourceFacets = DryRunFacets.Build(CountBy(_all, r => r.SourceRoot ?? NoSourceKey), OnFacetChanged);
        ShowSourceFacet = SourceFacets.Count > 0;
        DestinationFacets = DryRunFacets.Build(CountBy(_all, r => r.TargetRoot), OnFacetChanged);
        ShowDestinationFacet = DestinationFacets.Count > 0;

        SearchText = "";
        ShowTree = false;
        _applying = false;
        Rebuild();
    }

    public void Clear()
    {
        _applying = true;
        _all = [];
        _visible = [];
        VisibleRows = [];
        Tree = [];
        SourceFacets = DestinationFacets = [];
        ShowSourceFacet = ShowDestinationFacet = false;
        SearchText = "";
        ShowTree = false;
        UntouchedCount = NewCount = OverwrittenCount = DeletedCount = 0;
        _applying = false;
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_applying) return;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        _ = DebouncedRebuildAsync(_searchCts.Token);
    }

    partial void OnShowTreeChanged(bool value)
    {
        if (_applying) return;
        Tree = value ? BuildTree(_visible) : [];
    }

    private void OnFacetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        if (e.PropertyName == nameof(DryRunFacetRow.IsSelected))
            Rebuild();
    }

    private async Task DebouncedRebuildAsync(CancellationToken ct)
    {
        try
        {
            if (_searchDebounce > TimeSpan.Zero)
                await Task.Delay(_searchDebounce, ct);
            Rebuild();
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
        catch (Exception ex)
        {
            // Last resort: a rebuild fault becomes a logged error rather than an unobserved task fault.
            Log.Error(ex, "Failed to rebuild the Destinations tab after a search change");
        }
    }

    private void Rebuild()
    {
        string? term = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        HashSet<string>? sources = DryRunFacets.SelectedKeys(SourceFacets);
        HashSet<string>? destinations = DryRunFacets.SelectedKeys(DestinationFacets);

        IEnumerable<DryRunDestinationRow> rows = _all;
        if (sources is not null) rows = rows.Where(r => sources.Contains(r.SourceRoot ?? NoSourceKey));
        if (destinations is not null) rows = rows.Where(r => destinations.Contains(r.TargetRoot));
        if (term is not null) rows = rows.Where(r => r.Matches(term));

        // Same relative-to-root ordering as the Sources tab so the two previews line up row-for-row.
        _visible = rows
            .Select(r => (Row: r, Key: DryRunSort.RelativeKey(r.TargetPath, r.TargetRoot)))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Row.TargetRoot, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Row)
            .ToList();
        VisibleRows = _visible;
        if (ShowTree) Tree = BuildTree(_visible);
    }

    private static IReadOnlyList<DryRunTreeNode> BuildTree(IReadOnlyList<DryRunDestinationRow> rows) =>
        DryRunTreeNode.BuildForest(rows, static r => r.TargetPath, static r =>
        {
            string kind = r.Kind switch
            {
                DestinationRowKind.Untouched => "untouched",
                DestinationRowKind.New => "new",
                DestinationRowKind.Overwritten => "overwritten",
                DestinationRowKind.Deleted => "deleted",
                _ => "unknown",
            };
            return new[] { (kind, 1) };
        }, TreeSpecs);

    private static Dictionary<string, int> CountBy<T>(IEnumerable<T> items, Func<T, string> key)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (T item in items)
            counts[key(item)] = counts.GetValueOrDefault(key(item)) + 1;
        return counts;
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Root view-model: owns the run command, the global blast-radius banner, and the two tabs.
// ─────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class DryRunViewModel : ViewModelBase
{
    private readonly IIpcGateway _gateway;

    public DryRunViewModel(IIpcGateway gateway, TimeSpan? searchDebounce = null)
    {
        _gateway = gateway;
        TimeSpan debounce = searchDebounce ?? TimeSpan.FromMilliseconds(200);
        Sources = new DryRunSourcesTab(debounce);
        Destinations = new DryRunDestinationsTab(debounce);
    }

    public DryRunSourcesTab Sources { get; }
    public DryRunDestinationsTab Destinations { get; }

    [ObservableProperty] public partial Guid? ProfileId { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "";
    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial string GeneratedAtText { get; set; } = "";
    [ObservableProperty] public partial bool WasTruncated { get; set; }
    [ObservableProperty] public partial string TruncationNotice { get; set; } = "";

    // Blast-radius banner numbers (spec §8: deletions and overwrites are the report's whole point).
    [ObservableProperty] public partial int TotalFiles { get; set; }
    [ObservableProperty] public partial int OverwriteCount { get; set; }
    [ObservableProperty] public partial int RenameCount { get; set; }
    [ObservableProperty] public partial int DisposalCount { get; set; }
    [ObservableProperty] public partial bool HasDestructiveActions { get; set; }

    public bool CanRun => ProfileId is not null;

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

    internal void ApplyReport(DryRunReport report)
    {
        // Destination ops grouped by the source file they carry content from (SourceIndex); the
        // rename-around Untouched and swept orphans have SourceIndex == -1 and so attach to no source.
        ILookup<int, VirtualFileOperation> destOpsBySource =
            report.DestinationOperations.ToLookup(o => o.SourceIndex);
        // One source op per source file, addressable by the file's index.
        Dictionary<int, VirtualFileOperation> sourceOpByIndex =
            report.SourceOperations.ToDictionary(o => o.SourceIndex);

        List<DryRunFileRow> fileRows = new(report.SourceFiles.Count);
        for (int i = 0; i < report.SourceFiles.Count; i++)
        {
            PhysicalFile file = report.SourceFiles[i];
            sourceOpByIndex.TryGetValue(i, out VirtualFileOperation? op);
            List<DryRunTargetRow> targets = destOpsBySource[i]
                .Select(t => new DryRunTargetRow(t.Path, t.Root, t.Kind, t.Detail))
                .ToList();
            fileRows.Add(new DryRunFileRow(
                file.Path,
                file.Root,
                op?.Kind ?? OperationKind.Processed,
                op?.Detail,
                op?.SourceDisposition?.ToString(),
                targets));
        }

        // The destination "after" view maps 1:1 from the server's destination operations — no
        // client-side reconstruction. A New/Rename op's content comes from a source file, so its
        // SourceRoot resolves via SourceIndex; a pre-existing/orphan op has SourceIndex == -1 (→ null,
        // HasSource == false).
        List<DryRunDestinationRow> destRows = report.DestinationOperations.Select(o => new DryRunDestinationRow(
            o.Path,
            o.Root,
            o.SourceIndex >= 0 ? report.SourceFiles[o.SourceIndex].Root : null,
            MapDestinationKind(o.Kind),
            o.Detail))
            .ToList();

        TotalFiles = report.SourceFiles.Count;
        OverwriteCount = report.DestinationOperations.Count(static o => o.Kind == OperationKind.Overwrite);
        RenameCount = report.DestinationOperations.Count(static o => o.Kind == OperationKind.Rename);
        DisposalCount = fileRows.Count(static r => r.IsSourceDisposalDestructive);
        HasDestructiveActions = OverwriteCount > 0 || DisposalCount > 0
            || report.DestinationOperations.Any(static o => o.Kind == OperationKind.Deleted);

        GeneratedAtText = $"Generated {report.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        WasTruncated = report.Truncated;
        TruncationNotice = report.Truncated
            ? $"Report truncated: showing the first {report.SourceFiles.Count:N0} files — the scan found more. Destination deletions are not shown for a truncated report. Use the filters to narrow the view."
            : "";

        Sources.Load(fileRows);
        Destinations.Load(destRows);
        HasReport = true;
    }

    /// <summary>Projects a destination <see cref="OperationKind"/> onto the display-oriented
    /// <see cref="DestinationRowKind"/>. The server already split a conflict rename into a Rename op
    /// (a new file at the suffixed path) plus an Untouched op (the kept original), so both are mapped
    /// straight through.</summary>
    private static DestinationRowKind MapDestinationKind(OperationKind kind) => kind switch
    {
        OperationKind.New => DestinationRowKind.New,
        OperationKind.Overwrite => DestinationRowKind.Overwritten,
        OperationKind.Rename => DestinationRowKind.New,
        OperationKind.SkipConflict => DestinationRowKind.Untouched,
        OperationKind.SkipUnchanged => DestinationRowKind.Untouched,
        OperationKind.Untouched => DestinationRowKind.Untouched,
        OperationKind.Deleted => DestinationRowKind.Deleted,
        _ => DestinationRowKind.Unknown,
    };

    private void ClearReport()
    {
        Sources.Clear();
        Destinations.Clear();
        HasReport = false;
        ErrorMessage = null;
        TotalFiles = OverwriteCount = RenameCount = DisposalCount = 0;
        HasDestructiveActions = false;
        GeneratedAtText = "";
        WasTruncated = false;
        TruncationNotice = "";
    }
}
