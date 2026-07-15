using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
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

public sealed record DryRunTargetRow(string DirPath, string FileName, string? TargetRoot, OperationKind Kind, string? Detail)
{
    /// <summary>The absolute resulting path, reconstructed on demand — rows share their directory
    /// string instead of each retaining a full path.</summary>
    public string Path => System.IO.Path.Join(DirPath, FileName);

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
    string DirPath,
    string FileName,
    string? SourceRoot,
    OperationKind Disposition,
    string? DecidingFilter,
    string? SourceDisposition,
    IReadOnlyList<DryRunTargetRow> Targets,
    long SizeBytes = 0,
    string? SourceCommonRoot = null)
{
    /// <summary>The absolute source path, reconstructed on demand (tooltips) — rows share their
    /// directory string instead of each retaining a full path.</summary>
    public string SourcePath => System.IO.Path.Join(DirPath, FileName);

    /// <summary>The directory shown under the file name, relative to the tab's common root. Computed
    /// on demand: the list is virtualized, so only realized rows ever pay for it — storing it per row
    /// costs tens of MB at the streamed-report cap.</summary>
    public string ParentDisplay => DryRunPaths.ParentDisplayFor(DirPath, SourceCommonRoot);

    /// <summary>The source file's size, human-readable (right-aligned on the row).</summary>
    public string SizeText => ByteSize.Format(SizeBytes);

    /// <summary>The single operation-kind pill shown in place of the (dropped) nested target list —
    /// the most consequential of this file's target operations. Null when the file has no targets.</summary>
    private OperationKind? PrimaryTargetKind =>
        Targets.Count == 0 ? null
        : Targets.Any(t => t.IsOverwrite) ? OperationKind.Overwrite
        : Targets.Any(t => t.IsRename) ? OperationKind.Rename
        : Targets.Any(t => t.IsWrite) ? OperationKind.New
        : Targets.Any(t => t.IsSkip) ? OperationKind.SkipConflict
        : OperationKind.Unknown;

    public bool HasTargetKind => PrimaryTargetKind is not null;
    public string PrimaryKindText => PrimaryTargetKind?.GetTitle() ?? "";
    public bool IsPrimaryOverwrite => PrimaryTargetKind == OperationKind.Overwrite;
    public bool IsPrimaryRename => PrimaryTargetKind == OperationKind.Rename;
    public bool IsPrimaryWrite => PrimaryTargetKind == OperationKind.New;
    public bool IsPrimarySkip => PrimaryTargetKind == OperationKind.SkipConflict;

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
        DryRunPaths.PathContains(DirPath, FileName, term)
        || Targets.Any(t => DryRunPaths.PathContains(t.DirPath, t.FileName, term));
}

/// <summary>How a destination-side file is affected in the Destinations tab. A display-oriented
/// projection of the destination <see cref="OperationKind"/>s (e.g. both New and a conflict Rename
/// present as <see cref="New"/>; the several skip/keep kinds collapse to <see cref="Untouched"/>).</summary>
public enum DestinationRowKind { Untouched, New, Overwritten, Deleted, Unknown }

/// <summary>One resulting destination path within a <see cref="DryRunDestinationRow"/> — a single
/// target a source file lands at (a grouped row can have several), or the sole entry of a row that
/// has no originating source (a Mirror deletion, a kept-around conflict original, a pre-existing
/// untouched file).</summary>
public sealed record DryRunDestinationEntry(
    string DirPath,
    string FileName,
    string TargetRoot,
    DestinationRowKind Kind,
    string? Detail,
    string? CommonRoot,
    long SizeBytes = 0)
{
    /// <summary>The absolute resulting path, reconstructed on demand (tooltips) — entries share
    /// their directory string instead of each retaining a full path.</summary>
    public string TargetPath => System.IO.Path.Join(DirPath, FileName);

    /// <summary>Directory relative to the tab's common root; computed on demand — only realized
    /// (visible) rows ever run this.</summary>
    public string ParentDisplay => DryRunPaths.ParentDisplayFor(DirPath, CommonRoot);

    public string RelativeDisplay => ParentDisplay + FileName;

    /// <summary>The resulting file's size, human-readable: the incoming content for a write, or the
    /// existing file for an untouched/deleted entry.</summary>
    public string SizeText => ByteSize.Format(SizeBytes);

    public bool IsUntouched => Kind == DestinationRowKind.Untouched;
    public bool IsNew => Kind == DestinationRowKind.New;
    public bool IsOverwritten => Kind == DestinationRowKind.Overwritten;
    public bool IsDeleted => Kind == DestinationRowKind.Deleted;
    public bool IsUnknown => Kind == DestinationRowKind.Unknown;

    public string StatusText => Kind switch
    {
        DestinationRowKind.Untouched => "Untouched",
        DestinationRowKind.New => "New",
        DestinationRowKind.Overwritten => "Overwritten",
        DestinationRowKind.Deleted => "Deleted",
        _ => "Unknown",
    };

    public bool Matches(string term) => DryRunPaths.PathContains(DirPath, FileName, term);
}

