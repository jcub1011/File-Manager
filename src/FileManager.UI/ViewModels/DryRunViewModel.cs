using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
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

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  The row types are HANDLES: each stores a reference to the shared DryRunRowStore and one integer
//  position into it, and every property below is a column read. Two consequences worth knowing
//  before editing them.
//
//  Identity. Because a handle is a record over (store, index), two handles built for the same row
//  at different times are Equals — record equality is reference equality on the store plus an int
//  compare. That is load-bearing: the bound lists materialize a handle per indexer access, so
//  ListBox selection, IndexOf and container recycling all depend on it. Do NOT add a field that
//  joins the generated equality (a cached list, a command instance) — an earlier design was
//  rejected for exactly that (docs/dry-run-memory-optimization.md, Optimization 3).
//
//  Allocation. A handle is created per realization, not per file, so computed properties are the
//  right default and cheap here — but they run on the UI thread during scrolling, so keep them
//  O(this row's operations), never O(report).
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One destination operation carried out on behalf of a source file, addressed by its CSR
/// position in <see cref="DryRunRowStore"/>.</summary>
public sealed record DryRunTargetRow(DryRunRowStore Store, int Position)
{
    public string DirPath => Store.OpDirPath(Position);
    public string FileName => Store.OpFileName(Position);
    public string? TargetRoot => Store.OpRoot(Position);
    public OperationKind Kind => Store.OpKind(Position);
    public string? Detail => Store.OpDetail(Position);

    /// <summary>The absolute resulting path, reconstructed on demand — rows share their directory
    /// string instead of each retaining a full path.</summary>
    public string Path => System.IO.Path.Join(DirPath, FileName);

    public bool IsOverwrite => Kind == OperationKind.Overwrite;
    public bool IsRename => Kind == OperationKind.Rename;
    public bool IsWrite => Kind == OperationKind.New;
    public bool IsSkip => Kind is OperationKind.SkipConflict or OperationKind.SkipUnchanged;
}

/// <summary>A source file in the Sources tab. Carries its own pills: Untouched (nothing happens to
/// the source — filtered out or unchanged), Processed, and Deleted (a destructive source
/// disposition). A processed-and-deleted file shows both the Processed and Deleted pills.</summary>
public sealed partial record DryRunFileRow(DryRunRowStore Store, int Index) : IDryRunFileRow
{
    public string DirPath => Store.SourceDirPath(Index);
    public string FileName => Store.SourceFileName(Index);
    public string? SourceRoot => Store.SourceRoot(Index);
    public OperationKind Disposition => Store.SourceKind(Index);
    public string? DecidingFilter => Store.SourceDetail(Index);
    public string? SourceDisposition => Store.SourceDispositionText(Index);
    public long SizeBytes => Store.SourceSize(Index);
    public string? SourceCommonRoot => Store.SourceCommonRoot;

    /// <summary>This file's destination operations. Materialized on demand and bound nowhere — the
    /// row shows a single rolled-up status glyph (<see cref="PrimaryKindText"/>), not a nested list —
    /// so it exists for tests and for callers that want the operations as objects. The row's own
    /// pill, facet and search logic all read the store's CSR range directly instead, which is why a
    /// 500k-file preview allocates none of these.</summary>
    public IReadOnlyList<DryRunTargetRow> Targets
    {
        get
        {
            int start = Store.TargetStart(Index), end = Store.TargetEnd(Index);
            if (start == end)
                return [];
            DryRunTargetRow[] targets = new DryRunTargetRow[end - start];
            for (int p = start; p < end; p++)
                targets[p - start] = new DryRunTargetRow(Store, p);
            return targets;
        }
    }

    /// <summary>Clipboard/shell actions behind this row's right-click menu; set at build time, null
    /// in headless tests (the commands then no-op). An <c>init</c> property and NOT a positional
    /// member, so it stays out of the record's value equality — see the identity note above.</summary>
    public IDryRunItemActions? Actions { get; init; }

    // File right-click commands (the Sources list rows are always real source files). Names mirror
    // DryRunTreeNode so the shared file menu markup binds the same command names on either type.
    // These are computed get-only properties — NOT [ObservableProperty]/[RelayCommand] fields and
    // NOT cached in a field — so they never join this record's value equality (a cached command per
    // instance would break the presorted-rows dedup in DryRunRebuild.SameRows / the `with` copies).
    // A command is allocated only when the menu is opened; Open File / reveal grey out via a live
    // disk check evaluated when the right-click realizes the command.
    public IRelayCommand CopyPathCommand => new RelayCommand(() => Actions?.CopyText(SourcePath));
    public IRelayCommand CopyNameCommand => new RelayCommand(() => Actions?.CopyText(FileName));
    // Disabled for a name that is all extension (a dotfile like ".git" — stem is empty, so there is
    // nothing to copy).
    public IRelayCommand CopyNameWithoutExtensionCommand =>
        new RelayCommand(
            () => Actions?.CopyText(System.IO.Path.GetFileNameWithoutExtension(FileName)),
            () => System.IO.Path.GetFileNameWithoutExtension(FileName).Length > 0);
    public IRelayCommand OpenFileCommand =>
        new RelayCommand(() => Actions?.OpenFile(SourcePath), () => System.IO.File.Exists(SourcePath));
    public IRelayCommand RevealInExplorerCommand =>
        new RelayCommand(() => Actions?.RevealInExplorer(SourcePath), () => System.IO.File.Exists(SourcePath));

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
    /// the most consequential of this file's target operations. Null when the file has no targets.
    /// One pass over the row's CSR range, tracking the best kind seen, rather than the four
    /// <c>Any</c> scans it replaced: this runs per realized row while the user scrolls.</summary>
    private OperationKind? PrimaryTargetKind
    {
        get
        {
            int start = Store.TargetStart(Index), end = Store.TargetEnd(Index);
            if (start == end)
                return null;
            // Descending consequence: an Overwrite anywhere wins outright, so the scan can stop.
            OperationKind best = OperationKind.Unknown;
            int bestRank = 0;
            for (int p = start; p < end; p++)
            {
                OperationKind kind = Store.OpKind(p);
                int rank = kind switch
                {
                    OperationKind.Overwrite => 4,
                    OperationKind.Rename => 3,
                    OperationKind.New => 2,
                    OperationKind.SkipConflict or OperationKind.SkipUnchanged => 1,
                    _ => 0,
                };
                if (rank <= bestRank)
                    continue;
                bestRank = rank;
                // The skip kinds both present as SkipConflict, matching the previous IsSkip fold.
                best = rank == 1 ? OperationKind.SkipConflict : kind;
                if (rank == 4)
                    break;
            }
            return best;
        }
    }

    public bool HasTargetKind => PrimaryTargetKind is not null;
    public string PrimaryKindText => PrimaryTargetKind?.GetTitle() ?? "";

    /// <summary>Icon/colour resource keys for the single target-op status glyph (resolved in the view
    /// via IconConverters). Null when the file has no target operation.</summary>
    public string? PrimaryKindIconKey => PrimaryTargetKind switch
    {
        OperationKind.Overwrite => "IconOverwrite",
        OperationKind.Rename => "IconRename",
        OperationKind.New => "IconAdd",
        OperationKind.SkipConflict => "IconSkip",
        _ => null,
    };
    public string? PrimaryKindColorKey => PrimaryTargetKind switch
    {
        OperationKind.Overwrite => "Brush.Danger",
        OperationKind.Rename => "Brush.Warning",
        OperationKind.New => "Brush.Success",
        OperationKind.SkipConflict => "Brush.Muted",
        _ => null,
    };

    /// <summary>Anything other than keeping the source is destructive from the source's view. Reads
    /// the stored enum rather than <see cref="SourceDisposition"/>, whose <c>ToString</c> would
    /// allocate on a predicate the filter pass runs per row.</summary>
    public bool IsSourceDisposalDestructive => Store.IsSourceDisposalDestructive(Index);

    public bool IsProcessed => Disposition == OperationKind.Processed;
    public bool IsFilterSkipped => Disposition == OperationKind.SkippedByFilter;
    public bool IsUnchangedSkipped => Disposition == OperationKind.SkippedUnchanged;

    /// <summary>Untouched: nothing happens to this source file — it was filtered out or is unchanged.</summary>
    public bool IsUntouched => IsFilterSkipped || IsUnchangedSkipped;
    /// <summary>Deleted: the source would be removed (moved to trash/archive or permanently deleted).</summary>
    public bool IsDeleted => IsSourceDisposalDestructive;

    /// <summary>Distinct destination roots this file's targets land under — the Sources-tab
    /// "filter by destination" facet keys. Allocates per call, so only the once-per-load facet
    /// count pass reads it; the per-row rebuild filter goes through the allocation-free
    /// <see cref="HasTargetUnder"/> instead.</summary>
    public IReadOnlyList<string> TargetRoots
    {
        get
        {
            int start = Store.TargetStart(Index), end = Store.TargetEnd(Index);
            if (start == end)
                return [];
            // A file fans out to a handful of targets at most, and its roots are references into the
            // one directory-path array — so a linear scan beats a HashSet for the distinct check.
            List<string> roots = new(end - start);
            for (int p = start; p < end; p++)
            {
                string root = Store.OpRoot(p);
                if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                    roots.Add(root);
            }
            return roots;
        }
    }

    /// <summary>Whether any target lands under one of the given roots — the destination-facet
    /// predicate, run per row on every rebuild (duplicates don't matter for an any-match, so this
    /// skips <see cref="TargetRoots"/>' distinct/list allocations).</summary>
    public bool HasTargetUnder(IReadOnlySet<string> destinationRoots)
    {
        int end = Store.TargetEnd(Index);
        for (int p = Store.TargetStart(Index); p < end; p++)
        {
            if (destinationRoots.Contains(Store.OpRoot(p)))
                return true;
        }
        return false;
    }

    /// <summary>Filter-skipped rows are visually de-emphasized by dimming the row's content (path +
    /// targets); the pills stay fully legible, so it binds this on the content only.</summary>
    public double ContentOpacity => IsFilterSkipped ? 0.55 : 1.0;

    public bool Matches(string term)
    {
        if (DryRunPaths.PathContains(DirPath, FileName, term))
            return true;
        int end = Store.TargetEnd(Index);
        for (int p = Store.TargetStart(Index); p < end; p++)
        {
            if (DryRunPaths.PathContains(Store.OpDirPath(p), Store.OpFileName(p), term))
                return true;
        }
        return false;
    }
}

/// <summary>How a destination-side file is affected in the Destinations tab. A display-oriented
/// projection of the destination <see cref="OperationKind"/>s (e.g. both New and a conflict Rename
/// present as <see cref="New"/>; the several skip/keep kinds collapse to <see cref="Untouched"/>).</summary>
public enum DestinationRowKind { Untouched, New, Overwritten, Deleted, Unknown }

internal static class DestinationKindMap
{
    /// <summary>Projects a destination <see cref="OperationKind"/> onto the display-oriented
    /// <see cref="DestinationRowKind"/>. The server already split a conflict rename into a Rename op
    /// (a new file at the suffixed path) plus an Untouched op (the kept original), so both are mapped
    /// straight through.</summary>
    public static DestinationRowKind Map(OperationKind kind) => kind switch
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
}

/// <summary>One resulting destination path within a <see cref="DryRunDestinationRow"/> — a single
/// target a source file lands at (a grouped row can have several), or the sole entry of a row that
/// has no originating source (a Mirror deletion, a kept-around conflict original, a pre-existing
/// untouched file).</summary>
public sealed record DryRunDestinationEntry(DryRunRowStore Store, int Position)
{
    public string DirPath => Store.OpDirPath(Position);
    public string FileName => Store.OpFileName(Position);
    public string TargetRoot => Store.OpRoot(Position);
    public DestinationRowKind Kind => DestinationKindMap.Map(Store.OpKind(Position));
    public string? Detail => Store.OpDetail(Position);
    public string? CommonRoot => Store.DestinationCommonRoot;

    /// <summary>The resulting file's byte size: the incoming content for a write, or the existing
    /// file for an untouched/deleted entry.</summary>
    public long SizeBytes => Store.OpSize(Position);

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

    public string StatusText => Kind switch
    {
        DestinationRowKind.Untouched => "Untouched",
        DestinationRowKind.New => "New",
        DestinationRowKind.Overwritten => "Overwritten",
        DestinationRowKind.Deleted => "Deleted",
        _ => "Unknown",
    };

    /// <summary>Icon/colour resource keys for this entry's status glyph (resolved in the view via
    /// IconConverters); <see cref="StatusText"/> is the tooltip word.</summary>
    public string StatusIconKey => Kind switch
    {
        DestinationRowKind.Untouched => "IconUntouched",
        DestinationRowKind.New => "IconAdd",
        DestinationRowKind.Overwritten => "IconOverwrite",
        DestinationRowKind.Deleted => "IconTrash",
        _ => "IconQuestion",
    };
    public string StatusColorKey => Kind switch
    {
        DestinationRowKind.Untouched => "Brush.Info",
        DestinationRowKind.New => "Brush.Success",
        DestinationRowKind.Overwritten => "Brush.Warning",
        DestinationRowKind.Deleted => "Brush.Danger",
        _ => "Brush.Muted",
    };

    public bool Matches(string term) => DryRunPaths.PathContains(DirPath, FileName, term);
}