/// <summary>A row in the Destinations tab: a source file and every destination it fans out to
/// (replication collapses to one row with a list of destinations instead of one row per target).
/// Rows with no originating source (<see cref="HasSource"/> false) carry a single
/// <see cref="DryRunDestinationEntry"/> and render as a plain single-status row.</summary>
public sealed record DryRunDestinationRow(
    string? SourceDirPath,
    string? SourceFileName,
    string? SourceRoot,
    string? SourceCommonRoot,
    IReadOnlyList<DryRunDestinationEntry> Destinations)
{
    /// <summary>The absolute originating-source path, reconstructed on demand; null for rows with no
    /// source (Mirror deletions, kept-around originals, pre-existing untouched files).</summary>
    public string? SourcePath =>
        SourceFileName is null ? null : System.IO.Path.Join(SourceDirPath!, SourceFileName);

    /// <summary>The source file's name, or the sole entry's name for a no-source row (its
    /// <see cref="Primary"/> target path IS the row's identity).</summary>
    public string FileName => SourceFileName ?? Primary.FileName;

    /// <summary>"parent-dir\name" of the originating source, relative to the Sources tab's common
    /// root; "" for no-source rows. Computed on demand — only realized rows run this.</summary>
    public string SourceDisplay =>
        SourceFileName is null ? "" :
        DryRunPaths.ParentDisplayFor(SourceDirPath!, SourceCommonRoot) + SourceFileName;

    public bool HasSource => SourceFileName is not null;

    /// <summary>True for rows with no source — rendered with the simple filename + status + folder
    /// layout (their single entry is <see cref="Primary"/>).</summary>
    public bool IsSingle => !HasSource;

    /// <summary>The sole entry of a no-source row (also the first entry generally); the simple
    /// template binds its status and folder off this.</summary>
    public DryRunDestinationEntry Primary => Destinations[0];

    public bool Matches(string term) =>
        (SourceFileName is not null && DryRunPaths.PathContains(SourceDirPath!, SourceFileName, term))
        || Destinations.Any(d => d.Matches(term));
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
/// rendered as coloured summary pills. The forest is derived from the directory structure the rows
/// already share (their directory strings reference the report's materialized directory table), so
/// building it is cheap even at the streamed cap; TreeDataGrid flattens the forest into a single
/// virtualized row list and only realizes the rows currently in view, however deep or expanded the
/// tree is.</summary>
public sealed partial class DryRunTreeNode : ObservableObject
{
    private static readonly char[] Separators = ['\\', '/'];

    // Directory nodes store their absolute path; leaves hold a reference to their directory's
    // shared string and reconstruct on demand — FullPath is only ever read by the tooltip and
    // CollectExpanded, and per-leaf absolute paths alone are hundreds of MB at the streamed cap.
    private readonly string? _dirFullPath;      // directory nodes only
    private readonly string? _parentDirPath;    // leaves only — the row's shared directory string
    private List<DryRunTreeNode>? _children;    // created on first child; leaves stay null
    private int[]? _counts;                     // per-spec counts, released once the pills are built
    private long _sizeBytes;

    private DryRunTreeNode(string name, string? dirFullPath, string? parentDirPath, bool isDirectory, int depth)
    {
        Name = name;
        _dirFullPath = dirFullPath;
        _parentDirPath = parentDirPath;
        IsDirectory = isDirectory;
        Depth = depth;
    }

    public string Name { get; }

    /// <summary>The absolute path (the tooltip, and the expand-state key across rebuilds) — stored
    /// for directory nodes, reconstructed on demand for leaves.</summary>
    public string FullPath => _dirFullPath ?? System.IO.Path.Join(_parentDirPath, Name);

    public bool IsDirectory { get; }
    public int Depth { get; }
    public IReadOnlyList<DryRunTreeNode> Children => (IReadOnlyList<DryRunTreeNode>?)_children ?? [];

    /// <summary>Expand/collapse state, bound two-way to the TreeDataGrid expander column's
    /// <c>IsExpandedBinding</c>, so a rebuild can restore what the user had open.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public bool HasChildren => _children is { Count: > 0 };

    /// <summary>Rolled-up total byte size of everything beneath this node.</summary>
    public long SizeBytes => _sizeBytes;

    /// <summary>The rolled-up size, human-readable (shown right-aligned beside the node's pills).</summary>
    public string SizeText => ByteSize.Format(_sizeBytes);

    /// <summary>The rolled-up summary pills, in the tree's declared spec order. Built after the
    /// whole forest is assembled.</summary>
    public IReadOnlyList<TreePill> Pills { get; private set; } = [];

    /// <summary>Builds the forest from arbitrary (directory, file name) rows. Rows share their
    /// directory string instances (references into the report's materialized directory table), so
    /// the directory chain is split and its nodes created once per DISTINCT directory, with the
    /// segments taken relative to <paramref name="commonRoot"/> so the top level is the first folder
    /// the panel actually cares about rather than the drive letter (<paramref name="commonRoot"/>
    /// null falls back to the full path, e.g. when roots span drives). Each row then costs two
    /// dictionary lookups: its directory's node and its leaf (rows at duplicate paths merge). Every
    /// node's <see cref="FullPath"/> stays absolute for the tooltip. Each leaf accumulates its
    /// (kind, count) increments and size locally; one post-order pass rolls the totals up the
    /// ancestor chain and renders pills in <paramref name="specs"/> order for every kind with a
    /// non-zero count. Top-level nodes start expanded so the tree opens on something to see.</summary>
    public static IReadOnlyList<DryRunTreeNode> BuildForest<T>(
        IEnumerable<T> rows,
        Func<T, string> dirSelector,
        Func<T, string> nameSelector,
        Func<T, IReadOnlyList<(string Kind, int Increment)>> categorizer,
        IReadOnlyList<TreePillSpec> specs,
        string? commonRoot = null,
        IReadOnlySet<string>? expandedPaths = null,
        Func<T, long>? sizeSelector = null)
    {
        List<DryRunTreeNode> roots = [];
        Dictionary<string, DryRunTreeNode> rootIndex = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<DryRunTreeNode, Dictionary<string, DryRunTreeNode>> childIndex = [];
        // One entry per distinct directory string; a null value means the directory IS the common
        // root, so its files sit at forest-root level.
        Dictionary<string, DryRunTreeNode?> dirNodeByPath = new(StringComparer.OrdinalIgnoreCase);
        string? rootPrefix = string.IsNullOrEmpty(commonRoot) ? null : commonRoot.TrimEnd('\\', '/');

        Dictionary<string, int> specIndex = new(specs.Count, StringComparer.Ordinal);
        for (int k = 0; k < specs.Count; k++)
            specIndex[specs[k].Kind] = k;

        foreach (T row in rows)
        {
            string dirPath = dirSelector(row);
            if (!dirNodeByPath.TryGetValue(dirPath, out DryRunTreeNode? dirNode))
                dirNodeByPath[dirPath] = dirNode = ResolveDirectory(dirPath);

            string fileName = nameSelector(row);
            Dictionary<string, DryRunTreeNode> index = dirNode is null ? rootIndex : IndexOf(dirNode);
            if (!index.TryGetValue(fileName, out DryRunTreeNode? leaf))
            {
                leaf = new DryRunTreeNode(fileName, dirFullPath: null, dirPath, isDirectory: false,
                    depth: dirNode is null ? 0 : dirNode.Depth + 1);
                (dirNode is null ? roots : dirNode._children ??= []).Add(leaf);
                index[fileName] = leaf;
            }

            // Increments and size land on the leaf only; FinishRecursive rolls them up afterwards —
            // O(rows + nodes) instead of per-row walks of the whole ancestor chain. Kinds absent
            // from the specs are dropped, exactly as pills always rendered only spec kinds.
            foreach ((string kind, int increment) in categorizer(row))
            {
                if (specIndex.TryGetValue(kind, out int k))
                    (leaf._counts ??= new int[specs.Count])[k] += increment;
            }
            leaf._sizeBytes += sizeSelector?.Invoke(row) ?? 0;
        }

        SortRecursive(roots);
        FinishRecursive(roots, specs, [], []);
        foreach (DryRunTreeNode root in roots)
            root._counts = null;
        return roots;

        Dictionary<string, DryRunTreeNode> IndexOf(DryRunTreeNode node)
        {
            if (!childIndex.TryGetValue(node, out Dictionary<string, DryRunTreeNode>? index))
                childIndex[node] = index = new(StringComparer.OrdinalIgnoreCase);
            return index;
        }

        // Splits and walks one directory chain, creating any missing nodes — runs once per distinct
        // directory (~the directory-table size), not once per row; the absolute FullPath prefixes
        // are built only here.
        DryRunTreeNode? ResolveDirectory(string dirPath)
        {
            string relative = DryRunPaths.RelativeForTree(dirPath, commonRoot);
            if (relative == ".")
                return null;   // the directory IS the common root
            string[] segments = relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return null;

            // FullPath stays absolute: seed the running prefix with the common root when the path
            // was taken relative to it; otherwise (fallback to the full path) start empty.
            string prefix = ReferenceEquals(relative, dirPath) ? "" : rootPrefix ?? "";
            List<DryRunTreeNode> level = roots;
            Dictionary<string, DryRunTreeNode> index = rootIndex;
            DryRunTreeNode? node = null;
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                prefix = prefix.Length == 0 ? segment : $"{prefix}\\{segment}";

                if (!index.TryGetValue(segment, out node))
                {
                    // Restore the user's prior expand/collapse state (keyed by absolute path) across
                    // rebuilds; on the first build (no prior state) top-level nodes open by default.
                    bool expanded = expandedPaths is null ? i == 0 : expandedPaths.Contains(prefix);
                    node = new DryRunTreeNode(segment, prefix, parentDirPath: null, isDirectory: true, i)
                    {
                        IsExpanded = expanded,
                    };
                    level.Add(node);
                    index[segment] = node;
                }

                level = node._children ??= [];
                index = IndexOf(node);
            }
            return node;
        }
    }

    /// <summary>Collects the absolute path of every expanded node in a forest so a rebuild can restore
    /// the user's expand/collapse state instead of snapping back to defaults on every keystroke.</summary>
    public static IReadOnlySet<string> CollectExpanded(IEnumerable<DryRunTreeNode> nodes)
    {
        HashSet<string> into = new(StringComparer.OrdinalIgnoreCase);
        Walk(nodes);
        return into;

        void Walk(IEnumerable<DryRunTreeNode> level)
        {
            foreach (DryRunTreeNode n in level)
            {
                if (n.IsExpanded)
                    into.Add(n.FullPath);
                Walk(n.Children);
            }
        }
    }

    /// <summary>Wraps a built forest in the <see cref="HierarchicalTreeDataGridSource{TModel}"/> that the
    /// view binds to. Columns use compiled lambda/expression selectors (no runtime model reflection, so
    /// it stays AOT/trim-clean) and reference the Name/Status cell templates by resource key, resolved
    /// from the view's resources at cell realization. The expander's <c>IsExpanded</c> selector is
    /// two-way, so user expand/collapse writes back to the node and survives a rebuild via
    /// <see cref="CollectExpanded"/>.</summary>
    public static HierarchicalTreeDataGridSource<DryRunTreeNode> BuildSource(IReadOnlyList<DryRunTreeNode> roots)
    {
        // Header-click sort comparers (the default view keeps BuildForest's dirs-first order). Name keeps
        // directories first in both directions; Size sorts on the raw byte count, not the formatted text;
        // the pills column is not meaningfully sortable.
        TemplateColumnOptions<DryRunTreeNode> nameOptions = new()
        {
            CompareAscending = (a, b) => CompareNodes(a, b, ascending: true),
            CompareDescending = (a, b) => CompareNodes(a, b, ascending: false),
        };
        TextColumnOptions<DryRunTreeNode> sizeOptions = new()
        {
            CompareAscending = static (a, b) => (a?.SizeBytes ?? 0).CompareTo(b?.SizeBytes ?? 0),
            CompareDescending = static (a, b) => (b?.SizeBytes ?? 0).CompareTo(a?.SizeBytes ?? 0),
        };
        TemplateColumnOptions<DryRunTreeNode> pillsOptions = new() { CanUserSortColumn = false };

        HierarchicalTreeDataGridSource<DryRunTreeNode> source = new(roots)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<DryRunTreeNode>(
                    new TemplateColumn<DryRunTreeNode>("Name", "DryRunNameCell",
                        width: new GridLength(1, GridUnitType.Star), options: nameOptions),
                    x => x.Children,
                    x => x.HasChildren,
                    x => x.IsExpanded),
                new TextColumn<DryRunTreeNode, string>("Size", x => x.SizeText, width: null, options: sizeOptions),
                new TemplateColumn<DryRunTreeNode>("Status", "DryRunPillsCell", options: pillsOptions),
            },
        };
        return source;
    }

    // Directories before files, then alphabetical — the same ordering BuildForest applies by default.
    private static int CompareNodes(DryRunTreeNode? a, DryRunTreeNode? b, bool ascending)
    {
        if (a is null || b is null) return 0;
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return ascending ? byName : -byName;
    }

    /// <summary>One post-order pass: rolls every node's counts and size up from its children, then
    /// renders its pills. Pills are memoized per (kind, count) — at the streamed cap the ~500k
    /// leaves are overwhelmingly the same "1 &lt;label&gt;" pill — and each node's count buffer is
    /// released as soon as its parent has consumed it.</summary>
    private static void FinishRecursive(
        List<DryRunTreeNode> nodes,
        IReadOnlyList<TreePillSpec> specs,
        Dictionary<(int Spec, int Count), TreePill> pillCache,
        Dictionary<TreePill, IReadOnlyList<TreePill>> singlePillLists)
    {
        foreach (DryRunTreeNode node in nodes)
        {
            if (node._children is { Count: > 0 } children)
            {
                FinishRecursive(children, specs, pillCache, singlePillLists);
                foreach (DryRunTreeNode child in children)
                {
                    if (child._counts is int[] childCounts)
                    {
                        int[] counts = node._counts ??= new int[specs.Count];
                        for (int k = 0; k < childCounts.Length; k++)
                            counts[k] += childCounts[k];
                        child._counts = null;
                    }
                    node._sizeBytes += child._sizeBytes;
                }
            }

            if (node._counts is not int[] own)
                continue;   // nothing categorized beneath — Pills stays empty
            List<TreePill>? pills = null;
            for (int k = 0; k < specs.Count; k++)
            {
                int count = own[k];
                if (count == 0)
                    continue;
                if (!pillCache.TryGetValue((k, count), out TreePill? pill))
                {
                    TreePillSpec spec = specs[k];
                    pillCache[(k, count)] = pill =
                        new TreePill($"{count:N0} {spec.Label}", spec.Background, spec.Foreground);
                }
                (pills ??= []).Add(pill);
            }
            if (pills is null)
                continue;
            if (pills.Count == 1)
            {
                // The dominant case (a leaf's single pill) shares one list per distinct pill.
                if (!singlePillLists.TryGetValue(pills[0], out IReadOnlyList<TreePill>? shared))
                    singlePillLists[pills[0]] = shared = [pills[0]];
                node.Pills = shared;
            }
            else
            {
                node.Pills = pills;
            }
        }
    }

    // Directories before files, then alphabetical — a familiar file-explorer ordering.
    private static void SortRecursive(List<DryRunTreeNode> nodes)
    {
        nodes.Sort(static (a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (DryRunTreeNode node in nodes)
        {
            if (node._children is { Count: > 0 } children)
                SortRecursive(children);
        }
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
    /// <summary>The key for a (directory, file name) pair — identical ordering to relativizing the
    /// joined absolute path, but <c>GetRelativePath</c> runs once per distinct (directory, root)
    /// instead of once per row: callers pass a per-rebuild cache and rows sharing a directory reuse
    /// its relativized form.</summary>
    public static string RelativeKey(
        string dirPath, string fileName, string? root,
        Dictionary<(string DirPath, string? Root), string> relDirCache)
    {
        if (string.IsNullOrEmpty(root))
            return System.IO.Path.Join(dirPath, fileName);
        if (!relDirCache.TryGetValue((dirPath, root), out string? relDir))
            relDirCache[(dirPath, root)] = relDir = System.IO.Path.GetRelativePath(root, dirPath);
        // "." — the file sits in the root itself, so the key is just the name (Join would prepend ".\").
        return relDir == "." ? fileName : System.IO.Path.Join(relDir, fileName);
    }
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

    /// <summary>The TreeDataGrid source the view binds to, rebuilt from <see cref="Tree"/> whenever the
    /// forest changes (null while empty so the grid shows nothing).</summary>
    [ObservableProperty] public partial HierarchicalTreeDataGridSource<DryRunTreeNode>? TreeSource { get; private set; }

    partial void OnTreeChanged(IReadOnlyList<DryRunTreeNode> value) =>
        TreeSource = value.Count == 0 ? null : DryRunTreeNode.BuildSource(value);
    [ObservableProperty] public partial IReadOnlyList<DryRunFacetRow> SourceFacets { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunFacetRow> DestinationFacets { get; private set; } = [];
    [ObservableProperty] public partial bool ShowSourceFacet { get; private set; }
    [ObservableProperty] public partial bool ShowDestinationFacet { get; private set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ShowTree { get; set; }

    /// <summary>The folder every displayed source path is shown relative to (null when the sources
    /// span drives, so full paths are shown). Surfaced to the user by <see cref="CommonRootDisplay"/>
    /// and used to root the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommonRootDisplay))]
    public partial string? CommonRoot { get; private set; }

    public string CommonRootDisplay =>
        CommonRoot is { Length: > 0 } root ? $"Relative to  {root}" : "Multiple drives — showing full paths";

    // Summary counts over the whole run (not the filtered view).
    [ObservableProperty] public partial int UntouchedCount { get; private set; }
    [ObservableProperty] public partial int ProcessedCount { get; private set; }
    [ObservableProperty] public partial int DeletedCount { get; private set; }

    public void Load(IReadOnlyList<DryRunFileRow> rows, string? commonRoot)
    {
        _applying = true;
        _all = rows.ToList();
        CommonRoot = commonRoot;
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
        CommonRoot = null;
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
        Dictionary<(string, string?), string> relDirCache = [];
        _visible = rows
            .Select(r => (Row: r, Key: DryRunSort.RelativeKey(r.DirPath, r.FileName, r.SourceRoot, relDirCache)))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Row.SourceRoot ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Row)
            .ToList();
        VisibleRows = _visible;
        if (ShowTree) Tree = BuildTree(_visible);
    }

    private IReadOnlyList<DryRunTreeNode> BuildTree(IReadOnlyList<DryRunFileRow> rows) =>
        DryRunTreeNode.BuildForest(rows, static r => r.DirPath, static r => r.FileName, static r =>
        {
            List<(string, int)> cats = [];
            if (r.IsUntouched) cats.Add(("untouched", 1));
            if (r.IsProcessed) cats.Add(("processed", 1));
            if (r.IsDeleted) cats.Add(("deleted", 1));
            return cats;
            // A prior forest (Tree non-empty) → restore its expansion; the very first build → null so
            // the BuildForest default (top level expanded) applies.
        }, TreeSpecs, CommonRoot, Tree.Count > 0 ? DryRunTreeNode.CollectExpanded(Tree) : null,
           static r => r.SizeBytes);

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

    /// <summary>The TreeDataGrid source the view binds to, rebuilt from <see cref="Tree"/> whenever the
    /// forest changes (null while empty so the grid shows nothing).</summary>
    [ObservableProperty] public partial HierarchicalTreeDataGridSource<DryRunTreeNode>? TreeSource { get; private set; }

    partial void OnTreeChanged(IReadOnlyList<DryRunTreeNode> value) =>
        TreeSource = value.Count == 0 ? null : DryRunTreeNode.BuildSource(value);
    [ObservableProperty] public partial IReadOnlyList<DryRunFacetRow> SourceFacets { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunFacetRow> DestinationFacets { get; private set; } = [];
    [ObservableProperty] public partial bool ShowSourceFacet { get; private set; }
    [ObservableProperty] public partial bool ShowDestinationFacet { get; private set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ShowTree { get; set; }

    /// <summary>The folder every displayed destination path is shown relative to (null when the
    /// targets span drives). See <see cref="DryRunSourcesTab.CommonRoot"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommonRootDisplay))]
    public partial string? CommonRoot { get; private set; }

    public string CommonRootDisplay =>
        CommonRoot is { Length: > 0 } root ? $"Relative to  {root}" : "Multiple drives — showing full paths";

    [ObservableProperty] public partial int UntouchedCount { get; private set; }
    [ObservableProperty] public partial int NewCount { get; private set; }
    [ObservableProperty] public partial int OverwrittenCount { get; private set; }
    [ObservableProperty] public partial int DeletedCount { get; private set; }

    public void Load(IReadOnlyList<DryRunDestinationRow> rows, string? commonRoot)
    {
        _applying = true;
        _all = rows.ToList();
        CommonRoot = commonRoot;
        // Counts are over the individual destination entries (one per resulting path), not the grouped
        // rows, so replicating one file to N targets still counts as N destinations.
        List<DryRunDestinationEntry> entries = _all.SelectMany(r => r.Destinations).ToList();
        UntouchedCount = entries.Count(e => e.IsUntouched);
        NewCount = entries.Count(e => e.IsNew);
        OverwrittenCount = entries.Count(e => e.IsOverwritten);
        DeletedCount = entries.Count(e => e.IsDeleted);

        SourceFacets = DryRunFacets.Build(CountBy(_all, r => r.SourceRoot ?? NoSourceKey), OnFacetChanged);
        ShowSourceFacet = SourceFacets.Count > 0;
        DestinationFacets = DryRunFacets.Build(
            CountByMany(_all, r => r.Destinations.Select(d => d.TargetRoot).Distinct(StringComparer.OrdinalIgnoreCase)),
            OnFacetChanged);
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
        CommonRoot = null;
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

        // Filter at the destination-entry level and drop rows left with nothing, so a grouped row
        // shows only the destinations that survived the destination-facet / search filters. The source
        // facet applies to the whole row; a search hit on the source path keeps all of its entries.
        List<DryRunDestinationRow> visible = [];
        foreach (DryRunDestinationRow row in _all)
        {
            if (sources is not null && !sources.Contains(row.SourceRoot ?? NoSourceKey))
                continue;

            IEnumerable<DryRunDestinationEntry> entries = row.Destinations;
            if (destinations is not null) entries = entries.Where(e => destinations.Contains(e.TargetRoot));
            if (term is not null
                && !(row.SourceFileName is not null && DryRunPaths.PathContains(row.SourceDirPath!, row.SourceFileName, term)))
                entries = entries.Where(e => e.Matches(term));

            List<DryRunDestinationEntry> kept = entries.ToList();
            if (kept.Count == 0)
                continue;
            visible.Add(kept.Count == row.Destinations.Count ? row : row with { Destinations = kept });
        }

        // Order by the source file's relative path (grouped rows) or the destination's (no-source rows)
        // so the preview stays comparable to the Sources tab.
        Dictionary<(string, string?), string> relDirCache = [];
        _visible = visible
            .Select(r => (Row: r, Key: r.HasSource
                ? DryRunSort.RelativeKey(r.SourceDirPath!, r.SourceFileName!, r.SourceRoot, relDirCache)
                : DryRunSort.RelativeKey(r.Primary.DirPath, r.Primary.FileName, r.Primary.TargetRoot, relDirCache)))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Row.SourceRoot ?? x.Row.Primary.TargetRoot, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Row)
            .ToList();
        VisibleRows = _visible;
        if (ShowTree) Tree = BuildTree(_visible);
    }

    private IReadOnlyList<DryRunTreeNode> BuildTree(IReadOnlyList<DryRunDestinationRow> rows) =>
        // Flatten grouped rows back to one leaf per resulting path — the tree is per-file, unchanged.
        DryRunTreeNode.BuildForest(rows.SelectMany(static r => r.Destinations), static e => e.DirPath, static e => e.FileName, static e =>
        {
            string kind = e.Kind switch
            {
                DestinationRowKind.Untouched => "untouched",
                DestinationRowKind.New => "new",
                DestinationRowKind.Overwritten => "overwritten",
                DestinationRowKind.Deleted => "deleted",
                _ => "unknown",
            };
            return new[] { (kind, 1) };
        }, TreeSpecs, CommonRoot, Tree.Count > 0 ? DryRunTreeNode.CollectExpanded(Tree) : null,
           static e => e.SizeBytes);

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

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Space preview: per-volume used / free / bounded-maximum, plus a per-target-root drill-down.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The byte/space picture for the whole run: global data-moved / net-change figures and one
/// <see cref="VolumeSpaceRow"/> per destination volume. Built from <see cref="SpaceProjection"/>;
/// absent (null on the root VM) when the report is truncated or carried no projection.</summary>
public sealed class DryRunSpaceViewModel
{
    public DryRunSpaceViewModel(SpaceProjection projection)
    {
        TransferredText = ByteSize.Format(projection.TotalBytesWritten);
        NetChangeText = SignedText(projection.TotalNetChangeBytes);
        Volumes = projection.Volumes
            .Select(v => new VolumeSpaceRow(v, projection.SafetyMarginBytes))
            .ToList();
    }

    public string TransferredText { get; }
    public string NetChangeText { get; }
    public IReadOnlyList<VolumeSpaceRow> Volumes { get; }

    /// <summary>A net change is signed for the reader: "+1.2 GB" grows the drives, "-400 MB" frees them.</summary>
    internal static string SignedText(long bytes) => bytes > 0 ? $"+{ByteSize.Format(bytes)}" : ByteSize.Format(bytes);
}

/// <summary>One destination volume's row: the storage-bar inputs, human-readable labels, and a
/// state-driven warning level. The four "used" figures are nested thresholds
/// (used ≤ settled ≤ peak ≤ ceiling), fed straight to <c>StorageBar</c>.</summary>
public sealed class VolumeSpaceRow
{
    public VolumeSpaceRow(VolumeSpaceEstimate v, long marginBytes)
    {
        VolumeRoot = v.VolumeRoot;
        CapacityKnown = v.CapacityKnown;

        CapacityBytes = v.TotalCapacityBytes;
        UsedNowBytes = v.UsedNowBytes;
        SettledBytes = v.SettledUsedBytes;
        RealisticPeakBytes = v.RealisticPeakUsedBytes;
        SafeCeilingBytes = v.SafeCeilingUsedBytes;
        MarginBytes = marginBytes;

        WrittenText = ByteSize.Format(v.BytesWrittenBytes);
        CapacityText = ByteSize.Format(v.TotalCapacityBytes);
        UsedNowText = ByteSize.Format(v.UsedNowBytes);
        CurrentFreeText = ByteSize.Format(v.FreeNowBytes);
        SettledUsedText = ByteSize.Format(v.SettledUsedBytes);
        PeakText = ByteSize.Format(v.RealisticPeakUsedBytes);
        CeilingText = ByteSize.Format(v.SafeCeilingUsedBytes);

        Folders = v.Folders
            .Select(f => new FolderSpaceRow(f))
            .ToList();

        // Warnings mirror the bar: red once the realistic peak crosses capacity − margin (likely
        // won't fit / very tight), amber once only the safe ceiling does (worst case gets close).
        long marginLine = v.TotalCapacityBytes - marginBytes;
        if (CapacityKnown && v.RealisticPeakUsedBytes > marginLine)
        {
            IsDanger = true;
            WarningText = "The peak usage may exceed the free space on this drive.";
        }
        else if (CapacityKnown && v.SafeCeilingUsedBytes > marginLine)
        {
            IsWarning = true;
            WarningText = "The worst-case usage is close to filling this drive.";
        }
    }

    public string VolumeRoot { get; }
    public bool CapacityKnown { get; }

    // StorageBar inputs (doubles for the control's styled properties).
    public double CapacityBytes { get; }
    public double UsedNowBytes { get; }
    public double SettledBytes { get; }
    public double RealisticPeakBytes { get; }
    public double SafeCeilingBytes { get; }
    public double MarginBytes { get; }

    public string WrittenText { get; }
    public string CapacityText { get; }
    public string UsedNowText { get; }
    public string CurrentFreeText { get; }
    public string SettledUsedText { get; }
    public string PeakText { get; }
    public string CeilingText { get; }

    public IReadOnlyList<FolderSpaceRow> Folders { get; }
    public bool HasFolders => Folders.Count > 0;

    public bool IsDanger { get; }
    public bool IsWarning { get; }
    public bool HasWarning => IsDanger || IsWarning;
    public string WarningText { get; } = "";
}

/// <summary>One target-root row in a volume's drill-down.</summary>
public sealed class FolderSpaceRow
{
    public FolderSpaceRow(FolderSpaceBreakdown f)
    {
        Root = f.Root;
        WrittenText = ByteSize.Format(f.BytesWrittenBytes);
        NetChangeText = DryRunSpaceViewModel.SignedText(f.NetChangeBytes);
        FileCountText = $"{f.FileCount:N0} file{(f.FileCount == 1 ? "" : "s")}";
    }

    public string Root { get; }
    public string WrittenText { get; }
    public string NetChangeText { get; }
    public string FileCountText { get; }
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial Guid? ProfileId { get; set; }
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

    /// <summary>The byte/space projection for this run, or null when none was computed (a truncated
    /// report, or a profile whose volumes could not be resolved). The Space panel binds its
    /// visibility to this being non-null.</summary>
    [ObservableProperty] public partial DryRunSpaceViewModel? Space { get; set; }

    public bool CanRun => ProfileId is not null;

    public void SetProfile(Guid? profileId, string profileName)
    {
        ProfileId = profileId;
        ProfileName = profileName;
        ClearReport();
        // CanRun re-raises via [NotifyPropertyChangedFor] on ProfileId — no manual notify needed.
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

    /// <summary>Shared by every source row with no destination ops (skipped files, ~2/3 of a typical
    /// report) — a fresh empty list per row is ~10 MB at the streamed cap.</summary>
    private static readonly IReadOnlyList<DryRunTargetRow> EmptyTargets = [];

    internal void ApplyReport(DryRunReport report)
    {
        // The one per-directory string allocation: every row references entries of this array, so
        // sibling files share their directory chain instead of each retaining a full path string.
        string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);

        // Destination ops grouped by the source file they carry content from (SourceIndex); the
        // rename-around Untouched and swept orphans have SourceIndex == -1 and so attach to no source.
        ILookup<int, DryRunOperation> destOpsBySource =
            report.DestinationOperations.ToLookup(o => o.SourceIndex);
        // One source op per source file, addressable by the file's index. Guard against a duplicate
        // SourceIndex (a service-side bug) degrading gracefully to the first op rather than throwing
        // and wiping the entire preview — mirrors the ToLookup used for destination ops above.
        Dictionary<int, DryRunOperation> sourceOpByIndex =
            report.SourceOperations
                .GroupBy(o => o.SourceIndex)
                .ToDictionary(g => g.Key, g => g.First());

        // The folder every displayed path is shown relative to, per panel: the single source/target
        // dir, else the common parent, else null (spanning drives → full paths). Distinct first so a
        // huge scan doesn't re-walk identical roots.
        string? sourceCommonRoot = DryRunPaths.CommonRoot(
            report.SourceFiles.Select(f => f.RootDirIndex).Distinct().Select(i => dirPaths[i]));
        string? destCommonRoot = DryRunPaths.CommonRoot(
            report.DestinationOperations.Select(o => o.RootDirIndex).Distinct().Select(i => dirPaths[i]));

        List<DryRunFileRow> fileRows = new(report.SourceFiles.Count);
        for (int i = 0; i < report.SourceFiles.Count; i++)
        {
            DryRunFile file = report.SourceFiles[i];
            sourceOpByIndex.TryGetValue(i, out DryRunOperation? op);
            List<DryRunTargetRow> targets = destOpsBySource[i]
                .Select(t => new DryRunTargetRow(dirPaths[t.DirIndex], t.FileName, dirPaths[t.RootDirIndex], t.Kind, t.Detail))
                .ToList();
            fileRows.Add(new DryRunFileRow(
                dirPaths[file.DirIndex],
                file.FileName,
                dirPaths[file.RootDirIndex],
                op?.Kind ?? OperationKind.Processed,
                op?.Detail,
                op?.SourceDisposition?.ToString(),
                targets.Count == 0 ? EmptyTargets : targets,
                file.Length,
                sourceCommonRoot));
        }

        // The byte size behind a destination op: the incoming content (its source file) for a write,
        // else the pre-existing file it touches (untouched / deleted / kept original), else nothing.
        long OpSize(DryRunOperation o) =>
            o.SourceIndex >= 0 && o.SourceIndex < report.SourceFiles.Count ? report.SourceFiles[o.SourceIndex].Length
            : o.SubjectIndex >= 0 && o.SubjectIndex < report.DestinationFiles.Count ? report.DestinationFiles[o.SubjectIndex].Length
            : 0;

        // The destination "after" view groups the server's destination operations by the source file
        // they carry content from: a file replicated to N targets is one row listing N destinations
        // (each keeping its own status), instead of N near-identical rows. Ops with SourceIndex == -1
        // (a pre-existing untouched file, a kept-around conflict original, a Mirror orphan) have no
        // originating source and become their own single-entry rows.
        DryRunDestinationEntry Entry(DryRunOperation o, long sizeBytes) =>
            new(dirPaths[o.DirIndex], o.FileName, dirPaths[o.RootDirIndex], MapDestinationKind(o.Kind), o.Detail, destCommonRoot, sizeBytes);

        List<DryRunDestinationRow> destRows = [];
        for (int i = 0; i < report.SourceFiles.Count; i++)
        {
            List<DryRunDestinationEntry> entries = destOpsBySource[i].Select(o => Entry(o, OpSize(o))).ToList();
            if (entries.Count == 0)
                continue;   // a filtered/unchanged source that produced no destination op
            DryRunFile file = report.SourceFiles[i];
            destRows.Add(new DryRunDestinationRow(
                dirPaths[file.DirIndex], file.FileName, dirPaths[file.RootDirIndex], sourceCommonRoot, entries));
        }
        foreach (DryRunOperation o in report.DestinationOperations.Where(o => o.SourceIndex < 0))
            destRows.Add(new DryRunDestinationRow(null, null, null, null, [Entry(o, OpSize(o))]));

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

        Space = report.Space is { Volumes.Count: > 0 } projection
            ? new DryRunSpaceViewModel(projection)
            : null;

        Sources.Load(fileRows, sourceCommonRoot);
        Destinations.Load(destRows, destCommonRoot);
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
        Space = null;
        TotalFiles = OverwriteCount = RenameCount = DisposalCount = 0;
        HasDestructiveActions = false;
        GeneratedAtText = "";
        WasTruncated = false;
        TruncationNotice = "";
    }
}