/// <summary>A row in the Destinations tab: a source file and every destination it fans out to
/// (replication collapses to one row with a list of destinations instead of one row per target).
/// Rows with no originating source (<see cref="HasSource"/> false) carry a single
/// <see cref="DryRunDestinationEntry"/> and render as a plain single-status row.</summary>
/// <param name="Key">A source-file index when non-negative — the row is that file and its fan-out.
/// Otherwise <c>~position</c> of a single destination operation with no originating source.</param>
/// <param name="Slice">Null for a whole row (the store's own CSR range). Non-null when a filter kept
/// only some of a fan-out's destinations: the Destinations tab filters at the entry level, so a row
/// replicated to four targets can survive showing one. The slice is shared by every row of one
/// filtered list, so it compares by reference and row identity survives re-materialization.</param>
/// <param name="SliceStart">First index in <paramref name="Slice"/> belonging to this row.</param>
/// <param name="SliceEnd">One past this row's last index in <paramref name="Slice"/>.</param>
public sealed partial record DryRunDestinationRow(
    DryRunRowStore Store, int Key, int[]? Slice = null, int SliceStart = 0, int SliceEnd = 0) : IDryRunFileRow
{
    /// <summary>Whether this row is a source file and its fan-out, as opposed to a lone destination
    /// operation that has no originating source.</summary>
    public bool HasSource => Key >= 0;

    public string? SourceDirPath => HasSource ? Store.SourceDirPath(Key) : null;
    public string? SourceFileName => HasSource ? Store.SourceFileName(Key) : null;
    public string? SourceRoot => HasSource ? Store.SourceRoot(Key) : null;
    public string? SourceCommonRoot => Store.SourceCommonRoot;

    /// <summary>How many resulting destinations this row shows.</summary>
    public int EntryCount => Slice is null ? WholeEnd - WholeStart : SliceEnd - SliceStart;

    /// <summary>The store CSR position of this row's <paramref name="n"/>th shown destination.</summary>
    public int EntryAt(int n) => Slice is null ? WholeStart + n : Slice[SliceStart + n];

    /// <summary>The store's own CSR range for this row: a source file's whole fan-out, or the single
    /// operation of a no-source row.</summary>
    private int WholeStart => HasSource ? Store.TargetStart(Key) : ~Key;
    private int WholeEnd => HasSource ? Store.TargetEnd(Key) : ~Key + 1;

    /// <summary>The resulting destinations. Materialized on demand — the row's own glyphs go through
    /// <see cref="DistinctStatusEntries"/>, which reads the store's kinds directly and allocates only
    /// the entries it actually renders.</summary>
    public IReadOnlyList<DryRunDestinationEntry> Destinations
    {
        get
        {
            DryRunDestinationEntry[] entries = new DryRunDestinationEntry[EntryCount];
            for (int n = 0; n < entries.Length; n++)
                entries[n] = new DryRunDestinationEntry(Store, EntryAt(n));
            return entries;
        }
    }

    /// <summary>Clipboard/shell actions behind this row's right-click menu; set at build time, null
    /// in headless tests (the commands then no-op). An <c>init</c> property and NOT a positional
    /// member, so it stays out of the record's value equality.</summary>
    public IDryRunItemActions? Actions { get; init; }

    // File right-click commands acting on the representative destination (Primary) — a destination
    // row is a resulting file (or fan-out); folders only appear in the tree. Names mirror the tree
    // node / source row so the shared file menu markup binds identically. Computed get-only (see the
    // note on DryRunFileRow) so they never join this record's value equality; Open File / reveal grey
    // out via a live disk check, so a planned (not-yet-written) destination shows them disabled.
    public IRelayCommand CopyPathCommand => new RelayCommand(() => Actions?.CopyText(Primary.TargetPath));
    public IRelayCommand CopyNameCommand => new RelayCommand(() => Actions?.CopyText(Primary.FileName));
    // Disabled for a name that is all extension (a dotfile like ".git" — stem is empty).
    public IRelayCommand CopyNameWithoutExtensionCommand =>
        new RelayCommand(
            () => Actions?.CopyText(System.IO.Path.GetFileNameWithoutExtension(Primary.FileName)),
            () => System.IO.Path.GetFileNameWithoutExtension(Primary.FileName).Length > 0);
    public IRelayCommand OpenFileCommand =>
        new RelayCommand(() => Actions?.OpenFile(Primary.TargetPath), () => System.IO.File.Exists(Primary.TargetPath));
    public IRelayCommand RevealInExplorerCommand =>
        new RelayCommand(() => Actions?.RevealInExplorer(Primary.TargetPath), () => System.IO.File.Exists(Primary.TargetPath));

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

    /// <summary>The sole entry of a no-source row (also the first entry generally); the simple
    /// template binds its status and folder off this.</summary>
    public DryRunDestinationEntry Primary => new(Store, EntryAt(0));

    /// <summary>One entry per distinct destination status kind, in kind order — drives the row's
    /// status glyphs so a source fanning out to N targets shows one icon per kind, not N identical
    /// icons. There are five kinds, so the distinct set is a bitmask over the row's CSR range: this
    /// runs on the UI thread for every realized destination row, and the LINQ chain it replaced
    /// (<c>DistinctBy</c> → <c>OrderBy</c> → <c>ToList</c>) allocated four objects plus one entry per
    /// destination before discarding all but the few it rendered.</summary>
    public IReadOnlyList<DryRunDestinationEntry> DistinctStatusEntries
    {
        get
        {
            int count = EntryCount;
            int seen = 0;
            // First position per kind, so the entry rendered for a kind is the same one the old
            // DistinctBy kept (it takes the first of each group).
            Span<int> firstOf = stackalloc int[5];
            for (int n = 0; n < count; n++)
            {
                int p = EntryAt(n);
                int kind = (int)DestinationKindMap.Map(Store.OpKind(p));
                if ((seen & (1 << kind)) != 0)
                    continue;
                seen |= 1 << kind;
                firstOf[kind] = p;
            }
            List<DryRunDestinationEntry> distinct = new(System.Numerics.BitOperations.PopCount((uint)seen));
            for (int kind = 0; kind < 5; kind++)       // ascending kind == the old OrderBy(e => e.Kind)
            {
                if ((seen & (1 << kind)) != 0)
                    distinct.Add(new DryRunDestinationEntry(Store, firstOf[kind]));
            }
            return distinct;
        }
    }

    /// <summary>The row tooltip: the originating source path, or the resulting path for rows that
    /// have no source (Mirror deletions, kept-around originals, pre-existing untouched files).</summary>
    public string HoverPath => SourcePath ?? Primary.TargetPath;

    public bool Matches(string term)
    {
        if (HasSource && DryRunPaths.PathContains(Store.SourceDirPath(Key), Store.SourceFileName(Key), term))
            return true;
        for (int n = 0; n < EntryCount; n++)
        {
            int p = EntryAt(n);
            if (DryRunPaths.PathContains(Store.OpDirPath(p), Store.OpFileName(p), term))
                return true;
        }
        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Tree: a single generic path-forest whose nodes carry rolled-up, colour-coded summary pills.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One rendered pill on a tree node: a count shown under a colour-tinted glyph. Icon and
/// colour are carried as resource keys (resolved in the view via IconConverters against App.axaml /
/// Tokens.axaml) so the icon set and palette stay defined once and this view-model type keeps no
/// Avalonia/resource dependency — the pure view-model tests need no running application.</summary>
public sealed record TreePill(string CountText, string IconKey, string ColorKey, string Tip);

/// <summary>Declares a pill category for a tree: its kind key, tooltip label, glyph + colour resource
/// keys, and the display order.</summary>
public sealed record TreePillSpec(string Kind, string Label, string IconKey, string ColorKey);

/// <summary>Per-forest handle to the grid source, shared by every directory node in a forest and
/// assigned once the source is built (nodes are constructed before the source exists). Lets a
/// node's folder context-menu commands reach the source API for whole-tree / subtree expansion —
/// the only reliable path, since setting a model's IsExpanded only affects already-realized rows.</summary>
internal sealed class DryRunTreeController
{
    public HierarchicalTreeDataGridSource<DryRunTreeNode>? Source { get; set; }

    /// <summary>The clipboard/shell actions behind the node right-click menu, shared by every node
    /// in the forest (null in headless tests, where the menu commands simply no-op).</summary>
    public IDryRunItemActions? Actions { get; set; }
}

/// <summary>A node in a path tree whose counts are the rolled-up totals of everything beneath it,
/// rendered as coloured summary pills. The forest is derived from the directory structure the rows
/// already share (their directory strings reference the report's materialized directory table), so
/// building it is cheap even at the streamed cap; TreeDataGrid flattens the forest into a single
/// virtualized row list and only realizes the rows currently in view, however deep or expanded the
/// tree is.</summary>
public sealed partial class DryRunTreeNode : ObservableObject, IDryRunFileRow
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>On the first tree build the top-level folders auto-expand only when the first level of
    /// expansion would reveal at most this many rows — the files at the common root plus the immediate
    /// children (subfolders + files) of each top-level folder. Above it the tree opens fully collapsed.
    /// Counting only the top level's own children (not the whole subtree) keeps a deep tree with a few
    /// top-level folders auto-expanded while a wide top level collapses.</summary>
    internal const int AutoExpandChildLimit = 100;

    // Directory nodes store their absolute path; leaves hold a reference to their directory's
    // shared string and reconstruct on demand — FullPath is only ever read by the tooltip and
    // CollectExpanded, and per-leaf absolute paths alone are hundreds of MB at the streamed cap.
    private readonly string? _dirFullPath;      // directory nodes only
    private readonly string? _parentDirPath;    // leaves only — the row's shared directory string
    private List<DryRunTreeNode>? _children;    // subdirectory nodes; leaves are added lazily on expand
    private int[]? _counts;                     // per-spec counts, released once the pills are built
    private long _sizeBytes;
    // A directory node's file leaves are not built up front — they cost one node + one counts buffer
    // each (~500k of both at the streamed cap). Instead the directory keeps a factory that builds its
    // leaves the first time TreeDataGrid asks for its children (i.e. when the user expands it), and the
    // result is cached into _children. _hasChildren drives the expander chevron without materializing.
    private Func<IReadOnlyList<DryRunTreeNode>>? _leafFactory;
    private bool _hasChildren;
    // Per-forest handle to the grid source, shared by every directory node so the folder context-menu
    // commands can drive whole-tree / subtree expansion through the source API (the only viewport-
    // independent path). Null on leaves — files show no menu — and until BuildSource attaches it.
    private readonly DryRunTreeController? _controller;

    // Directories before files, then alphabetical — the familiar file-explorer order, applied both to
    // the up-front subdirectory sort and to the lazy merge of subdirs + freshly built leaves.
    private static readonly Comparison<DryRunTreeNode> DirsFirstByName = static (a, b) =>
        a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private DryRunTreeNode(string name, string? dirFullPath, string? parentDirPath, bool isDirectory, int depth,
        DryRunTreeController? controller = null)
    {
        Name = name;
        _dirFullPath = dirFullPath;
        _parentDirPath = parentDirPath;
        IsDirectory = isDirectory;
        Depth = depth;
        _controller = controller;
    }

    public string Name { get; }

    /// <summary>The absolute path (the tooltip, and the expand-state key across rebuilds) — stored
    /// for directory nodes, reconstructed on demand for leaves.</summary>
    public string FullPath => _dirFullPath ?? System.IO.Path.Join(_parentDirPath, Name);

    public bool IsDirectory { get; }
    public int Depth { get; }

    /// <summary>The node's children. Directory nodes carry their subdirectories eagerly but build their
    /// file leaves lazily on first access (TreeDataGrid pulls this only when a row is expanded): the
    /// leaves are merged with the subdirectories, sorted once, and cached, so every later access returns
    /// the same instance (the grid rebuilds child rows only when the reference changes). Runs on the UI
    /// thread only — build-time passes use the <c>_children</c> field directly and never trip this.</summary>
    public IReadOnlyList<DryRunTreeNode> Children
    {
        get
        {
            if (_leafFactory is { } factory)
            {
                IReadOnlyList<DryRunTreeNode> leaves = factory();
                List<DryRunTreeNode> merged = new((_children?.Count ?? 0) + leaves.Count);
                if (_children is { } subs)
                    merged.AddRange(subs);
                merged.AddRange(leaves);
                merged.Sort(DirsFirstByName);
                _children = merged;
                _leafFactory = null;   // materialize exactly once; the reference is stable hereafter
            }
            return (IReadOnlyList<DryRunTreeNode>?)_children ?? [];
        }
    }

    /// <summary>Expand/collapse state, bound two-way to the TreeDataGrid expander column's
    /// <c>IsExpandedBinding</c>, so a rebuild can restore what the user had open.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public bool HasChildren => _hasChildren;

    // Folder right-click context-menu commands (see DryRunView.axaml's TextBlock.dir ContextFlyout).
    // Single open/close set the two-way-bound IsExpanded directly: the right-clicked folder is on
    // screen, so its cell is realized and the change propagates to the row. The recursive and
    // whole-tree operations go through the source API instead — setting IsExpanded on off-screen
    // descendants would not take effect until their cells realize, whereas the source rebuilds the
    // flattened row list in one shot.
    [RelayCommand] private void OpenFolder() => IsExpanded = true;
    [RelayCommand] private void CloseFolder() => IsExpanded = false;

    [RelayCommand]
    private void OpenFolderRecursive()
    {
        if (_controller?.Source is not { } source)
        {
            IsExpanded = true;   // no source attached (e.g. tests) — at least open this folder
            return;
        }
        if (FindRow(source, this) is { } row)
            source.ExpandCollapseRecursive(row, static _ => true);
    }

    [RelayCommand] private void ExpandAll() => _controller?.Source?.ExpandAll();
    [RelayCommand] private void CloseAll() => _controller?.Source?.CollapseAll();

    // The Ctrl+Enter keyboard toggle: this folder and everything under it — expand the whole
    // subtree when the folder is collapsed, collapse it (resetting every descendant) when open. Both
    // directions go through the source so the grid's rows and the models stay in sync. The collapse
    // ALSO resets the descendant models directly: the source's row-entry recursion collapses the
    // entry row before visiting its children, which releases the child rows, so off-screen
    // descendants would keep a stale expanded flag and spring back open the next time just this
    // folder is opened (the whole-tree CollapseAll does not have this problem — its root overload
    // recurses children first).
    [RelayCommand]
    private void ToggleExpandCollapseAll()
    {
        bool expand = !IsExpanded;
        if (_controller?.Source is { } source && FindRow(source, this) is { } row)
            source.ExpandCollapseRecursive(row, _ => expand);
        if (!expand)
            CollapseDescendantModels(this);
        IsExpanded = expand;
    }

    /// <summary>Clears the expand flag on every materialized descendant directory model. Uses the
    /// <c>_children</c> field, never the <see cref="Children"/> getter — a collapse must not force
    /// lazy leaf realization (an unrealized subtree has nothing expanded to reset).</summary>
    private static void CollapseDescendantModels(DryRunTreeNode node)
    {
        if (node._children is not { } children)
            return;
        foreach (DryRunTreeNode child in children)
        {
            if (!child.IsDirectory)
                continue;
            child.IsExpanded = false;
            CollapseDescendantModels(child);
        }
    }

    // Clipboard / shell right-click commands, on files and folders alike (the node menu gates which
    // are shown by IsDirectory). All route through the forest's shared actions service; Open File /
    // reveal / Open Folder in Explorer are gated by a disk check so they grey out for a planned entry
    // that does not exist on disk (e.g. a not-yet-written destination file).
    [RelayCommand] private void CopyPath() => _controller?.Actions?.CopyText(FullPath);
    [RelayCommand] private void CopyName() => _controller?.Actions?.CopyText(Name);
    [RelayCommand(CanExecute = nameof(CanCopyNameWithoutExtension))]
    private void CopyNameWithoutExtension() =>
        _controller?.Actions?.CopyText(System.IO.Path.GetFileNameWithoutExtension(Name));

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFile() => _controller?.Actions?.OpenFile(FullPath);
    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void RevealInExplorer() => _controller?.Actions?.RevealInExplorer(FullPath);
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolderInExplorer() => _controller?.Actions?.OpenFolderInExplorer(FullPath);

    /// <summary>A file that exists on disk right now — drives whether Open File / Open File In
    /// Explorer are enabled (checked when the menu realizes the command, per the right-click).</summary>
    private bool CanOpenFile => !IsDirectory && System.IO.File.Exists(FullPath);
    /// <summary>A folder that exists on disk right now — drives Open Folder In Explorer.</summary>
    private bool CanOpenFolder => IsDirectory && System.IO.Directory.Exists(FullPath);
    /// <summary>The name has something before its extension — false for an all-extension dotfile
    /// (".git"), so Copy File Name Without Extension greys out rather than copying an empty string.</summary>
    private bool CanCopyNameWithoutExtension => System.IO.Path.GetFileNameWithoutExtension(Name).Length > 0;

    // The clicked folder is visible, so its HierarchicalRow is present in the flattened Rows list.
    private static HierarchicalRow<DryRunTreeNode>? FindRow(
        HierarchicalTreeDataGridSource<DryRunTreeNode> source, DryRunTreeNode node)
    {
        foreach (object? r in source.Rows)
            if (r is HierarchicalRow<DryRunTreeNode> hr && ReferenceEquals(hr.Model, node))
                return hr;
        return null;
    }

    /// <summary>Test seam: true while this directory still has file leaves waiting to be built (i.e.
    /// <see cref="Children"/> has not been accessed since the forest was built). Lets tests assert that
    /// snapshotting expansion or reading directory pills does not force materialization.</summary>
    internal bool LeavesPending => _leafFactory is not null;

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
    /// non-zero count. On the first build (no <paramref name="expandedPaths"/>) the top-level folders
    /// start expanded so the tree opens on something to see — but only when doing so reveals a modest
    /// number of rows (root-level files plus the immediate children of the top-level folders, see
    /// <see cref="AutoExpandChildLimit"/>); a wide top level opens fully collapsed, while a deep tree
    /// with a few top-level folders still auto-expands.</summary>
    public static IReadOnlyList<DryRunTreeNode> BuildForest<T>(
        IEnumerable<T> rows,
        Func<T, string> dirSelector,
        Func<T, string> nameSelector,
        Func<T, IReadOnlyList<(string Kind, int Increment)>> categorizer,
        IReadOnlyList<TreePillSpec> specs,
        string? commonRoot = null,
        IReadOnlySet<string>? expandedPaths = null,
        Func<T, long>? sizeSelector = null,
        IDryRunItemActions? actions = null)
    {
        List<DryRunTreeNode> roots = [];
        Dictionary<string, DryRunTreeNode> rootIndex = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<DryRunTreeNode, Dictionary<string, DryRunTreeNode>> childIndex = [];
        // One entry per distinct directory string; a null value means the directory IS the common
        // root, so its files sit at forest-root level.
        Dictionary<string, DryRunTreeNode?> dirNodeByPath = new(StringComparer.OrdinalIgnoreCase);
        string? rootPrefix = string.IsNullOrEmpty(commonRoot) ? null : commonRoot.TrimEnd('\\', '/');

        // Shared by every node in this forest; BuildSource attaches the built source to it so the
        // folder context-menu commands can drive expansion through the source API, and it carries the
        // clipboard/shell actions the file + folder right-click commands route through.
        DryRunTreeController controller = new() { Actions = actions };

        Dictionary<string, int> specIndex = new(specs.Count, StringComparer.Ordinal);
        for (int k = 0; k < specs.Count; k++)
            specIndex[specs[k].Kind] = k;

        // Rows under a real directory are bucketed on that directory and their contribution rolled onto
        // it now (so its pills/size are correct before any leaf exists); the leaf nodes themselves are
        // deferred to first expand. Rows that sit in the common root itself (dirNode == null) have no
        // directory to defer under and the forest root is always realized, so they stay eager.
        Dictionary<DryRunTreeNode, List<T>> bucketByDir = [];
        foreach (T row in rows)
        {
            string dirPath = dirSelector(row);
            if (!dirNodeByPath.TryGetValue(dirPath, out DryRunTreeNode? dirNode))
                dirNodeByPath[dirPath] = dirNode = ResolveDirectory(dirPath);

            if (dirNode is null)
            {
                string fileName = nameSelector(row);
                if (!rootIndex.TryGetValue(fileName, out DryRunTreeNode? leaf))
                {
                    leaf = new DryRunTreeNode(fileName, dirFullPath: null, dirPath, isDirectory: false, depth: 0, controller);
                    roots.Add(leaf);
                    rootIndex[fileName] = leaf;
                }
                Accumulate(leaf, row);
            }
            else
            {
                if (!bucketByDir.TryGetValue(dirNode, out List<T>? bucket))
                    bucketByDir[dirNode] = bucket = [];
                bucket.Add(row);
                Accumulate(dirNode, row);
            }
        }

        // One leaf-pill cache shared across every directory's factory in THIS forest — the leaf's single
        // "1 <label>" pill dominates, so sharing maximizes de-dup. Allocated here (off-thread) but only
        // ever touched by BuildLeaves, which runs on the UI thread when a directory is expanded.
        Dictionary<(int Spec, int Count), TreePill> leafPillCache = [];
        Dictionary<TreePill, IReadOnlyList<TreePill>> leafSingleLists = [];
        foreach ((DryRunTreeNode dir, List<T> bucket) in bucketByDir)
        {
            List<T> capturedBucket = bucket;
            int leafDepth = dir.Depth + 1;
            dir._leafFactory = () => BuildLeaves(
                capturedBucket, leafDepth, nameSelector, dirSelector, categorizer, sizeSelector,
                specs, specIndex, leafPillCache, leafSingleLists, controller);
        }

        // First build: auto-expand the top-level folders only when the first level of expansion stays
        // small — root-level files plus each top-level folder's immediate children (its subfolders and
        // its own files). Only the top level's children count, so a deep tree with a few top-level
        // folders still opens expanded, while a wide top level opens collapsed. (On a rebuild the saved
        // per-node state in expandedPaths already drove the flags in ResolveDirectory.)
        if (expandedPaths is null)
        {
            long revealed = 0;
            foreach (DryRunTreeNode root in roots)
            {
                if (!root.IsDirectory)
                {
                    revealed++;   // a file sitting directly at the common root
                }
                else
                {
                    // Count DISTINCT child names: rows sharing a file name collapse to one leaf on
                    // expand (BuildLeaves de-dups), so that's what the first expansion actually reveals.
                    revealed += root._children?.Count ?? 0;
                    if (bucketByDir.TryGetValue(root, out List<T>? bucket))
                    {
                        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                        foreach (T r in bucket)
                        {
                            names.Add(nameSelector(r));
                            if (revealed + names.Count > AutoExpandChildLimit) break;   // keep the scan ~O(limit)
                        }
                        revealed += names.Count;
                    }
                }
                if (revealed > AutoExpandChildLimit) break;   // already too many; stop counting
            }
            if (revealed <= AutoExpandChildLimit)
                foreach (DryRunTreeNode root in roots)
                    if (root.IsDirectory)
                        root.IsExpanded = true;
        }

        SortRecursive(roots);
        FinishRecursive(roots, specs, [], []);
        foreach (DryRunTreeNode root in roots)
            root._counts = null;
        return roots;

        // Rolls one row's (kind, count) increments and size onto a node — the leaf's own node for a
        // root-level file, otherwise the directory node (its leaves are deferred). Kinds absent from the
        // specs are dropped, exactly as pills only ever rendered spec kinds.
        void Accumulate(DryRunTreeNode node, T row)
        {
            foreach ((string kind, int increment) in categorizer(row))
            {
                if (specIndex.TryGetValue(kind, out int k))
                    (node._counts ??= new int[specs.Count])[k] += increment;
            }
            node._sizeBytes += sizeSelector?.Invoke(row) ?? 0;
        }

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
            // was taken relative to it; otherwise (fallback to the full path) start empty — EXCEPT a
            // UNC path, whose leading "\\" is a separator that Split(RemoveEmptyEntries) drops, so seed
            // a single backslash and let the loop's first append ($"{prefix}\\{segment}") add the second,
            // reconstructing "\\server\share\…" instead of "server\share\…".
            string prefix;
            if (!ReferenceEquals(relative, dirPath))
                prefix = rootPrefix ?? "";
            else if (dirPath.StartsWith(@"\\", StringComparison.Ordinal) || dirPath.StartsWith("//", StringComparison.Ordinal))
                prefix = @"\";
            else
                prefix = "";
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
                    // rebuilds. On the first build (no prior state) everything starts collapsed here; the
                    // top-level auto-expand is applied afterward, once the child metric is known.
                    bool expanded = expandedPaths is not null && expandedPaths.Contains(prefix);
                    node = new DryRunTreeNode(segment, prefix, parentDirPath: null, isDirectory: true, i, controller)
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

    /// <summary>Re-applies expansion state onto a freshly built forest. A rebuild snapshots
    /// <see cref="CollectExpanded"/> before its possibly seconds-long off-thread build, so anything
    /// the user expanded or collapsed while the build ran would be silently reverted — the
    /// publisher re-applies the live tree's state just before swapping the forest in.</summary>
    public static void ApplyExpanded(IEnumerable<DryRunTreeNode> nodes, IReadOnlySet<string> expandedPaths)
    {
        foreach (DryRunTreeNode node in nodes)
        {
            if (!node.IsDirectory)
                continue;
            node.IsExpanded = expandedPaths.Contains(node.FullPath);
            if (node._children is { Count: > 0 } children)
                ApplyExpanded(children, expandedPaths);
        }
    }

    /// <summary>Collects the absolute path of every expanded node in a forest so a rebuild can restore
    /// the user's expand/collapse state instead of snapping back to defaults on every keystroke.</summary>
    public static IReadOnlySet<string> CollectExpanded(IEnumerable<DryRunTreeNode> nodes)
    {
        HashSet<string> into = new(StringComparer.OrdinalIgnoreCase);
        Walk(nodes);
        return into;

        // Recurse the internal _children field, never the public Children getter — snapshotting the
        // expanded set (on every filter keystroke) must not force lazy leaf materialization. _children
        // always holds every subdirectory, so all expandable descendants are still reached; leaves are
        // absent before materialization and, being non-expandable, irrelevant after.
        void Walk(IEnumerable<DryRunTreeNode>? level)
        {
            if (level is null)
                return;
            foreach (DryRunTreeNode n in level)
            {
                if (n.IsExpanded)
                    into.Add(n.FullPath);
                Walk(n._children);
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
            // Names are the primary content: hold a readable floor so the Name column can never be
            // squeezed to the default 30px minimum (and vanish) by a wide pills column — the deep,
            // high-count directories of a large profile roll up to very long summary pills otherwise.
            MinWidth = new GridLength(160, GridUnitType.Pixel),
            CompareAscending = (a, b) => CompareNodes(a, b, ascending: true),
            CompareDescending = (a, b) => CompareNodes(a, b, ascending: false),
        };
        // A TemplateColumn (not a TextColumn) so the size renders through the same centred, monospace
        // cell path as the name and the two line up vertically; sort still compares the raw byte count.
        TemplateColumnOptions<DryRunTreeNode> sizeOptions = new()
        {
            CompareAscending = static (a, b) => (a?.SizeBytes ?? 0).CompareTo(b?.SizeBytes ?? 0),
            CompareDescending = static (a, b) => (b?.SizeBytes ?? 0).CompareTo(a?.SizeBytes ?? 0),
        };
        // Cap the auto-sized pills column so it yields the row to the Name column instead of growing
        // to its content; pills past the cap wrap to a second line (see the WrapPanel in the cell
        // template) rather than clipping the counts.
        TemplateColumnOptions<DryRunTreeNode> pillsOptions = new()
        {
            CanUserSortColumn = false,
            MaxWidth = new GridLength(280, GridUnitType.Pixel),
        };

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
                new TemplateColumn<DryRunTreeNode>("Size", "DryRunSizeCell", options: sizeOptions),
                new TemplateColumn<DryRunTreeNode>("Status", "DryRunPillsCell", options: pillsOptions),
            },
        };

        // Attach the source to the forest's shared controller so folder context-menu commands can
        // reach it. Every directory node shares one instance, so the first directory root carries it;
        // a file-only forest has no folders (hence no menu) and nothing to attach.
        foreach (DryRunTreeNode root in roots)
            if (root._controller is { } c)
            {
                c.Source = source;
                break;
            }

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

            // A directory shows an expander if it carries subdirectories OR still has file leaves waiting
            // to be built (a deferred factory). Leaves have neither, so this stays false for them.
            node._hasChildren = node._children is { Count: > 0 } || node._leafFactory is not null;

            BuildPills(node, specs, pillCache, singlePillLists);
        }
    }

    /// <summary>Renders a node's rolled-up counts into its <see cref="Pills"/> in spec order, skipping
    /// zero counts. Pills are memoized per (spec, count) and single-pill nodes — the dominant leaf case —
    /// share one list instance per distinct pill. Shared by the up-front directory roll-up and the lazy
    /// leaf build.</summary>
    private static void BuildPills(
        DryRunTreeNode node,
        IReadOnlyList<TreePillSpec> specs,
        Dictionary<(int Spec, int Count), TreePill> pillCache,
        Dictionary<TreePill, IReadOnlyList<TreePill>> singlePillLists)
    {
        if (node._counts is not int[] own)
            return;   // nothing categorized beneath — Pills stays empty
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
                    new TreePill($"{count:N0}", spec.IconKey, spec.ColorKey, spec.Label);
            }
            (pills ??= []).Add(pill);
        }
        if (pills is null)
            return;
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

    /// <summary>Builds a directory's file-leaf nodes on demand — the first time TreeDataGrid asks for the
    /// directory's children (i.e. the user expands it). Rows sharing a file name merge into one leaf with
    /// summed counts/size (the same dedup the eager build did inline). Order is irrelevant: the
    /// <see cref="Children"/> getter re-sorts the merged subdirectory + leaf list.</summary>
    private static List<DryRunTreeNode> BuildLeaves<T>(
        List<T> bucket,
        int leafDepth,
        Func<T, string> nameSelector,
        Func<T, string> dirSelector,
        Func<T, IReadOnlyList<(string Kind, int Increment)>> categorizer,
        Func<T, long>? sizeSelector,
        IReadOnlyList<TreePillSpec> specs,
        Dictionary<string, int> specIndex,
        Dictionary<(int Spec, int Count), TreePill> pillCache,
        Dictionary<TreePill, IReadOnlyList<TreePill>> singlePillLists,
        DryRunTreeController? controller)
    {
        Dictionary<string, DryRunTreeNode> byName = new(StringComparer.OrdinalIgnoreCase);
        List<DryRunTreeNode> leaves = new(bucket.Count);
        foreach (T row in bucket)
        {
            string fileName = nameSelector(row);
            if (!byName.TryGetValue(fileName, out DryRunTreeNode? leaf))
            {
                leaf = new DryRunTreeNode(fileName, dirFullPath: null, dirSelector(row), isDirectory: false, leafDepth, controller);
                byName[fileName] = leaf;
                leaves.Add(leaf);
            }
            foreach ((string kind, int increment) in categorizer(row))
            {
                if (specIndex.TryGetValue(kind, out int k))
                    (leaf._counts ??= new int[specs.Count])[k] += increment;
            }
            leaf._sizeBytes += sizeSelector?.Invoke(row) ?? 0;
        }
        foreach (DryRunTreeNode leaf in leaves)
        {
            BuildPills(leaf, specs, pillCache, singlePillLists);
            leaf._counts = null;   // a leaf is terminal — nothing rolls up from it
        }
        return leaves;
    }

    // Sorts the subdirectory nodes at each level (leaves are added and sorted later, lazily, by the
    // Children getter using the same DirsFirstByName comparator).
    private static void SortRecursive(List<DryRunTreeNode> nodes)
    {
        nodes.Sort(DirsFirstByName);
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

/// <summary>A selectable status chip in a tab's summary row (e.g. Processed, Deleted). Unlike a
/// <see cref="DryRunFacetRow"/>, it defaults to <em>unselected</em>: with none selected the list
/// shows everything; selecting one or more narrows the list to rows carrying any selected status.
/// Icon/colour are carried as resource keys (resolved in the view via IconConverters), matching the
/// tree pill specs.</summary>
public sealed partial class DryRunStatusFilter : ViewModelBase
{
    public required string Key { get; init; }        // "untouched","processed","deleted","new","overwritten"
    public required string Label { get; init; }      // tooltip word, e.g. "Processed"
    public required string IconKey { get; init; }    // resolved via IconConverters.Geometry
    public required string ColorKey { get; init; }   // resolved via IconConverters.Brush
    public required int Count { get; init; }
    public string CountText => Count.ToString("N0");

    [ObservableProperty] public partial bool IsSelected { get; set; }
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

internal static class DryRunRebuild
{
    /// <summary>Row count above which a tab's rebuild (filter + optional forest build) hops to the
    /// thread pool. At or below it the rebuild completes synchronously — small reports keep their
    /// immediate click-to-result semantics (and the unit tests their synchronous asserts) while
    /// large reports never block the UI thread.</summary>
    public const int SyncThreshold = 5_000;

    /// <summary>Whether two key arrays describe the same rows in the same order. A rebuild whose
    /// output is unchanged republishes the SAME bound list instance, so the ItemsControl (and its
    /// scroll position / selection) is left alone. Rows are keys now, not objects, so this is an int
    /// compare rather than the reference compare it replaced — and the common no-filter case hands
    /// back the very same array, which <see cref="ReferenceEquals"/> settles in one step.</summary>
    public static bool SameKeys(int[] a, int[] b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }
}

/// <summary>The cancel-and-coalesce lifecycle shared by both tabs: at most one rebuild is current,
/// and scheduling the next supersedes it. Cancellation and publishing synchronize on one lock, so a
/// superseded rebuild can never publish after its successor even where no serializing
/// SynchronizationContext exists (unit tests, benchmarks); in the app both sides run on the UI
/// thread and the lock is uncontended.</summary>
internal sealed class DryRunRebuildGate
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;

    /// <summary>Cancels the current rebuild without starting a successor — a new report load (or
    /// clear) must not let a stale rebuild publish the old report's rows.</summary>
    public void Cancel()
    {
        lock (_sync)
            _cts?.Cancel();
    }

    /// <summary>Supersedes the current rebuild and returns its successor's token.</summary>
    public CancellationToken Supersede()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }
    }

    /// <summary>Runs the publish action unless the rebuild holding <paramref name="ct"/> has been
    /// superseded — atomic with <see cref="Cancel"/>/<see cref="Supersede"/>.</summary>
    public bool TryPublish(CancellationToken ct, Action publish)
    {
        lock (_sync)
        {
            if (ct.IsCancellationRequested)
                return false;
            publish();
            return true;
        }
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
        new("untouched", "untouched", "IconUntouched", "Brush.Info"),
        new("processed", "processed", "IconCheckmark", "Brush.Success"),
        new("deleted", "deleted", "IconTrash", "Brush.Danger"),
    ];

    private readonly TimeSpan _searchDebounce;
    private readonly DryRunRebuildGate _rebuildGate = new();
    private bool _applying;
    // Rows are source-file indices into _store, not objects: the whole retained cost of a 500k-row
    // preview is these two int arrays (4 MB) plus the store itself. _all is presorted by DryRunSort at
    // load and never mutated after; _visible is an order-preserving subset of it (the same array
    // instance when no filter is active).
    private DryRunRowStore _store = DryRunRowStore.Empty;
    private int[] _all = [];
    private int[] _visible = [];

    /// <summary>Clipboard/shell actions for the row + tree right-click menus (null in headless tests).</summary>
    private readonly IDryRunItemActions? _actions;

    public DryRunSourcesTab(TimeSpan searchDebounce, IDryRunItemActions? actions = null)
    {
        _searchDebounce = searchDebounce;
        _actions = actions;
    }

    /// <summary>Wraps a set of source-file indices as the bound row list. The handles are built per
    /// indexer access, so this costs the array and nothing else.</summary>
    private DryRunRowList<DryRunFileRow> RowsFor(int[] keys)
    {
        DryRunRowStore store = _store;
        IDryRunItemActions? actions = _actions;
        return new DryRunRowList<DryRunFileRow>(
            keys,
            i => new DryRunFileRow(store, keys[i]) { Actions = actions },
            static r => r.Index);
    }

    /// <summary>The in-flight (or last completed) rebuild. Rebuilds over
    /// <see cref="DryRunRebuild.SyncThreshold"/> rows run on the thread pool; tests that cross that
    /// size (or use a non-zero debounce) await this before asserting.</summary>
    internal Task PendingRebuild { get; private set; } = Task.CompletedTask;

    /// <summary>True while a rebuild is computing on the thread pool — drives the rows area's
    /// loading overlay. Synchronous small-report rebuilds never set it, so it appears exactly when
    /// there is a visible delay to explain.</summary>
    [ObservableProperty] public partial bool IsRebuilding { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<DryRunFileRow> VisibleRows { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunTreeNode> Tree { get; private set; } = [];

    /// <summary>The TreeDataGrid source the view binds to, rebuilt from <see cref="Tree"/> whenever the
    /// forest changes (null while empty so the grid shows nothing).</summary>
    [ObservableProperty] public partial HierarchicalTreeDataGridSource<DryRunTreeNode>? TreeSource { get; private set; }

    partial void OnTreeChanged(IReadOnlyList<DryRunTreeNode> value)
    {
        // Dispose the previous grid source after the new value has propagated to the bound
        // TreeDataGrid. HierarchicalTreeDataGridSource owns a realized HierarchicalRows cache whose
        // per-row wrappers subscribe to each node's IsExpanded — never disposing it lets the control's
        // realization keep the whole old forest alive, so every tree toggle (and every re-run after
        // one) stacks another forest on the heap. Assign first, dispose second, so the grid never
        // briefly binds to a disposed source.
        HierarchicalTreeDataGridSource<DryRunTreeNode>? previous = TreeSource;
        TreeSource = value.Count == 0 ? null : DryRunTreeNode.BuildSource(value);
        previous?.Dispose();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(FilterLabel))]
    public partial IReadOnlyList<DryRunFacetRow> SourceFacets { get; private set; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(FilterLabel))]
    public partial IReadOnlyList<DryRunFacetRow> DestinationFacets { get; private set; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnyFacet))]
    public partial bool ShowSourceFacet { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnyFacet))]
    public partial bool ShowDestinationFacet { get; private set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ShowTree { get; set; }

    /// <summary>Whether this tab has any facet filter to offer — gates the toolbar's Filter button.</summary>
    public bool ShowAnyFacet => ShowSourceFacet || ShowDestinationFacet;

    /// <summary>How many facet values are currently deselected (i.e. actively filtering the view).
    /// Drives the Filter button's badge so an active filter is visible once the facets are collapsed
    /// into the flyout.</summary>
    public int ActiveFilterCount =>
        SourceFacets.Count(f => !f.IsSelected) + DestinationFacets.Count(f => !f.IsSelected);

    /// <summary>The Filter button caption, carrying the active-filter count when non-zero.</summary>
    public string FilterLabel => ActiveFilterCount > 0 ? $"Filter ({ActiveFilterCount})" : "Filter";

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

    /// <summary>The clickable summary chips (Untouched / Processed / Deleted). None selected → the
    /// list shows everything; selecting one or more narrows it to rows carrying any selected status.</summary>
    [ObservableProperty] public partial IReadOnlyList<DryRunStatusFilter> StatusFilters { get; private set; } = [];

    /// <summary>Whether any status chip is selected — drives the clear (✕) button's visibility.</summary>
    public bool AnyStatusSelected => StatusFilters.Any(f => f.IsSelected);

    /// <summary>Everything <see cref="Load(LoadData)"/> assigns, precomputed by
    /// <see cref="ComputeLoad"/> — pure data, safe to build off the UI thread.</summary>
    internal sealed record LoadData(
        DryRunRowStore Store,
        int[] SortedKeys,
        string? CommonRoot,
        int UntouchedCount,
        int ProcessedCount,
        int DeletedCount,
        Dictionary<string, int> SourceRootCounts,
        Dictionary<string, int> DestinationRootCounts);

    /// <summary>The pure, thread-safe half of loading: one pass for the status and facet counts,
    /// then the single sort the tab ever pays. The sort key is fixed per row (path relative to its
    /// root), so sorting once here makes every later rebuild a linear order-preserving filter.
    /// Reads the store's columns directly — no row object is built for any of it.</summary>
    internal static LoadData ComputeLoad(DryRunRowStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        string? commonRoot = store.SourceCommonRoot;
        int untouched = 0, processed = 0, deleted = 0;
        Dictionary<string, int> sourceCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> destinationCounts = new(StringComparer.OrdinalIgnoreCase);
        List<string> rowRoots = [];   // reused per row; a file fans out to a handful of targets
        int n = store.SourceCount;
        for (int i = 0; i < n; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            OperationKind kind = store.SourceKind(i);
            if (kind is OperationKind.SkippedByFilter or OperationKind.SkippedUnchanged) untouched++;
            if (kind is OperationKind.Processed) processed++;
            if (store.IsSourceDisposalDestructive(i)) deleted++;

            string sourceRoot = store.SourceRoot(i);
            sourceCounts[sourceRoot] = sourceCounts.GetValueOrDefault(sourceRoot) + 1;

            // The facet counts rows, so a file fanning out twice to the same root counts once.
            rowRoots.Clear();
            int end = store.TargetEnd(i);
            for (int p = store.TargetStart(i); p < end; p++)
            {
                string root = store.OpRoot(p);
                if (rowRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                    continue;
                rowRoots.Add(root);
                destinationCounts[root] = destinationCounts.GetValueOrDefault(root) + 1;
            }
        }
        ct.ThrowIfCancellationRequested();

        // Order by path-relative-to-root (then root, then original position) so a file lines up with
        // the same file in the Destinations tab. Keys are computed once per row up front, then an index
        // array is sorted — no per-comparison Path.Join, no per-row tuple, no OrderBy buffering. The
        // final original-index tiebreak keeps the sort stable (Array.Sort is not), matching the prior
        // LINQ OrderBy/ThenBy so rows equal on key+root keep report order.
        string[] keys = new string[n];
        if (n <= DryRunRebuild.SyncThreshold)
        {
            Dictionary<(string, string?), string> relDirCache = [];
            for (int j = 0; j < n; j++)
                keys[j] = DryRunSort.RelativeKey(store.SourceDirPath(j), store.SourceFileName(j), store.SourceRoot(j), relDirCache);
        }
        else
        {
            // Each partition owns a lock-free relDir cache (a shared ConcurrentDictionary would add
            // per-lookup sync); GetRelativePath is pure, so identical keys result regardless of split.
            Parallel.For(0, n, new ParallelOptions { CancellationToken = ct },
                () => new Dictionary<(string, string?), string>(),
                (j, _, cache) =>
                {
                    keys[j] = DryRunSort.RelativeKey(store.SourceDirPath(j), store.SourceFileName(j), store.SourceRoot(j), cache);
                    return cache;
                },
                _ => { });
        }
        ct.ThrowIfCancellationRequested();
        int[] order = new int[n];
        for (int j = 0; j < n; j++)
            order[j] = j;
        Array.Sort(order, (a, b) =>
        {
            int c = string.Compare(keys[a], keys[b], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(store.SourceRoot(a), store.SourceRoot(b), StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.CompareTo(b);
        });
        ct.ThrowIfCancellationRequested();
        // `order` IS the presorted row set: position → source index. The sort keys die with this frame.
        return new(store, order, commonRoot, untouched, processed, deleted, sourceCounts, destinationCounts);
    }

    /// <summary>Convenience for callers holding a store (tests); the app path computes off-thread via
    /// <see cref="DryRunViewModel.PrepareReport"/> and calls <see cref="Load(LoadData)"/>.</summary>
    public void Load(DryRunRowStore store) => Load(ComputeLoad(store));

    /// <summary>The UI-thread half of loading: assigns the precomputed data to the bound
    /// properties. A fresh load has no active filters and the rows arrive presorted, so the whole
    /// set IS the visible list — no rebuild.</summary>
    internal void Load(LoadData data)
    {
        _rebuildGate.Cancel();   // a rebuild racing this load must not publish the old report's rows
        IsRebuilding = false;    // the cancelled rebuild has no successor to clear the overlay
        _applying = true;
        _store = data.Store;
        _all = data.SortedKeys;
        CommonRoot = data.CommonRoot;
        UntouchedCount = data.UntouchedCount;
        ProcessedCount = data.ProcessedCount;
        DeletedCount = data.DeletedCount;
        StatusFilters = BuildStatusFilters();

        SourceFacets = DryRunFacets.Build(data.SourceRootCounts, OnFacetChanged);
        ShowSourceFacet = SourceFacets.Count > 0;
        DestinationFacets = DryRunFacets.Build(data.DestinationRootCounts, OnFacetChanged);
        ShowDestinationFacet = DestinationFacets.Count > 0;

        SearchText = "";
        ShowTree = false;
        Tree = [];   // mirror Clear(): the _applying guard stops the ShowTree setter from clearing a
                     // previously-built forest, which would otherwise stay retained until the next toggle.
        _applying = false;
        _visible = data.SortedKeys;
        VisibleRows = RowsFor(_visible);
    }

    public void Clear()
    {
        _rebuildGate.Cancel();   // a rebuild racing this clear must not publish the old report's rows
        IsRebuilding = false;    // the cancelled rebuild has no successor to clear the overlay
        _applying = true;
        _store = DryRunRowStore.Empty;   // drops the whole preview's columns
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
        StatusFilters = [];
        OnPropertyChanged(nameof(AnyStatusSelected));
        _applying = false;
    }

    /// <summary>The status chips for this tab, wired so toggling one re-filters the list.</summary>
    private IReadOnlyList<DryRunStatusFilter> BuildStatusFilters()
    {
        DryRunStatusFilter[] filters =
        [
            new() { Key = "untouched", Label = "Untouched", IconKey = "IconUntouched",  ColorKey = "Brush.Info",    Count = UntouchedCount },
            new() { Key = "processed", Label = "Processed", IconKey = "IconCheckmark", ColorKey = "Brush.Success", Count = ProcessedCount },
            new() { Key = "deleted",   Label = "Deleted",   IconKey = "IconTrash",     ColorKey = "Brush.Danger",  Count = DeletedCount },
        ];
        foreach (DryRunStatusFilter f in filters)
            f.PropertyChanged += OnStatusFilterChanged;
        return filters;
    }

    /// <summary>The selected status keys, or null when none are selected (the common case → show all).</summary>
    private HashSet<string>? SelectedStatusKeys()
    {
        HashSet<string> keys = StatusFilters.Where(f => f.IsSelected).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return keys.Count == 0 ? null : keys;
    }

    [RelayCommand]
    private void ClearStatusFilters()
    {
        _applying = true;
        foreach (DryRunStatusFilter f in StatusFilters) f.IsSelected = false;
        _applying = false;
        OnPropertyChanged(nameof(AnyStatusSelected));
        RequestRebuild(debounce: false);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_applying) return;
        RequestRebuild(debounce: true);
    }

    partial void OnShowTreeChanged(bool value)
    {
        if (_applying) return;
        if (!value)
        {
            // Release the forest immediately and leave VisibleRows untouched (keeps the list's
            // scroll position). An in-flight rebuild still publishes its filter result but skips
            // its tree publish — RebuildAsync re-checks ShowTree.
            Tree = [];
            return;
        }
        RequestRebuild(debounce: false);
    }

    private void OnFacetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        if (e.PropertyName == nameof(DryRunFacetRow.IsSelected))
        {
            OnPropertyChanged(nameof(ActiveFilterCount));
            OnPropertyChanged(nameof(FilterLabel));
            RequestRebuild(debounce: false);
        }
    }

    private void OnStatusFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        if (e.PropertyName == nameof(DryRunStatusFilter.IsSelected))
        {
            OnPropertyChanged(nameof(AnyStatusSelected));
            RequestRebuild(debounce: false);
        }
    }

    /// <summary>Schedules a rebuild, superseding any rebuild pending or in flight — rapid filter
    /// clicks coalesce and only the latest publishes. Search changes debounce; filter and tree
    /// toggles rebuild immediately.</summary>
    private void RequestRebuild(bool debounce)
    {
        PendingRebuild = RebuildAsync(debounce ? _searchDebounce : TimeSpan.Zero, _rebuildGate.Supersede());
    }

    /// <summary>One rebuild: snapshot the filter state on the UI thread, compute the visible rows
    /// (and forest) — synchronously for small reports, on the thread pool past
    /// <see cref="DryRunRebuild.SyncThreshold"/> — then publish back on the UI thread. Cancel and
    /// the pre-publish token check both happen on the UI thread, so a superseded rebuild can never
    /// publish after its successor.</summary>
    private async Task RebuildAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            RebuildInput input = new(
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                DryRunFacets.SelectedKeys(SourceFacets),
                DryRunFacets.SelectedKeys(DestinationFacets),
                SelectedStatusKeys(),
                ShowTree,
                CommonRoot,
                _store,
                _all,
                _visible,
                ShowTree && Tree.Count > 0 ? DryRunTreeNode.CollectExpanded(Tree) : null,
                _actions);

            RebuildResult result;
            if (input.All.Length <= DryRunRebuild.SyncThreshold)
            {
                result = ComputeRebuild(input, ct);
            }
            else
            {
                IsRebuilding = true;   // cleared by whichever current rebuild publishes (or by Load/Clear)
                result = await Task.Run(() => ComputeRebuild(input, ct), ct);
            }

            // The gate makes cancel-vs-publish atomic, so a superseded rebuild can never publish
            // after its successor.
            _rebuildGate.TryPublish(ct, () =>
            {
                IsRebuilding = false;
                // Republish the bound list only when the visible set actually changed, so an
                // unchanged rebuild leaves the ListBox's scroll position and selection alone. (The
                // old code got this by republishing the same List instance; keys are value-compared
                // now, so the decision has to be explicit.)
                if (!DryRunRebuild.SameKeys(result.Visible, _visible))
                {
                    _visible = result.Visible;
                    VisibleRows = RowsFor(_visible);
                }
                if (input.ShowTree && ShowTree)
                {
                    IReadOnlyList<DryRunTreeNode> forest = result.Forest!;
                    // The forest baked in an expansion snapshot taken before the (possibly long)
                    // off-thread build — re-apply the live tree's state so expand/collapse the user
                    // did meanwhile survives the swap.
                    if (Tree.Count > 0)
                        DryRunTreeNode.ApplyExpanded(forest, DryRunTreeNode.CollectExpanded(Tree));
                    Tree = forest;
                }
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer filter/search change */ }
        catch (Exception ex)
        {
            // Last resort: a rebuild fault becomes a logged error rather than an unobserved task fault.
            Log.Error(ex, "Failed to rebuild the Sources tab");
            _rebuildGate.TryPublish(ct, () => IsRebuilding = false);   // only if still the current rebuild
        }
    }

    private sealed record RebuildInput(
        string? Term,
        HashSet<string>? SourceRoots,
        HashSet<string>? DestinationRoots,
        HashSet<string>? StatusKeys,
        bool ShowTree,
        string? CommonRoot,
        DryRunRowStore Store,
        int[] All,
        int[] CurrentVisible,
        IReadOnlySet<string>? ExpandedPaths,
        IDryRunItemActions? Actions);

    private sealed record RebuildResult(int[] Visible, IReadOnlyList<DryRunTreeNode>? Forest);

    /// <summary>Pure: filters the presorted rows (order-preserving — the sort was paid once at
    /// load) and, in tree mode, builds the forest. Runs on the thread pool for large reports, so it
    /// touches nothing but its snapshot. Every predicate reads the store's columns directly, so a
    /// filter pass over 500k rows allocates one int array and no row objects at all.</summary>
    private static RebuildResult ComputeRebuild(RebuildInput input, CancellationToken ct)
    {
        DryRunRowStore store = input.Store;
        int[] visible;
        if (input.Term is null && input.SourceRoots is null && input.DestinationRoots is null && input.StatusKeys is null)
        {
            visible = input.All;   // no filter active — the presorted whole set IS the view
        }
        else
        {
            int[] kept = new int[input.All.Length];
            int count = 0;
            for (int i = 0; i < input.All.Length; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                int key = input.All[i];
                if (input.SourceRoots is not null && !input.SourceRoots.Contains(store.SourceRoot(key)))
                    continue;
                if (input.DestinationRoots is HashSet<string> destinations && !HasTargetUnder(store, key, destinations))
                    continue;
                if (input.StatusKeys is HashSet<string> statuses && !MatchesStatus(store, key, statuses))
                    continue;
                if (input.Term is string term && !Matches(store, key, term))
                    continue;
                kept[count++] = key;
            }
            visible = count == kept.Length ? kept : kept[..count];
            if (DryRunRebuild.SameKeys(visible, input.CurrentVisible))
                visible = input.CurrentVisible;
        }
        ct.ThrowIfCancellationRequested();
        return new(visible, input.ShowTree ? BuildTree(store, visible, input.CommonRoot, input.ExpandedPaths, input.Actions) : null);

        static bool HasTargetUnder(DryRunRowStore store, int key, IReadOnlySet<string> roots)
        {
            int end = store.TargetEnd(key);
            for (int p = store.TargetStart(key); p < end; p++)
            {
                if (roots.Contains(store.OpRoot(p)))
                    return true;
            }
            return false;
        }

        static bool MatchesStatus(DryRunRowStore store, int key, HashSet<string> statuses)
        {
            OperationKind kind = store.SourceKind(key);
            return (statuses.Contains("untouched") && kind is OperationKind.SkippedByFilter or OperationKind.SkippedUnchanged)
                || (statuses.Contains("processed") && kind is OperationKind.Processed)
                || (statuses.Contains("deleted") && store.IsSourceDisposalDestructive(key));
        }

        static bool Matches(DryRunRowStore store, int key, string term)
        {
            if (DryRunPaths.PathContains(store.SourceDirPath(key), store.SourceFileName(key), term))
                return true;
            int end = store.TargetEnd(key);
            for (int p = store.TargetStart(key); p < end; p++)
            {
                if (DryRunPaths.PathContains(store.OpDirPath(p), store.OpFileName(p), term))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Builds the forest over source-file <em>indices</em> rather than row objects: the
    /// per-directory buckets <c>BuildForest</c> retains for its lazy leaf factories then cost 4 bytes
    /// a file instead of a row reference plus the row.</summary>
    private static IReadOnlyList<DryRunTreeNode> BuildTree(
        DryRunRowStore store, int[] keys, string? commonRoot, IReadOnlySet<string>? expandedPaths,
        IDryRunItemActions? actions) =>
        DryRunTreeNode.BuildForest(keys, store.SourceDirPath, store.SourceFileName, i =>
        {
            List<(string, int)> cats = [];
            OperationKind kind = store.SourceKind(i);
            if (kind is OperationKind.SkippedByFilter or OperationKind.SkippedUnchanged) cats.Add(("untouched", 1));
            if (kind is OperationKind.Processed) cats.Add(("processed", 1));
            if (store.IsSourceDisposalDestructive(i)) cats.Add(("deleted", 1));
            return cats;
            // A prior forest (expandedPaths non-null) → restore its expansion; the very first build
            // → null so BuildForest's top-level auto-expand heuristic applies.
        }, TreeSpecs, commonRoot, expandedPaths, store.SourceSize, actions);
}

/// <summary>The Destinations tab: the resulting destination structure — files a run would add
/// (New), overwrite (Overwritten), leave in place (Untouched), or (under Mirror) delete (Deleted) —
/// filterable by source and by destination, as a flat list or a rolled-up path tree.</summary>
public sealed partial class DryRunDestinationsTab : ViewModelBase
{
    private static readonly IReadOnlyList<TreePillSpec> TreeSpecs =
    [
        new("untouched", "untouched", "IconUntouched", "Brush.Info"),
        new("new", "new", "IconAdd", "Brush.Success"),
        new("overwritten", "overwritten", "IconOverwrite", "Brush.Warning"),
        new("deleted", "deleted", "IconTrash", "Brush.Danger"),
        new("unknown", "unknown", "IconQuestion", "Brush.Muted"),
    ];

    private const string NoSourceKey = "[From Destination]";

    private readonly TimeSpan _searchDebounce;
    private readonly DryRunRebuildGate _rebuildGate = new();
    private bool _applying;
    // Rows are store keys: a source-file index for a grouped row, ~position for a destination
    // operation with no originating source. _all is presorted by DryRunSort at load and never mutated
    // after; _visible is an order-preserving subset, which unlike the Sources tab can also carry a
    // per-row slice of surviving destinations (this tab filters at the entry level).
    private DryRunRowStore _store = DryRunRowStore.Empty;
    private int[] _all = [];
    private VisibleRowSet _visible = VisibleRowSet.Empty;

    /// <summary>The rows a rebuild published: their keys, plus — when an entry-level filter kept only
    /// part of some fan-out — a CSR of the destination positions that survived.</summary>
    private sealed record VisibleRowSet(int[] Keys, int[]? SliceStarts, int[]? SlicePositions)
    {
        public static readonly VisibleRowSet Empty = new([], null, null);

        /// <summary>Whether this set shows exactly what <paramref name="other"/> shows. Slices are
        /// built fresh per rebuild, so they are compared elementwise rather than by reference.</summary>
        public bool Same(VisibleRowSet other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (!DryRunRebuild.SameKeys(Keys, other.Keys))
                return false;
            if (SlicePositions is null || other.SlicePositions is null)
                return SlicePositions is null && other.SlicePositions is null;
            return DryRunRebuild.SameKeys(SliceStarts!, other.SliceStarts!)
                && DryRunRebuild.SameKeys(SlicePositions, other.SlicePositions);
        }
    }

    /// <summary>Clipboard/shell actions for the row + tree right-click menus (null in headless tests).</summary>
    private readonly IDryRunItemActions? _actions;

    public DryRunDestinationsTab(TimeSpan searchDebounce, IDryRunItemActions? actions = null)
    {
        _searchDebounce = searchDebounce;
        _actions = actions;
    }

    /// <summary>Wraps a visible set as the bound row list — handles built per indexer access.</summary>
    private DryRunRowList<DryRunDestinationRow> RowsFor(VisibleRowSet rows)
    {
        DryRunRowStore store = _store;
        IDryRunItemActions? actions = _actions;
        int[] keys = rows.Keys;
        int[]? starts = rows.SliceStarts;
        int[]? positions = rows.SlicePositions;
        return new DryRunRowList<DryRunDestinationRow>(
            keys,
            positions is null
                ? i => new DryRunDestinationRow(store, keys[i]) { Actions = actions }
                : i => new DryRunDestinationRow(store, keys[i], positions, starts![i], starts[i + 1]) { Actions = actions },
            static r => r.Key);
    }

    /// <summary>The in-flight (or last completed) rebuild. Rebuilds over
    /// <see cref="DryRunRebuild.SyncThreshold"/> rows run on the thread pool; tests that cross that
    /// size (or use a non-zero debounce) await this before asserting.</summary>
    internal Task PendingRebuild { get; private set; } = Task.CompletedTask;

    /// <summary>True while a rebuild is computing on the thread pool — drives the rows area's
    /// loading overlay. Synchronous small-report rebuilds never set it, so it appears exactly when
    /// there is a visible delay to explain.</summary>
    [ObservableProperty] public partial bool IsRebuilding { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<DryRunDestinationRow> VisibleRows { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DryRunTreeNode> Tree { get; private set; } = [];

    /// <summary>The TreeDataGrid source the view binds to, rebuilt from <see cref="Tree"/> whenever the
    /// forest changes (null while empty so the grid shows nothing).</summary>
    [ObservableProperty] public partial HierarchicalTreeDataGridSource<DryRunTreeNode>? TreeSource { get; private set; }

    partial void OnTreeChanged(IReadOnlyList<DryRunTreeNode> value)
    {
        // Dispose the previous grid source after the new value has propagated to the bound
        // TreeDataGrid. HierarchicalTreeDataGridSource owns a realized HierarchicalRows cache whose
        // per-row wrappers subscribe to each node's IsExpanded — never disposing it lets the control's
        // realization keep the whole old forest alive, so every tree toggle (and every re-run after
        // one) stacks another forest on the heap. Assign first, dispose second, so the grid never
        // briefly binds to a disposed source.
        HierarchicalTreeDataGridSource<DryRunTreeNode>? previous = TreeSource;
        TreeSource = value.Count == 0 ? null : DryRunTreeNode.BuildSource(value);
        previous?.Dispose();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(FilterLabel))]
    public partial IReadOnlyList<DryRunFacetRow> SourceFacets { get; private set; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(FilterLabel))]
    public partial IReadOnlyList<DryRunFacetRow> DestinationFacets { get; private set; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnyFacet))]
    public partial bool ShowSourceFacet { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnyFacet))]
    public partial bool ShowDestinationFacet { get; private set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ShowTree { get; set; }

    /// <summary>Whether this tab has any facet filter to offer — gates the toolbar's Filter button.</summary>
    public bool ShowAnyFacet => ShowSourceFacet || ShowDestinationFacet;

    /// <summary>How many facet values are currently deselected (i.e. actively filtering the view).
    /// Drives the Filter button's badge so an active filter is visible once the facets are collapsed
    /// into the flyout.</summary>
    public int ActiveFilterCount =>
        SourceFacets.Count(f => !f.IsSelected) + DestinationFacets.Count(f => !f.IsSelected);

    /// <summary>The Filter button caption, carrying the active-filter count when non-zero.</summary>
    public string FilterLabel => ActiveFilterCount > 0 ? $"Filter ({ActiveFilterCount})" : "Filter";

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

    /// <summary>The clickable summary chips (Untouched / New / Overwritten / Deleted). None selected →
    /// the list shows everything; selecting one or more narrows it to destinations of that kind.</summary>
    [ObservableProperty] public partial IReadOnlyList<DryRunStatusFilter> StatusFilters { get; private set; } = [];

    /// <summary>Whether any status chip is selected — drives the clear (✕) button's visibility.</summary>
    public bool AnyStatusSelected => StatusFilters.Any(f => f.IsSelected);

    /// <summary>Everything <see cref="Load(LoadData)"/> assigns, precomputed by
    /// <see cref="ComputeLoad"/> — pure data, safe to build off the UI thread.</summary>
    internal sealed record LoadData(
        DryRunRowStore Store,
        int[] SortedKeys,
        string? CommonRoot,
        int UntouchedCount,
        int NewCount,
        int OverwrittenCount,
        int DeletedCount,
        Dictionary<string, int> SourceRootCounts,
        Dictionary<string, int> DestinationRootCounts);

    /// <summary>The pure, thread-safe half of loading: one pass for the status and facet counts,
    /// then the single sort the tab ever pays. The sort key is fixed per row (the source's — or for
    /// no-source rows the sole entry's — path relative to its root; entry filtering can't change
    /// it), so sorting once here makes every later rebuild a linear order-preserving filter.</summary>
    internal static LoadData ComputeLoad(DryRunRowStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        string? commonRoot = store.DestinationCommonRoot;

        // The row set: one row per source file that produced destination operations, then one per
        // operation with no originating source (a Mirror orphan, a kept-around conflict original, a
        // pre-existing untouched file). The store's CSR already has both groups contiguous.
        List<int> keyList = new(store.SourceCount);
        for (int i = 0; i < store.SourceCount; i++)
        {
            if (store.TargetStart(i) != store.TargetEnd(i))
                keyList.Add(i);
        }
        for (int p = store.NoSourceStart; p < store.OperationCount; p++)
            keyList.Add(~p);
        int[] rowKeys = [.. keyList];

        int untouched = 0, added = 0, overwritten = 0, deleted = 0;
        Dictionary<string, int> sourceCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> destinationCounts = new(StringComparer.OrdinalIgnoreCase);
        List<string> rowRoots = [];   // reused per row
        for (int i = 0; i < rowKeys.Length; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            int key = rowKeys[i];
            string sourceKey = key >= 0 ? store.SourceRoot(key) : NoSourceKey;
            sourceCounts[sourceKey] = sourceCounts.GetValueOrDefault(sourceKey) + 1;
            rowRoots.Clear();
            (int start, int end) = EntryRange(store, key);
            for (int p = start; p < end; p++)
            {
                // Counts are over the individual destination entries (one per resulting path), not
                // the grouped rows, so replicating one file to N targets still counts as N.
                switch (DestinationKindMap.Map(store.OpKind(p)))
                {
                    case DestinationRowKind.Untouched: untouched++; break;
                    case DestinationRowKind.New: added++; break;
                    case DestinationRowKind.Overwritten: overwritten++; break;
                    case DestinationRowKind.Deleted: deleted++; break;
                    default: break;
                }
                string root = store.OpRoot(p);
                if (rowRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                    continue;   // the facet counts rows, so dedupe roots per row
                rowRoots.Add(root);
                destinationCounts[root] = destinationCounts.GetValueOrDefault(root) + 1;
            }
        }
        ct.ThrowIfCancellationRequested();

        // Order by the source file's relative path (grouped rows) or the destination's (no-source
        // rows) so the preview stays comparable to the Sources tab. Keys are computed once per row up
        // front, then an index array is sorted (key → root → original position); the final index
        // tiebreak keeps Array.Sort stable, matching the prior LINQ OrderBy/ThenBy.
        int n = rowKeys.Length;
        string[] keys = new string[n];
        if (n <= DryRunRebuild.SyncThreshold)
        {
            Dictionary<(string, string?), string> relDirCache = [];
            for (int j = 0; j < n; j++)
                keys[j] = SortKey(store, rowKeys[j], relDirCache);
        }
        else
        {
            // Each partition owns a lock-free relDir cache; GetRelativePath is pure so the split can't
            // change any key.
            Parallel.For(0, n, new ParallelOptions { CancellationToken = ct },
                () => new Dictionary<(string, string?), string>(),
                (j, _, cache) =>
                {
                    keys[j] = SortKey(store, rowKeys[j], cache);
                    return cache;
                },
                _ => { });
        }
        ct.ThrowIfCancellationRequested();
        int[] order = new int[n];
        for (int j = 0; j < n; j++)
            order[j] = j;
        Array.Sort(order, (a, b) =>
        {
            int c = string.Compare(keys[a], keys[b], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(RootOf(store, rowKeys[a]), RootOf(store, rowKeys[b]), StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.CompareTo(b);
        });
        int[] sorted = new int[n];
        for (int j = 0; j < n; j++)
            sorted[j] = rowKeys[order[j]];
        ct.ThrowIfCancellationRequested();
        return new(store, sorted, commonRoot, untouched, added, overwritten, deleted, sourceCounts, destinationCounts);

        static string SortKey(DryRunRowStore store, int key, Dictionary<(string, string?), string> cache) =>
            key >= 0
                ? DryRunSort.RelativeKey(store.SourceDirPath(key), store.SourceFileName(key), store.SourceRoot(key), cache)
                : DryRunSort.RelativeKey(store.OpDirPath(~key), store.OpFileName(~key), store.OpRoot(~key), cache);

        static string RootOf(DryRunRowStore store, int key) =>
            key >= 0 ? store.SourceRoot(key) : store.OpRoot(~key);
    }

    /// <summary>The store CSR range of a row's destination operations — a source file's fan-out, or
    /// the single operation of a no-source row.</summary>
    private static (int Start, int End) EntryRange(DryRunRowStore store, int key) =>
        key >= 0 ? (store.TargetStart(key), store.TargetEnd(key)) : (~key, ~key + 1);

    /// <summary>Convenience for callers holding a store (tests); the app path computes off-thread via
    /// <see cref="DryRunViewModel.PrepareReport"/> and calls <see cref="Load(LoadData)"/>.</summary>
    public void Load(DryRunRowStore store) => Load(ComputeLoad(store));

    /// <summary>The UI-thread half of loading: assigns the precomputed data to the bound
    /// properties. A fresh load has no active filters and the rows arrive presorted, so the whole
    /// set IS the visible list — no rebuild.</summary>
    internal void Load(LoadData data)
    {
        _rebuildGate.Cancel();   // a rebuild racing this load must not publish the old report's rows
        IsRebuilding = false;    // the cancelled rebuild has no successor to clear the overlay
        _applying = true;
        _store = data.Store;
        _all = data.SortedKeys;
        CommonRoot = data.CommonRoot;
        UntouchedCount = data.UntouchedCount;
        NewCount = data.NewCount;
        OverwrittenCount = data.OverwrittenCount;
        DeletedCount = data.DeletedCount;
        StatusFilters = BuildStatusFilters();

        SourceFacets = DryRunFacets.Build(data.SourceRootCounts, OnFacetChanged);
        ShowSourceFacet = SourceFacets.Count > 0;
        DestinationFacets = DryRunFacets.Build(data.DestinationRootCounts, OnFacetChanged);
        ShowDestinationFacet = DestinationFacets.Count > 0;

        SearchText = "";
        ShowTree = false;
        Tree = [];   // mirror Clear(): the _applying guard stops the ShowTree setter from clearing a
                     // previously-built forest, which would otherwise stay retained until the next toggle.
        _applying = false;
        _visible = new VisibleRowSet(data.SortedKeys, null, null);
        VisibleRows = RowsFor(_visible);
    }

    public void Clear()
    {
        _rebuildGate.Cancel();   // a rebuild racing this clear must not publish the old report's rows
        IsRebuilding = false;    // the cancelled rebuild has no successor to clear the overlay
        _applying = true;
        _store = DryRunRowStore.Empty;   // drops the whole preview's columns
        _all = [];
        _visible = VisibleRowSet.Empty;
        VisibleRows = [];
        Tree = [];
        CommonRoot = null;
        SourceFacets = DestinationFacets = [];
        ShowSourceFacet = ShowDestinationFacet = false;
        SearchText = "";
        ShowTree = false;
        UntouchedCount = NewCount = OverwrittenCount = DeletedCount = 0;
        StatusFilters = [];
        OnPropertyChanged(nameof(AnyStatusSelected));
        _applying = false;
    }

    /// <summary>The status chips for this tab, wired so toggling one re-filters the list.</summary>
    private IReadOnlyList<DryRunStatusFilter> BuildStatusFilters()
    {
        DryRunStatusFilter[] filters =
        [
            new() { Key = "untouched",   Label = "Untouched",   IconKey = "IconUntouched", ColorKey = "Brush.Info",    Count = UntouchedCount },
            new() { Key = "new",         Label = "New",         IconKey = "IconAdd",       ColorKey = "Brush.Success", Count = NewCount },
            new() { Key = "overwritten", Label = "Overwritten", IconKey = "IconOverwrite", ColorKey = "Brush.Warning", Count = OverwrittenCount },
            new() { Key = "deleted",     Label = "Deleted",     IconKey = "IconTrash",     ColorKey = "Brush.Danger",  Count = DeletedCount },
        ];
        foreach (DryRunStatusFilter f in filters)
            f.PropertyChanged += OnStatusFilterChanged;
        return filters;
    }

    /// <summary>The selected status keys, or null when none are selected (the common case → show all).</summary>
    private HashSet<string>? SelectedStatusKeys()
    {
        HashSet<string> keys = StatusFilters.Where(f => f.IsSelected).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return keys.Count == 0 ? null : keys;
    }

    /// <summary>The status-chip key a destination entry filters under (mirrors the summary vocabulary);
    /// Unknown entries carry no summary chip, so they never survive an active status filter.</summary>
    private static string StatusKey(DestinationRowKind kind) => kind switch
    {
        DestinationRowKind.Untouched => "untouched",
        DestinationRowKind.New => "new",
        DestinationRowKind.Overwritten => "overwritten",
        DestinationRowKind.Deleted => "deleted",
        _ => "unknown",
    };

    [RelayCommand]
    private void ClearStatusFilters()
    {
        _applying = true;
        foreach (DryRunStatusFilter f in StatusFilters) f.IsSelected = false;
        _applying = false;
        OnPropertyChanged(nameof(AnyStatusSelected));
        RequestRebuild(debounce: false);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_applying) return;
        RequestRebuild(debounce: true);
    }

    partial void OnShowTreeChanged(bool value)
    {
        if (_applying) return;
        if (!value)
        {
            // Release the forest immediately and leave VisibleRows untouched (keeps the list's
            // scroll position). An in-flight rebuild still publishes its filter result but skips
            // its tree publish — RebuildAsync re-checks ShowTree.
            Tree = [];
            return;
        }
        RequestRebuild(debounce: false);
    }

    private void OnFacetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        if (e.PropertyName == nameof(DryRunFacetRow.IsSelected))
        {
            OnPropertyChanged(nameof(ActiveFilterCount));
            OnPropertyChanged(nameof(FilterLabel));
            RequestRebuild(debounce: false);
        }
    }

    private void OnStatusFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        if (e.PropertyName == nameof(DryRunStatusFilter.IsSelected))
        {
            OnPropertyChanged(nameof(AnyStatusSelected));
            RequestRebuild(debounce: false);
        }
    }

    /// <summary>Schedules a rebuild, superseding any rebuild pending or in flight — rapid filter
    /// clicks coalesce and only the latest publishes. Search changes debounce; filter and tree
    /// toggles rebuild immediately.</summary>
    private void RequestRebuild(bool debounce)
    {
        PendingRebuild = RebuildAsync(debounce ? _searchDebounce : TimeSpan.Zero, _rebuildGate.Supersede());
    }

    /// <summary>One rebuild: snapshot the filter state on the UI thread, compute the visible rows
    /// (and forest) — synchronously for small reports, on the thread pool past
    /// <see cref="DryRunRebuild.SyncThreshold"/> — then publish back on the UI thread. Cancel and
    /// the pre-publish token check both happen on the UI thread, so a superseded rebuild can never
    /// publish after its successor.</summary>
    private async Task RebuildAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            RebuildInput input = new(
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                DryRunFacets.SelectedKeys(SourceFacets),
                DryRunFacets.SelectedKeys(DestinationFacets),
                SelectedStatusKeys(),
                ShowTree,
                CommonRoot,
                _store,
                _all,
                _visible,
                ShowTree && Tree.Count > 0 ? DryRunTreeNode.CollectExpanded(Tree) : null,
                _actions);

            RebuildResult result;
            if (input.All.Length <= DryRunRebuild.SyncThreshold)
            {
                result = ComputeRebuild(input, ct);
            }
            else
            {
                IsRebuilding = true;   // cleared by whichever current rebuild publishes (or by Load/Clear)
                result = await Task.Run(() => ComputeRebuild(input, ct), ct);
            }

            // The gate makes cancel-vs-publish atomic, so a superseded rebuild can never publish
            // after its successor.
            _rebuildGate.TryPublish(ct, () =>
            {
                IsRebuilding = false;
                // Republish the bound list only when the visible set actually changed, so an
                // unchanged rebuild leaves the ListBox's scroll position and selection alone. Entry
                // slices count as part of "changed": the same rows showing different destinations is
                // a different view.
                if (!result.Visible.Same(_visible))
                {
                    _visible = result.Visible;
                    VisibleRows = RowsFor(_visible);
                }
                if (input.ShowTree && ShowTree)
                {
                    IReadOnlyList<DryRunTreeNode> forest = result.Forest!;
                    // The forest baked in an expansion snapshot taken before the (possibly long)
                    // off-thread build — re-apply the live tree's state so expand/collapse the user
                    // did meanwhile survives the swap.
                    if (Tree.Count > 0)
                        DryRunTreeNode.ApplyExpanded(forest, DryRunTreeNode.CollectExpanded(Tree));
                    Tree = forest;
                }
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer filter/search change */ }
        catch (Exception ex)
        {
            // Last resort: a rebuild fault becomes a logged error rather than an unobserved task fault.
            Log.Error(ex, "Failed to rebuild the Destinations tab");
            _rebuildGate.TryPublish(ct, () => IsRebuilding = false);   // only if still the current rebuild
        }
    }

    private sealed record RebuildInput(
        string? Term,
        HashSet<string>? SourceRoots,
        HashSet<string>? DestinationRoots,
        HashSet<string>? StatusKeys,
        bool ShowTree,
        string? CommonRoot,
        DryRunRowStore Store,
        int[] All,
        VisibleRowSet CurrentVisible,
        IReadOnlySet<string>? ExpandedPaths,
        IDryRunItemActions? Actions);

    private sealed record RebuildResult(VisibleRowSet Visible, IReadOnlyList<DryRunTreeNode>? Forest);

    /// <summary>Pure: filters the presorted rows (order-preserving — the sort was paid once at
    /// load) and, in tree mode, builds the forest. Runs on the thread pool for large reports, so it
    /// touches nothing but its snapshot.</summary>
    private static RebuildResult ComputeRebuild(RebuildInput input, CancellationToken ct)
    {
        DryRunRowStore store = input.Store;
        VisibleRowSet visible;
        if (input.Term is null && input.SourceRoots is null && input.DestinationRoots is null && input.StatusKeys is null)
        {
            visible = new VisibleRowSet(input.All, null, null);   // no filter — the presorted whole set IS the view
        }
        else
        {
            // Filter at the destination-entry level and drop rows left with nothing, so a grouped row
            // shows only the destinations that survived the destination-facet / status / search filters.
            // The source facet applies to the whole row; a search hit on the source path keeps all entries.
            // The survivors are collected as a CSR of store positions rather than as rebuilt rows — the
            // shape that lets a filtered view cost two int arrays instead of a row object per hit.
            List<int> keptKeys = [];
            List<int> starts = [0];
            List<int> positions = [];
            bool anySliced = false;
            for (int i = 0; i < input.All.Length; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                int key = input.All[i];
                if (input.SourceRoots is not null
                    && !input.SourceRoots.Contains(key >= 0 ? store.SourceRoot(key) : NoSourceKey))
                    continue;

                // A search hit on the source path keeps every destination; otherwise each entry is
                // tested individually.
                bool sourceMatches = input.Term is null
                    || (key >= 0 && DryRunPaths.PathContains(store.SourceDirPath(key), store.SourceFileName(key), input.Term));

                (int start, int end) = EntryRange(store, key);
                int before = positions.Count;
                for (int p = start; p < end; p++)
                {
                    if (input.DestinationRoots is HashSet<string> destinations && !destinations.Contains(store.OpRoot(p)))
                        continue;
                    if (input.StatusKeys is HashSet<string> statuses
                        && !statuses.Contains(StatusKey(DestinationKindMap.Map(store.OpKind(p)))))
                        continue;
                    if (!sourceMatches && !DryRunPaths.PathContains(store.OpDirPath(p), store.OpFileName(p), input.Term!))
                        continue;
                    positions.Add(p);
                }
                int kept = positions.Count - before;
                if (kept == 0)
                    continue;   // every destination filtered out — the row disappears with them
                anySliced |= kept != end - start;
                keptKeys.Add(key);
                starts.Add(positions.Count);
            }
            // Whole rows only → drop the slice entirely, so the common case stores just the keys and
            // rows read their range straight off the store.
            visible = anySliced
                ? new VisibleRowSet([.. keptKeys], [.. starts], [.. positions])
                : new VisibleRowSet([.. keptKeys], null, null);
            if (visible.Same(input.CurrentVisible))
                visible = input.CurrentVisible;
        }
        ct.ThrowIfCancellationRequested();
        return new(visible, input.ShowTree ? BuildTree(store, visible, input.CommonRoot, input.ExpandedPaths, input.Actions) : null);
    }

    /// <summary>Builds the forest over destination CSR <em>positions</em> rather than entry objects —
    /// one leaf per resulting path, the tree is per-file and unchanged.</summary>
    private static IReadOnlyList<DryRunTreeNode> BuildTree(
        DryRunRowStore store, VisibleRowSet rows, string? commonRoot, IReadOnlySet<string>? expandedPaths,
        IDryRunItemActions? actions) =>
        DryRunTreeNode.BuildForest(EntryPositions(store, rows), store.OpDirPath, store.OpFileName, p =>
        {
            string kind = DestinationKindMap.Map(store.OpKind(p)) switch
            {
                DestinationRowKind.Untouched => "untouched",
                DestinationRowKind.New => "new",
                DestinationRowKind.Overwritten => "overwritten",
                DestinationRowKind.Deleted => "deleted",
                _ => "unknown",
            };
            return new[] { (kind, 1) };
        }, TreeSpecs, commonRoot, expandedPaths, store.OpSize, actions);

    /// <summary>Every destination position the visible rows show, flattened.</summary>
    private static IEnumerable<int> EntryPositions(DryRunRowStore store, VisibleRowSet rows)
    {
        if (rows.SlicePositions is { } positions)
            return positions;
        return Flatten();

        IEnumerable<int> Flatten()
        {
            foreach (int key in rows.Keys)
            {
                (int start, int end) = EntryRange(store, key);
                for (int p = start; p < end; p++)
                    yield return p;
            }
        }
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

    /// <summary>Any destination volume is at risk (amber worst-case or red likely-won't-fit) — the
    /// Storage panel's header carries a badge and auto-expands when this is true.</summary>
    public bool HasWarning => Volumes.Any(v => v.HasWarning);

    /// <summary>The one-line summary shown in the collapsed Storage panel's header.</summary>
    public string SummaryText => $"{TransferredText} to transfer · {NetChangeText} net change";

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

    /// <summary>The clipboard/shell actions behind the row/node right-click menus; null in
    /// headless view-model tests, where the menu commands simply no-op.</summary>
    private readonly IDryRunItemActions? _actions;

    public DryRunViewModel(IIpcGateway gateway, IDryRunItemActions? actions = null, TimeSpan? searchDebounce = null)
    {
        _gateway = gateway;
        _actions = actions;
        TimeSpan debounce = searchDebounce ?? TimeSpan.FromMilliseconds(200);
        Sources = new DryRunSourcesTab(debounce, actions);
        Destinations = new DryRunDestinationsTab(debounce, actions);
    }

    public DryRunSourcesTab Sources { get; }
    public DryRunDestinationsTab Destinations { get; }

    [ObservableProperty] public partial Guid? ProfileId { get; set; }

    /// <summary>Supplied by the shell: builds the editor's current draft (unsaved edits) for the run,
    /// or returns a parse error to surface instead. Null in tests that drive ProfileId directly.</summary>
    public Func<(Profile? profile, string? error)>? DraftProvider { get; set; }

    /// <summary>True whenever a profile is open in the editor — new or existing — so a dry run can be
    /// launched without first saving. Drives <see cref="CanRun"/> (and the Dry Run tab's enabled
    /// state) in place of a non-null persisted id.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial bool HasEditableProfile { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "";
    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial string GeneratedAtText { get; set; } = "";
    [ObservableProperty] public partial bool WasTruncated { get; set; }
    [ObservableProperty] public partial string TruncationNotice { get; set; } = "";

    /// <summary>Live phase/count caption shown beside the run buttons while a dry run works
    /// ("Scanning sources… 12,345 files found" → destinations → "Building the lists…"). The view
    /// gates its visibility on <c>RunCommand.IsRunning</c>, so a late progress post after the run
    /// ends is harmless.</summary>
    [ObservableProperty] public partial string RunStatusText { get; set; } = "";

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

    public bool CanRun => HasEditableProfile;

    // ── Destination-scan choice behind the split run button ───────────────────────────────────
    /// <summary>The scan choice the primary run button will use. Defaults to the profile's
    /// <c>ScanDestination</c> setting via <see cref="ApplySyncSettings"/>; the dropdown overrides it
    /// for subsequent run(s) without touching the saved profile. Forced true in Mirror.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonLabel))]
    public partial bool RunWithDestinationScan { get; set; }

    /// <summary>False in Mirror, where the destination sweep is mandatory (it is the only source of
    /// Deleted-orphan previews) — so the "No Destination Scan" dropdown item is disabled.</summary>
    [ObservableProperty] public partial bool CanChooseNoScan { get; set; } = true;

    /// <summary>The two run-button / dropdown captions, defined once so the split button and its menu
    /// items can never drift apart.</summary>
    public string WithScanLabel => "Dry Run (With Destination Scan)";
    public string NoScanLabel => "Dry Run (No Destination Scan)";

    /// <summary>Primary run-button caption reflecting the current scan choice.</summary>
    public string RunButtonLabel => RunWithDestinationScan ? WithScanLabel : NoScanLabel;

    /// <summary>Resets the run button's scan choice to the profile's default whenever the editor's
    /// sync mode or ScanDestination setting changes (and on profile load). Mirror forces the scan on
    /// and locks out the "No scan" option; AdditiveArchive follows the profile setting.</summary>
    public void ApplySyncSettings(SyncMode mode, bool profileScanDestination)
    {
        CanChooseNoScan = mode != SyncMode.Mirror;
        RunWithDestinationScan = Profile.ComputeEffectiveScanDestination(mode, profileScanDestination);
    }

    [RelayCommand]
    private void SelectWithScan() => RunWithDestinationScan = true;

    [RelayCommand]
    private void SelectNoScan()
    {
        if (CanChooseNoScan)
            RunWithDestinationScan = false;
    }

    /// <summary>Attach the editor's open profile (new or existing). Enables the run and clears any
    /// prior preview. A null id is valid for a never-saved draft — the run sends the draft inline.</summary>
    public void SetProfile(Guid? profileId, string profileName)
    {
        ProfileId = profileId;
        ProfileName = profileName;
        HasEditableProfile = true;
        ReportClosed();
        // CanRun re-raises via [NotifyPropertyChangedFor] on HasEditableProfile — no manual notify.
    }

    /// <summary>Detach: no profile is open in the editor, so the run is disabled and the preview cleared.</summary>
    public void ClearProfile()
    {
        ProfileId = null;
        ProfileName = "";
        HasEditableProfile = false;
        ReportClosed();
    }

    /// <summary>Bumped by <see cref="ClearReport"/>. A run captures it before its awaits and must
    /// not apply its result if the preview was cleared meanwhile — comparing profile ids is not
    /// enough, because saving edits to the profile re-selects it under the SAME id and the stale
    /// report (generated from the pre-edit settings) would resurrect over the cleared preview.</summary>
    private int _reportEpoch;

    [RelayCommand(IncludeCancelCommand = true)]
    public async Task RunAsync(CancellationToken ct)
    {
        // Build the editor's current draft (unsaved edits) when a provider is attached, so the run
        // previews exactly what is on screen. A parse error short-circuits before any work. Without a
        // provider (unit tests), fall back to the persisted-profile path keyed on ProfileId.
        Profile? draft = null;
        if (DraftProvider is not null)
        {
            (Profile? built, string? buildError) = DraftProvider();
            if (buildError is not null)
            {
                ErrorMessage = buildError;
                return;
            }
            // Apply the split button's per-run scan choice to the ephemeral draft only. The draft is
            // never saved, so this override does not change the profile's stored ScanDestination.
            draft = built is not null
                ? built with { ScanDestination = RunWithDestinationScan }
                : null;
        }
        Guid? runId = draft?.Id ?? ProfileId;
        if (runId is not Guid profileId)
            return;
        // Release the previous preview's rows and forest now — this is the intentional "fresh run"
        // path, so ClearReport bumps the epoch and nulls both tabs' _all/_visible/VisibleRows/Tree/
        // TreeSource. Without it the old report stays alive while PrepareReport builds the next one, so
        // consecutive runs peak at ~2x the row footprint (the Gen2-GC pressure that makes a re-run feel
        // worse). The epoch bump also supersedes any run still in flight; the guard below drops this
        // run's result if a later clear/run supersedes it in turn. This calls the state-only
        // ClearReport(), not ReportClosed() — a run is never a safe moment for UiMemoryTrim's blocking
        // collect.
        ClearReport();
        int epoch = _reportEpoch;
        // Baseline for the churn figure on the two samples below — taken after the clear so it measures
        // this run only, not the previous preview's teardown.
        long allocatedBefore = UiMemoryLog.AllocatedSnapshot();
        RunStatusText = "Scanning sources…";
        // Constructed on the UI thread, so Progress<T> captures the UI SynchronizationContext and
        // every report marshals there — the service's throttling (~10 frames/sec) bounds the load.
        var progress = new Progress<DryRunProgress>(p => RunStatusText = FormatRunStatus(p));

        try
        {
            // The run streams straight into its store: chunks fold into columns as they arrive and
            // are dropped, so the assembled report — which used to be live alongside the rows being
            // projected out of it — never exists here at all.
            DryRunRowStore store = DryRunRowStore.CreateForIngest();
            // Only wrapped when the log will actually take the line — the meter is cheap but it is pure
            // diagnostics, so the un-instrumented path stays the un-instrumented path.
            DryRunIngestMeter? meter = Log.IsEnabled(Serilog.Events.LogEventLevel.Information)
                ? new DryRunIngestMeter(store)
                : null;
            long streamAllocatedBefore = UiMemoryLog.AllocatedSnapshot();
            var run = await _gateway.DryRunAsync(profileId, meter ?? (IDryRunChunkSink)store, progress, draft, ct);
            meter?.Log(UiMemoryLog.AllocatedSnapshot() - streamAllocatedBefore);
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
            run.TryGetValue(out DryRunCompletion? completion);
            // Set unconditionally (not only via a progress frame): a fast run may finish before any
            // frame arrives, and the preparation below is real seconds of work worth labelling.
            RunStatusText = "Building the lists…";
            // Completing the store and sorting both tabs is seconds of CPU at the streamed cap, so it
            // runs on the thread pool. No ConfigureAwait(false): the continuation must resume on the
            // UI context so ApplyPrepared raises its property changes on the UI thread.
            // RunCommand.IsRunning spans the preparation, so the view's progress bar keeps
            // animating instead of the window freezing.
            long prepareAllocatedBefore = UiMemoryLog.AllocatedSnapshot();
            PreparedReport prepared = await Task.Run(() =>
            {
                store.Complete();
                return PrepareReport(store, completion!, ct);
            }, ct);
            // The third phase, logged separately from the stream's two so all of a run's allocation is
            // attributed rather than inferred. This is the one with a known shape: each tab's ComputeLoad
            // materializes a string[] of one relative-path key per row and drops it after the sort
            // (~147 MB at the 500k cap, measured by
            // PrepareReport_transient_allocation_at_streamed_cap_with_realistic_deep_paths).
            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Information))
            {
                Log.Information(
                    "Dry-run prepare allocation (store completion + both tabs' sort): {PrepareMb}MB",
                    (UiMemoryLog.AllocatedSnapshot() - prepareAllocatedBefore) >> 20);
            }
            // NOTE THE SAMPLE POINT: the store, both tabs' prepared loads AND the transient sort-key
            // arrays PrepareReport just dropped are all accounted here without a collection having been
            // forced, so this is the closest thing to the run's PEAK that can be read without
            // perturbing it. It is the only visibility we have into the transient burst — the retained
            // heap probes in DryRunViewModelMemoryTests measure after a forced full collection and
            // cannot see it at all.
            UiMemoryLog.Sample("preview-prepared (near peak)", allocatedBefore);
            if (_reportEpoch != epoch)
                return;   // the preview was cleared while running — a stale report must not apply
            ApplyPrepared(prepared);
            // What the app now sits at with a preview on screen — the number a user reports.
            UiMemoryLog.Sample("preview-applied", allocatedBefore);
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
        finally
        {
            RunStatusText = "";
        }
    }

    private static string FormatRunStatus(DryRunProgress p) => p.Phase switch
    {
        DryRunProgressPhase.ScanningSources => $"Scanning sources… {p.SourceFiles:N0} files found",
        DryRunProgressPhase.SweepingDestinations => $"Scanning destinations… {p.DestinationFiles:N0} files found",
        _ => "Building the lists…",
    };

    /// <summary>Shared by every source row with no destination ops (skipped files, ~2/3 of a typical
    /// report) — a fresh empty list per row is ~10 MB at the streamed cap.</summary>
    private static readonly IReadOnlyList<DryRunTargetRow> EmptyTargets = [];

    /// <summary>Everything <see cref="ApplyPrepared"/> assigns, precomputed by
    /// <see cref="PrepareReport"/> — pure data (the space view-model included), safe to build off
    /// the UI thread.</summary>
    internal sealed record PreparedReport(
        DryRunSourcesTab.LoadData Sources,
        DryRunDestinationsTab.LoadData Destinations,
        int TotalFiles,
        int OverwriteCount,
        int RenameCount,
        int DisposalCount,
        bool HasDestructiveActions,
        string GeneratedAtText,
        bool WasTruncated,
        string TruncationNotice,
        DryRunSpaceViewModel? Space);

    /// <summary>Synchronous prepare-and-apply from an assembled report, kept for the benchmarks and
    /// memory tests that gauge the whole projection; the app path streams straight into a store and
    /// runs <see cref="PrepareReport(DryRunRowStore, DryRunCompletion, CancellationToken)"/> on the
    /// thread pool.</summary>
    internal void ApplyReport(DryRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        DryRunRowStore store = DryRunRowStore.FromReport(report);
        ApplyPrepared(PrepareReport(store, new DryRunCompletion(report.GeneratedAt, report.Truncated, report.Space)));
    }

    /// <summary>The pure, thread-safe half of applying a run: turns the completed store into both
    /// tabs' presorted key arrays, counts and facets, plus the banner numbers. O(n log n) over up to
    /// the ~500k-file streamed cap — always run this off the UI thread for real reports.
    ///
    /// <para>Almost all of what this used to do now happens during ingest or in
    /// <see cref="DryRunRowStore.Complete"/>: the directory table is resolved as it arrives, the
    /// destination operations are grouped by their source there, and the blast-radius counts fall out
    /// of the same pass. What is left is the two sorts, which are genuinely per-tab.</para></summary>
    internal static PreparedReport PrepareReport(
        DryRunRowStore store, DryRunCompletion completion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(completion);

        // The two tabs' loads share no mutable state (the store is immutable once completed), so run
        // their independent O(n log n) sorts concurrently above the sync threshold. Below it they run
        // inline (no Task/Parallel overhead) so small reports and tests stay deterministic.
        DryRunSourcesTab.LoadData sourcesLoad;
        DryRunDestinationsTab.LoadData destinationsLoad;
        if (store.SourceCount <= DryRunRebuild.SyncThreshold)
        {
            sourcesLoad = DryRunSourcesTab.ComputeLoad(store, ct);
            destinationsLoad = DryRunDestinationsTab.ComputeLoad(store, ct);
        }
        else
        {
            DryRunSourcesTab.LoadData? s = null;
            DryRunDestinationsTab.LoadData? d = null;
            Parallel.Invoke(new ParallelOptions { CancellationToken = ct },
                () => s = DryRunSourcesTab.ComputeLoad(store, ct),
                () => d = DryRunDestinationsTab.ComputeLoad(store, ct));
            sourcesLoad = s!;
            destinationsLoad = d!;
        }

        return new PreparedReport(
            sourcesLoad,
            destinationsLoad,
            TotalFiles: store.SourceCount,
            OverwriteCount: store.OverwriteCount,
            RenameCount: store.RenameCount,
            DisposalCount: store.DisposalCount,
            HasDestructiveActions:
                store.OverwriteCount > 0 || store.DisposalCount > 0 || store.AnyDestinationDeleted,
            GeneratedAtText: $"Generated {completion.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            WasTruncated: completion.Truncated,
            TruncationNotice: completion.Truncated
                ? $"Report truncated: showing the first {store.SourceCount:N0} files — the scan found more. Destination deletions are not shown for a truncated report. Use the filters to narrow the view."
                : "",
            Space: completion.Space is { Volumes.Count: > 0 } projection
                ? new DryRunSpaceViewModel(projection)
                : null);
    }


    /// <summary>The UI-thread half of applying a report: assigns the precomputed data to the bound
    /// properties and hands each tab its load.</summary>
    internal void ApplyPrepared(PreparedReport prepared)
    {
        TotalFiles = prepared.TotalFiles;
        OverwriteCount = prepared.OverwriteCount;
        RenameCount = prepared.RenameCount;
        DisposalCount = prepared.DisposalCount;
        HasDestructiveActions = prepared.HasDestructiveActions;
        GeneratedAtText = prepared.GeneratedAtText;
        WasTruncated = prepared.WasTruncated;
        TruncationNotice = prepared.TruncationNotice;
        Space = prepared.Space;
        Sources.Load(prepared.Sources);
        Destinations.Load(prepared.Destinations);
        HasReport = true;
    }

    /// <summary>Clears the report's bound state — rows, counts, error, space projection, epoch bump —
    /// unconditionally. Safe to call from all three transitions that need a blank report: profile
    /// selection/deselection and the start of every run. Carries no side effect beyond state: it must
    /// never trigger the blocking memory trim (see <see cref="ReportClosed"/> for that), because
    /// <see cref="RunAsync"/> calls this immediately before scanning and a run is never a safe moment
    /// for a blocking two-pass GC.Collect on the UI thread.</summary>
    private void ClearReport()
    {
        _reportEpoch++;   // invalidates any report still being prepared or awaited
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

    /// <summary>Call when the user is genuinely done with the current preview and not about to
    /// immediately start a new run — profile selection and deselection in the editor. Clears the
    /// report's state via <see cref="ClearReport"/> and, only if a report was actually showing,
    /// additionally returns the memory it was holding via <see cref="UiMemoryTrim"/>. Deliberately NOT
    /// called from <see cref="RunAsync"/>: a run's own <c>ClearReport()</c> call must stay state-only, or
    /// every re-run pays a blocking two-pass GC.Collect on the UI thread before the scan even starts —
    /// the freeze this split exists to prevent (see <see cref="UiMemoryTrim"/> for why the trim is only
    /// safe at a transition with no run in flight).</summary>
    private void ReportClosed()
    {
        // Only instrument a clear that actually released a preview — this also runs on profile
        // selection/deselection where there is nothing to report.
        bool hadReport = HasReport;
        ClearReport();
        if (!hadReport)
            return;
        // The rows are unrooted but not yet collected, so this reads barely changed from
        // preview-applied — that is correct, not a bug, and it is the baseline the trim is measured
        // against.
        UiMemoryLog.Sample("preview-cleared (not yet collected)");
        // Then actually give the pages back. Measured before this existed: ten seconds after a clear the
        // process still held 214 MB against a 108 MB idle, with no gen2 having run — an idle UI
        // allocates nothing, so nothing is ever collected until the next run's burst, which is why
        // consecutive runs climbed. See UiMemoryTrim for why a collect is acceptable at this one moment
        // (profile switch/deselect) and nowhere else — in particular, never from RunAsync.
        UiMemoryTrim.AfterPreviewClosedHook();
        // Ten seconds on, with the app idle: confirms the trim's effect persists rather than the
        // allocator immediately re-committing what it just released.
        Avalonia.Threading.DispatcherTimer.RunOnce(
            static () => UiMemoryLog.Sample("preview-cleared (+10s)"),
            TimeSpan.FromSeconds(10));
    }
}
