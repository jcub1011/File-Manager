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
using System.Text;
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
    // instance would break row-handle equality, which ListBox selection and container recycling rest on).
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
    /// <c>Any</c> scans it replaced: this runs per realized row while the user scrolls.
    ///
    /// <para><b>A PAGE has no destination half,</b> so its rows have an empty CSR range and fall back to
    /// the mask the plan recorded per source (<c>RunSourceItem.TargetKinds</c>). The ranking below is the
    /// same either way and lives only here — the service ships the SET of kinds and never a choice among
    /// them, so the two paths cannot disagree about which glyph wins.</para></summary>
    private OperationKind? PrimaryTargetKind
    {
        get
        {
            int start = Store.TargetStart(Index), end = Store.TargetEnd(Index);
            if (start == end)
                return FromMask(Store.SourceTargetKinds(Index));
            // Descending consequence: an Overwrite anywhere wins outright, so the scan can stop.
            OperationKind best = OperationKind.Unknown;
            int bestRank = 0;
            for (int p = start; p < end; p++)
            {
                OperationKind kind = Store.OpKind(p);
                int rank = Rank(kind);
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

    private static int Rank(OperationKind kind) => kind switch
    {
        OperationKind.Overwrite => 4,
        OperationKind.Rename => 3,
        OperationKind.New => 2,
        OperationKind.SkipConflict or OperationKind.SkipUnchanged => 1,
        _ => 0,
    };

    /// <summary>The same choice the CSR walk makes, over a recorded kind set instead of the operations.
    /// A zero mask means the plan recorded none — which is both "this file has no destinations" and "this
    /// snapshot predates the mask", and renders identically as no glyph.
    ///
    /// <para>Probed in descending <see cref="Rank"/> order rather than iterated, so it allocates nothing
    /// and stops at the first hit: this runs per realized row while the user scrolls a paged list.</para></summary>
    private static OperationKind? FromMask(int mask)
    {
        if (mask == 0)
            return null;
        if (OperationKindMask.Has(mask, OperationKind.Overwrite))
            return OperationKind.Overwrite;
        if (OperationKindMask.Has(mask, OperationKind.Rename))
            return OperationKind.Rename;
        if (OperationKindMask.Has(mask, OperationKind.New))
            return OperationKind.New;
        // Both skip kinds present as SkipConflict, matching the CSR walk's rank-1 fold.
        if (OperationKindMask.Has(mask, OperationKind.SkipConflict)
            || OperationKindMask.Has(mask, OperationKind.SkipUnchanged))
            return OperationKind.SkipConflict;
        return OperationKind.Unknown;
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

internal static class DryRunRebuild
{
    /// <summary>Plan size above which a filter change is worth an explaining overlay. A view build is a
    /// pass over the half's rows, so below this it is imperceptible and the list simply swaps; above it
    /// the user is waiting on the service and should be told.
    ///
    /// <para>It used to mean "hop to the thread pool", back when the filter pass ran here. The number is
    /// the same and so is the judgement it encodes — small plans keep their immediate
    /// click-to-result semantics, and the unit tests their synchronous asserts.</para></summary>
    public const int SyncThreshold = 5_000;
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
    /// (the common case, which skips the check entirely).
    ///
    /// <para><b>Null and empty mean different things and both are reachable.</b> Null is "keep every
    /// root"; an EMPTY list is "keep none", which is what deselecting every facet expresses — widening
    /// that back to everything would show the user exactly the rows they just excluded. The service
    /// reads it the same way.</para></summary>
    public static IReadOnlyList<string>? SelectedKeyList(IReadOnlyList<DryRunFacetRow> facets)
    {
        if (facets.Count == 0 || facets.All(f => f.IsSelected))
            return null;
        return facets.Where(f => f.IsSelected).Select(f => f.Key).ToList();
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Tabs.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The Sources tab: every scanned source file, filterable by source root, by destination root,
/// by status and by search term — all of it applied service-side, over the whole plan. Summary counts
/// (Untouched / Processed / Deleted) always reflect the whole run, never the filtered view.</summary>
public sealed partial class DryRunSourcesTab : ViewModelBase
{
    /// <summary>Rendered for a position whose page is still in flight. One store, made once and shared
    /// by every tab instance — see <see cref="DryRunRowStore.CreatePlaceholder"/> for why it holds a
    /// page's worth of rows rather than one.</summary>
    private static readonly DryRunRowStore PlaceholderStore =
        DryRunRowStore.CreatePlaceholder(PagedDryRunRowStore.PageRows);

    private readonly TimeSpan _searchDebounce;
    private readonly DryRunRebuildGate _rebuildGate = new();
    private bool _applying;

    // The open plan, as a handle rather than as rows. Nothing here is proportional to the plan's size:
    // the rows live on the service and arrive a page at a time, which is what lets a preview of a
    // five-million-file plan cost the same as one of five hundred.
    private IIpcGateway? _gateway;
    private Guid _runId;
    private string? _planSourceRoot;
    private string? _planDestinationRoot;
    /// <summary>Rows in the UNFILTERED plan half — what decides whether a rebuild is slow enough to
    /// deserve the overlay, the same judgement <see cref="DryRunRebuild.SyncThreshold"/> used to make
    /// against the resident row count.</summary>
    private int _planRowCount;
    private PagedDryRunRowStore? _paged;
    private PagedDryRunRowList<DryRunFileRow>? _rowList;

    /// <summary>Clipboard/shell actions for the row right-click menu (null in headless tests).</summary>
    private readonly IDryRunItemActions? _actions;

    public DryRunSourcesTab(TimeSpan searchDebounce, IDryRunItemActions? actions = null)
    {
        _searchDebounce = searchDebounce;
        _actions = actions;
    }

    /// <summary>Opens a window onto one view of the plan half and wraps it as the bound list. Costs the
    /// handle: no page is fetched until the list is asked for a row.</summary>
    private PagedDryRunRowList<DryRunFileRow> BindPaged(string? viewId, int rowCount)
    {
        PagedDryRunRowStore store = new(
            _gateway!, _runId, RunPlanSide.Sources, viewId, rowCount,
            _planSourceRoot, _planDestinationRoot);
        IDryRunItemActions? actions = _actions;
        PagedDryRunRowList<DryRunFileRow> list = new(
            store,
            (page, within, _) => new DryRunFileRow(page, within) { Actions = actions },
            static position => new DryRunFileRow(
                PlaceholderStore, position & (PagedDryRunRowStore.PageRows - 1)));
        _paged = store;
        _rowList = list;
        return list;
    }

    /// <summary>Drops the current view: the store abandons its pages and anything in flight, and the
    /// list stops listening. Both matter — a fetch for a superseded view must not land in the list that
    /// replaced it, and a list left subscribed keeps its store alive through the event.</summary>
    private void ReleaseRows()
    {
        _rowList?.Detach();
        _paged?.Close();
        _rowList = null;
        _paged = null;
    }

    /// <summary>The in-flight (or last completed) rebuild. Rebuilds over
    /// <see cref="DryRunRebuild.SyncThreshold"/> rows run on the thread pool; tests that cross that
    /// size (or use a non-zero debounce) await this before asserting.</summary>
    internal Task PendingRebuild { get; private set; } = Task.CompletedTask;

    /// <summary>True while a rebuild is computing on the thread pool — drives the rows area's
    /// loading overlay. Synchronous small-report rebuilds never set it, so it appears exactly when
    /// there is a visible delay to explain.</summary>
    [ObservableProperty] public partial bool IsRebuilding { get; private set; }

    // The bound list. A PagedDryRunRowList: as many positions as the view has rows, each materialized
    // from whichever page is resident, or as a placeholder while that page is on its way.
    [ObservableProperty] public partial IReadOnlyList<DryRunFileRow> VisibleRows { get; private set; } = [];

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
    /// span drives, so full paths are shown). Surfaced to the user by <see cref="CommonRootDisplay"/>.
    /// <para>Derived from the plan's facet keys, not from any page: a page holds one directory's worth of
    /// rows, so a root computed from its own contents would shift as the user scrolled.</para></summary>
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

    /// <summary>Everything <see cref="Load(LoadData)"/> assigns: a handle to page against plus the
    /// whole-plan aggregates, all of it read from the run's snapshot header in O(1).
    ///
    /// <para>There is no row data here and no sort. Both used to be computed by a <c>ComputeLoad</c> pass
    /// over every ingested row — ~147 MB of transient sort keys at 500,000 files, and linear in a plan
    /// size that is no longer bounded. The service sorts where the rows are and answers a window.</para></summary>
    internal sealed record LoadData(
        IIpcGateway Gateway,
        Guid RunId,
        string? ViewId,
        int RowCount,
        string? SourceCommonRoot,
        string? DestinationCommonRoot,
        int UntouchedCount,
        int ProcessedCount,
        int DeletedCount,
        IReadOnlyDictionary<string, int> SourceRootCounts,
        IReadOnlyDictionary<string, int> DestinationRootCounts);

    /// <summary>Points the tab at a plan half. A fresh load has no active filters, so the view handle is
    /// the unfiltered one the caller already opened — no rebuild, and no page fetched until the list is
    /// scrolled.</summary>
    internal void Load(LoadData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _rebuildGate.Cancel();   // a rebuild racing this load must not publish the old plan's rows
        IsRebuilding = false;    // the cancelled rebuild has no successor to clear the overlay
        _applying = true;
        ReleaseRows();
        _gateway = data.Gateway;
        _runId = data.RunId;
        _planRowCount = data.RowCount;
        _planSourceRoot = data.SourceCommonRoot;
        _planDestinationRoot = data.DestinationCommonRoot;
        CommonRoot = data.SourceCommonRoot;
        UntouchedCount = data.UntouchedCount;
        ProcessedCount = data.ProcessedCount;
        DeletedCount = data.DeletedCount;
        StatusFilters = BuildStatusFilters();

        SourceFacets = DryRunFacets.Build(data.SourceRootCounts, OnFacetChanged);
        ShowSourceFacet = SourceFacets.Count > 0;
        DestinationFacets = DryRunFacets.Build(data.DestinationRootCounts, OnFacetChanged);
        ShowDestinationFacet = DestinationFacets.Count > 0;

        SearchText = "";
        _applying = false;
        VisibleRows = BindPaged(data.ViewId, data.RowCount);
    }

    public void Clear()
    {
        _rebuildGate.Cancel();   // a rebuild racing this clear must not publish the old plan's rows
        IsRebuilding = false;    // the cancelled rebuild has no successor to clear the overlay
        _applying = true;
        ReleaseRows();           // drops every resident page and abandons anything in flight
        _gateway = null;
        _runId = default;
        _planRowCount = 0;
        _planSourceRoot = _planDestinationRoot = null;
        VisibleRows = [];
        CommonRoot = null;
        SourceFacets = DestinationFacets = [];
        ShowSourceFacet = ShowDestinationFacet = false;
        SearchText = "";
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

    /// <summary>The selected chips as the service's own vocabulary. Null keeps every kind; an EMPTY list
    /// keeps none, which is a filter the user can express by selecting only Deleted.
    ///
    /// <para>The Deleted chip is not a kind — it is a destructive source disposition — so it rides as its
    /// own flag, ORed with the kinds rather than intersected, because the tab treats its chips as
    /// alternatives. This mapping is the client's half of the contract; the service does set membership
    /// on an enum it already owns and never learns what a chip is.</para></summary>
    private IReadOnlyList<OperationKind>? SelectedKinds(out bool includeDestructiveDisposition)
    {
        includeDestructiveDisposition = false;
        if (SelectedStatusKeys() is not { } keys)
            return null;
        List<OperationKind> kinds = [];
        if (keys.Contains("untouched"))
        {
            kinds.Add(OperationKind.SkippedByFilter);
            kinds.Add(OperationKind.SkippedUnchanged);
        }
        if (keys.Contains("processed"))
            kinds.Add(OperationKind.Processed);
        if (keys.Contains("deleted"))
            includeDestructiveDisposition = true;
        return kinds;
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

    /// <summary>One rebuild: snapshot the filter state on the UI thread, ask the service for a VIEW over
    /// the rows that match it, then publish a window onto that view back on the UI thread.
    ///
    /// <para><b>The filter pass moved; the lifecycle did not.</b> The debounce, the supersede gate and
    /// the publish sequencing below are unchanged, and deliberately so — they are what stops a stale
    /// keystroke from overwriting a newer one, and that is true wherever the filtering happens. What
    /// changed is the middle: a pass over the resident rows only ever meant anything while every row was
    /// resident, so the search box, the facet bar and the status chips now run where the rows are and
    /// come back as a handle plus a count.</para></summary>
    private async Task RebuildAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            if (_gateway is not { } gateway)
                return;   // nothing loaded; a filter change on an empty tab has nothing to ask about

            GetRunPlanViewRequest request = new()
            {
                RunId = _runId,
                Side = RunPlanSide.Sources,
                Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                SourceRoots = DryRunFacets.SelectedKeyList(SourceFacets),
                DestinationRoots = DryRunFacets.SelectedKeyList(DestinationFacets),
                Kinds = SelectedKinds(out bool destructive),
                IncludeDestructiveDisposition = destructive,
            };

            // The overlay is judged on the PLAN's size, not on the round trip: a view build is a pass
            // over the half's rows, so it is imperceptible on a small plan and seconds on a huge one —
            // the same distinction the old threshold drew over the resident row count.
            if (_planRowCount > DryRunRebuild.SyncThreshold)
                IsRebuilding = true;   // cleared by whichever current rebuild publishes (or by Load/Clear)

            Result<RunPlanViewResponse, IpcError> result =
                await gateway.GetRunPlanViewAsync(request, ct).ConfigureAwait(true);
            if (result.IsCanceled)
                return;
            if (result.TryGetError(out IpcError? error))
            {
                // A view that cannot be built leaves the current rows on screen rather than blanking the
                // panel: the previous filter's result is still a truthful view of the plan, and the next
                // keystroke retries anyway.
                Log.Warning(
                    "Filtering the Sources tab of run {RunId} failed: {Code} {Message}",
                    _runId, error.Code, error.Message);
                _rebuildGate.TryPublish(ct, () => IsRebuilding = false);
                return;
            }
            result.TryGetValue(out RunPlanViewResponse? view);

            // The gate makes cancel-vs-publish atomic, so a superseded rebuild can never publish
            // after its successor.
            _rebuildGate.TryPublish(ct, () =>
            {
                IsRebuilding = false;
                // Always a fresh list: the rows are a window onto a DIFFERENT view now, so there is no
                // scroll position worth preserving — position 40 of the filtered list is not the row
                // that was at position 40 of the previous one.
                ReleaseRows();
                VisibleRows = BindPaged(view!.ViewId, view.RowCount);
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
}

/// <summary>The Destinations tab: the resulting destination structure — files a run would add
/// (New), overwrite (Overwritten), leave in place (Untouched), or (under Mirror) delete (Deleted) —
/// filterable by source and by destination, as a flat list or a rolled-up path tree.</summary>
public sealed partial class DryRunDestinationsTab : ViewModelBase
{
    /// <summary>Rendered for a position whose page is still in flight. Shared with the Sources tab's
    /// placeholder store — its rows carry both a blank source file and a blank no-source destination
    /// operation, so either tab's row type resolves against it.</summary>
    private static readonly DryRunRowStore PlaceholderStore =
        DryRunRowStore.CreatePlaceholder(PagedDryRunRowStore.PageRows);

    private readonly TimeSpan _searchDebounce;
    private readonly DryRunRebuildGate _rebuildGate = new();
    private bool _applying;

    // The open plan, as a handle rather than as rows — see the Sources tab for the reasoning.
    private IIpcGateway? _gateway;
    private Guid _runId;
    private string? _planSourceRoot;
    private string? _planDestinationRoot;
    private int _planRowCount;
    private PagedDryRunRowStore? _paged;
    private PagedDryRunRowList<DryRunDestinationRow>? _rowList;

    /// <summary>Clipboard/shell actions for the row right-click menu (null in headless tests).</summary>
    private readonly IDryRunItemActions? _actions;

    public DryRunDestinationsTab(TimeSpan searchDebounce, IDryRunItemActions? actions = null)
    {
        _searchDebounce = searchDebounce;
        _actions = actions;
    }

    /// <summary>Opens a window onto one view of the destination half and wraps it as the bound list.
    ///
    /// <para><b>One row per destination path, not per source.</b> A page's operations never name a source
    /// (<c>GetRunPlanPageHandler.DestinationPage</c> sets <c>SourceIndex = -1</c>), so every row is a
    /// no-source row — <c>~position</c> — and a source that replicates to three targets is three rows
    /// rather than one row with three entries. That is forced rather than chosen: the view's index space
    /// is per operation, and a fan-out group cannot straddle a page boundary. It also makes the row
    /// count the page store sizes itself by exactly the count the view reports.</para></summary>
    private PagedDryRunRowList<DryRunDestinationRow> BindPaged(string? viewId, int rowCount)
    {
        PagedDryRunRowStore store = new(
            _gateway!, _runId, RunPlanSide.Destinations, viewId, rowCount,
            _planSourceRoot, _planDestinationRoot);
        IDryRunItemActions? actions = _actions;
        PagedDryRunRowList<DryRunDestinationRow> list = new(
            store,
            (page, within, _) => new DryRunDestinationRow(page, ~within) { Actions = actions },
            static position => new DryRunDestinationRow(
                PlaceholderStore, ~(position & (PagedDryRunRowStore.PageRows - 1))));
        _paged = store;
        _rowList = list;
        return list;
    }

    /// <summary>Drops the current view — see <c>DryRunSourcesTab.ReleaseRows</c>.</summary>
    private void ReleaseRows()
    {
        _rowList?.Detach();
        _paged?.Close();
        _rowList = null;
        _paged = null;
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

    /// <summary>Always empty, and always hidden: <c>RunSnapshotView.KeepDestinations</c> filters the
    /// destination half by target root, kind and search only — a destination row's SOURCE root is not
    /// something it can select on, so a facet offering it would list roots that no filter honours.
    /// Kept as a property because the view binds it; see the paging handoff for the follow-up that would
    /// restore it (a source-ordinal → root map built alongside the view).</summary>
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

    /// <summary>Everything <see cref="Load(LoadData)"/> assigns: a handle to page against plus the
    /// whole-plan aggregates. See <c>DryRunSourcesTab.LoadData</c> for why there is no row data here.</summary>
    internal sealed record LoadData(
        IIpcGateway Gateway,
        Guid RunId,
        string? ViewId,
        int RowCount,
        string? SourceCommonRoot,
        string? DestinationCommonRoot,
        int UntouchedCount,
        int NewCount,
        int OverwrittenCount,
        int DeletedCount,
        IReadOnlyDictionary<string, int> DestinationRootCounts);

    /// <summary>Folds the plan's per-kind destination totals into this tab's four chips.
    ///
    /// <para>The service tallies by <see cref="OperationKind"/> and stops there; which chip a kind
    /// belongs to is a display decision, and it is made HERE — through the same
    /// <see cref="DestinationKindMap"/> the rows themselves render through, so a chip's count and the
    /// glyphs on the rows it selects can never disagree about what a kind means.</para></summary>
    internal static (int Untouched, int New, int Overwritten, int Deleted) ChipCounts(
        IReadOnlyDictionary<OperationKind, int> byKind)
    {
        ArgumentNullException.ThrowIfNull(byKind);
        int untouched = 0, added = 0, overwritten = 0, deleted = 0;
        foreach ((OperationKind kind, int count) in byKind)
        {
            switch (DestinationKindMap.Map(kind))
            {
                case DestinationRowKind.Untouched: untouched += count; break;
                case DestinationRowKind.New: added += count; break;
                case DestinationRowKind.Overwritten: overwritten += count; break;
                case DestinationRowKind.Deleted: deleted += count; break;
                default: break;
            }
        }
        return (untouched, added, overwritten, deleted);
    }

    /// <summary>Points the tab at the plan's destination half — see <c>DryRunSourcesTab.Load</c>.</summary>
    internal void Load(LoadData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _rebuildGate.Cancel();   // a rebuild racing this load must not publish the old plan's rows
        IsRebuilding = false;    // the cancelled rebuild has no successor to clear the overlay
        _applying = true;
        ReleaseRows();
        _gateway = data.Gateway;
        _runId = data.RunId;
        _planRowCount = data.RowCount;
        _planSourceRoot = data.SourceCommonRoot;
        _planDestinationRoot = data.DestinationCommonRoot;
        CommonRoot = data.DestinationCommonRoot;
        UntouchedCount = data.UntouchedCount;
        NewCount = data.NewCount;
        OverwrittenCount = data.OverwrittenCount;
        DeletedCount = data.DeletedCount;
        StatusFilters = BuildStatusFilters();

        // No source facet: the service cannot filter the destination half by source root, so offering
        // one would list values that nothing honours. See SourceFacets.
        SourceFacets = [];
        ShowSourceFacet = false;
        DestinationFacets = DryRunFacets.Build(data.DestinationRootCounts, OnFacetChanged);
        ShowDestinationFacet = DestinationFacets.Count > 0;

        SearchText = "";
        _applying = false;
        VisibleRows = BindPaged(data.ViewId, data.RowCount);
    }

    public void Clear()
    {
        _rebuildGate.Cancel();   // a rebuild racing this clear must not publish the old plan's rows
        IsRebuilding = false;    // the cancelled rebuild has no successor to clear the overlay
        _applying = true;
        ReleaseRows();           // drops every resident page and abandons anything in flight
        _gateway = null;
        _runId = default;
        _planRowCount = 0;
        _planSourceRoot = _planDestinationRoot = null;
        VisibleRows = [];
        CommonRoot = null;
        SourceFacets = DestinationFacets = [];
        ShowSourceFacet = ShowDestinationFacet = false;
        SearchText = "";
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

    /// <summary>The selected chips as the service's own vocabulary. Null keeps every kind; an EMPTY list
    /// keeps none, which is what deselecting everything means.
    ///
    /// <para>Every chip on this tab IS a set of kinds — unlike the Sources tab, whose Deleted chip is a
    /// source disposition and needs its own flag. The fold below is the inverse of
    /// <see cref="DestinationKindMap.Map"/>, and lives here for the same reason that one does: the
    /// service does set membership on an enum it owns and never learns what a chip is.</para></summary>
    private IReadOnlyList<OperationKind>? SelectedKinds()
    {
        if (SelectedStatusKeys() is not { } keys)
            return null;
        List<OperationKind> kinds = [];
        if (keys.Contains("untouched"))
        {
            kinds.Add(OperationKind.SkipConflict);
            kinds.Add(OperationKind.SkipUnchanged);
            kinds.Add(OperationKind.Untouched);
        }
        if (keys.Contains("new"))
        {
            // A conflict rename writes a new file at a suffixed path, so it reads as New on this tab —
            // the server already split it from the Untouched original it sits beside.
            kinds.Add(OperationKind.New);
            kinds.Add(OperationKind.Rename);
        }
        if (keys.Contains("overwritten"))
            kinds.Add(OperationKind.Overwrite);
        if (keys.Contains("deleted"))
            kinds.Add(OperationKind.Deleted);
        return kinds;
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
    /// clicks coalesce and only the latest publishes. Search changes debounce; facet and chip toggles
    /// rebuild immediately.</summary>
    private void RequestRebuild(bool debounce)
    {
        PendingRebuild = RebuildAsync(debounce ? _searchDebounce : TimeSpan.Zero, _rebuildGate.Supersede());
    }

    /// <summary>One rebuild — see <c>DryRunSourcesTab.RebuildAsync</c>, of which this is the destination
    /// half. Same debounce, same supersede gate, same publish sequencing; a different side and no
    /// source-root filter, which this half of the plan cannot express.</summary>
    private async Task RebuildAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            if (_gateway is not { } gateway)
                return;   // nothing loaded

            GetRunPlanViewRequest request = new()
            {
                RunId = _runId,
                Side = RunPlanSide.Destinations,
                Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                DestinationRoots = DryRunFacets.SelectedKeyList(DestinationFacets),
                Kinds = SelectedKinds(),
            };

            if (_planRowCount > DryRunRebuild.SyncThreshold)
                IsRebuilding = true;   // cleared by whichever current rebuild publishes (or by Load/Clear)

            Result<RunPlanViewResponse, IpcError> result =
                await gateway.GetRunPlanViewAsync(request, ct).ConfigureAwait(true);
            if (result.IsCanceled)
                return;
            if (result.TryGetError(out IpcError? error))
            {
                Log.Warning(
                    "Filtering the Destinations tab of run {RunId} failed: {Code} {Message}",
                    _runId, error.Code, error.Message);
                _rebuildGate.TryPublish(ct, () => IsRebuilding = false);
                return;
            }
            result.TryGetValue(out RunPlanViewResponse? view);

            _rebuildGate.TryPublish(ct, () =>
            {
                IsRebuilding = false;
                ReleaseRows();
                VisibleRows = BindPaged(view!.ViewId, view.RowCount);
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer filter/search change */ }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to rebuild the Destinations tab");
            _rebuildGate.TryPublish(ct, () => IsRebuilding = false);   // only if still the current rebuild
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
        HasDeferredReclaim = v.DeferredReclaimBytes > 0;
        DeferredReclaimText = ByteSize.Format(v.DeferredReclaimBytes);
        MirrorDeferredReclaimText = ByteSize.Format(v.MirrorDeferredReclaimBytes);

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

        // The peak is carrying orphans that Mirror only removes after the copies land. Naming the
        // amount turns a dead end into a decision, because "Delete Proactively" takes exactly this off
        // the peak — and the reader cannot weigh that trade without the number. Deliberately gated on
        // the Mirror share: the source-disposition share of the deferred total has no such remedy, so
        // offering one for it would be advice the user cannot act on.
        if (v.MirrorDeferredReclaimBytes > 0 && HasWarning)
        {
            WarningText +=
                $" {MirrorDeferredReclaimText} of that is files awaiting deletion; deleting them" +
                " proactively would keep them off the peak.";
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

    /// <summary>Space the run gives back only once it finishes, so the peak carries it throughout:
    /// permanently-deleted sources, plus Mirror orphans on a delete-after-copy profile.</summary>
    public bool HasDeferredReclaim { get; }
    public string DeferredReclaimText { get; }
    public string MirrorDeferredReclaimText { get; }

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

    /// <summary>Clock behind the staleness age. Injected so a test can advance it with
    /// <c>FakeTimeProvider</c> instead of waiting fifteen real minutes.</summary>
    private readonly TimeProvider _time;

    public DryRunViewModel(
        IIpcGateway gateway, IDryRunItemActions? actions = null, TimeSpan? searchDebounce = null,
        TimeProvider? time = null)
    {
        _gateway = gateway;
        _actions = actions;
        _time = time ?? TimeProvider.System;
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

    /// <summary>Live phase/count caption shown beside the run buttons while a preview works
    /// ("Working out what this will do…" → "Building the lists…"). The view gates its visibility on
    /// <see cref="IsPreviewing"/>, so a late post after the preview ends is harmless.</summary>
    [ObservableProperty] public partial string RunStatusText { get; set; } = "";

    /// <summary>True from the moment a preview is requested until its rows are on screen (or it
    /// failed). Spans BOTH engine phases — the run's planning scan, which happens service-side and
    /// reports nothing to this view model, and the plan-stream ingest that follows — because to the
    /// user they are one wait. Replaces the old <c>RunCommand.IsRunning</c> the view used to gate on;
    /// that command no longer exists, and it could not have covered the planning half anyway.</summary>
    [ObservableProperty] public partial bool IsPreviewing { get; set; }

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

    // ── The pending run this preview IS ───────────────────────────────────────────────────────
    // A preview is a real run's planning phase, parked in AwaitingApproval with nothing touched. So
    // these are not decorations on a simulation: PendingRunId identifies work the engine is holding on
    // this window's behalf, and it must be answered (approved or declined) or explicitly abandoned.

    /// <summary>The run whose frozen plan is on screen and waiting for an answer, or null when there is
    /// nothing to approve. Drives the footer's visibility.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveRunCommand))]
    public partial Guid? PendingRunId { get; set; }

    /// <summary>The plan's totals, straight off the <c>run-planned</c> event. These are the numbers the
    /// footer states and the numbers the run will act on — the plan is a ceiling the executor can only
    /// come in under (it re-screens and re-checks-unchanged), never exceed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanSummary))]
    public partial int PlannedCopies { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanSummary), nameof(PlanTruncationNotice))]
    public partial int PlannedDeletes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanSummary))]
    public partial long PlannedCopyBytes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanSummary))]
    public partial long PlannedDeleteBytes { get; set; }

    /// <summary>The footer's one-line summary of what approving will do. Deletions lead when there are
    /// any: removing files from a target is the only part that feels irreversible, so it must not be
    /// the second clause.</summary>
    public string PlanSummary
    {
        get
        {
            string copies = $"{PlannedCopies:N0} file(s) to copy or update ({ByteSize.Format(PlannedCopyBytes)})";
            return PlannedDeletes > 0
                ? $"{PlannedDeletes:N0} file(s) to REMOVE from the target folder(s) "
                  + $"({ByteSize.Format(PlannedDeleteBytes)}) — these go to the Recycle Bin  ·  {copies}"
                : copies;
        }
    }

    /// <summary>The Mirror blast-radius warning, or empty for a non-Mirror profile. Shown in the footer
    /// beside the counts rather than in a dialog before the run, which is the point of the Preview tab:
    /// the warning now sits next to the actual rows it describes.
    /// <para>The filter caveat is not padding. An excluded source file contributes no survivor, so
    /// tightening a filter on a Mirror profile removes files the profile previously copied — correct,
    /// faithful to what is listed above, and utterly surprising if unsaid.</para></summary>
    [ObservableProperty] public partial string MirrorWarning { get; set; } = "";

    /// <summary>Set when the plan does not cover everything that was scanned. Distinct from
    /// <see cref="WasTruncated"/>, which describes the rows on screen: this one is what the RUN will do
    /// about it, and for Mirror the answer is "remove nothing" — <c>MirrorDeletionPass</c> refuses a
    /// truncated plan outright, so a footer that still promised deletions would be lying.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanTruncationNotice))]
    public partial bool PlanTruncated { get; set; }

    /// <summary>The §4.1 blocking warnings this run's profile raises, straight off the
    /// <c>run-planned</c> event. Empty for a profile with none, which is the ordinary case.
    /// <para>These gate the run in the ENGINE — <c>approve-run</c> refuses without an acknowledgment —
    /// so the footer states them and the checkbox below is what turns Approve back on. A run may have
    /// been planned from an unsaved draft, which never passed the save path where these are otherwise
    /// acknowledged, and that is the hole this closes.</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockingIssues), nameof(BlockingIssuesText))]
    public partial IReadOnlyList<ValidationIssue> BlockingIssues { get; set; } = [];

    public bool HasBlockingIssues => BlockingIssues.Count > 0;

    /// <summary>The blocking warnings as one block of text, one per line — the footer states them rather
    /// than listing them as rows, because they are prose the user has to read, not data to scan.</summary>
    public string BlockingIssuesText
    {
        get
        {
            StringBuilder text = new();
            foreach (ValidationIssue issue in BlockingIssues)
            {
                if (text.Length > 0)
                    text.Append('\n');
                text.Append(issue.Message);
            }
            return text.ToString();
        }
    }

    /// <summary>The user's confirmation of <see cref="BlockingIssues"/>. Reset by every
    /// <see cref="BeginPlanning"/> and by <see cref="ForgetPendingPlan"/>, so an acknowledgment can never
    /// carry from one plan to the next — the whole point is that it applies to the run on screen.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveRunCommand))]
    public partial bool AcknowledgedWarnings { get; set; }

    /// <summary>Whether Approve is offered. Gates on the acknowledgment so the button and the engine
    /// agree: without this the user presses Approve and gets a RUN_NOT_APPROVABLE banner instead of a
    /// run, with nothing on screen explaining what to do about it.</summary>
    private bool CanApproveRun => PendingRunId is not null && (!HasBlockingIssues || AcknowledgedWarnings);

    /// <summary>The truncated-plan footer notice, kept beside <see cref="PlanTruncated"/> so the two
    /// cannot drift.</summary>
    public string PlanTruncationNotice =>
        "This plan does not cover everything that was scanned, so it may be incomplete."
        + (PlannedDeletes > 0 ? " No files will be removed from the target folder(s)." : "");

    /// <summary>What a run does to the SOURCE files, and whether that destroys them.
    ///
    /// <para>The footer's other two warnings are both about the destination. This one is the half that
    /// was missing entirely: the modal confirmation this footer replaced was the only place
    /// <c>Policies.OnSuccess</c> was ever stated, and it said "each source file will then be PERMANENTLY
    /// DELETED". Without it a user could read "1,204 file(s) to copy or update (8.4 GB)", press Approve,
    /// and lose all 1,204 originals with nothing on screen having said so.</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSourceDispositionNote))]
    public partial string SourceDispositionWarning { get; set; } = "";

    /// <summary>Whether <see cref="SourceDispositionWarning"/> describes destroying the sources, which is
    /// what earns it the danger styling rather than plain text. Archiving relocates and keeps, so it is
    /// stated without being shouted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSourceDispositionNote))]
    public partial bool SourceDispositionDestroys { get; set; }

    /// <summary>Whether to show the disposition as plain text: there is something to say, and it is not
    /// the destructive case the danger bar above already covers. Gating on both matters — the empty
    /// KeepSource case would otherwise still cost the footer a stack gap.</summary>
    public bool ShowSourceDispositionNote =>
        SourceDispositionWarning.Length > 0 && !SourceDispositionDestroys;

    /// <summary>Keeps the footer's warnings in step with the editor's live policies. Called on profile
    /// load and whenever the sync mode or disposition changes, so the footer describes the profile
    /// currently on screen even before a plan exists.</summary>
    public void ApplyPolicies(SyncMode mode, OnSuccessAction onSuccess, string? archiveFolder)
    {
        MirrorWarning = mode == SyncMode.Mirror
            ? "This is a MIRROR profile: every file listed for removal goes to the Recycle Bin. That "
              + "includes files excluded by this profile's filters, so tightening a filter removes copies "
              + "this profile made earlier."
            : "";
        (string text, bool destroys) = SourceDisposition.Describe(onSuccess, archiveFolder);
        // KeepSource is the default and the safe case; stating "kept in place" beside the counts is noise
        // that dilutes the warnings that matter.
        SourceDispositionWarning = onSuccess == OnSuccessAction.KeepSource ? "" : text;
        SourceDispositionDestroys = destroys;
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
        MirrorWarning = "";
        SourceDispositionWarning = "";
        SourceDispositionDestroys = false;
        ReportClosed();
    }

    /// <summary>Bumped by <see cref="ClearReport"/>. A run captures it before its awaits and must
    /// not apply its result if the preview was cleared meanwhile — comparing profile ids is not
    /// enough, because saving edits to the profile re-selects it under the SAME id and the stale
    /// report (generated from the pre-edit settings) would resurrect over the cleared preview.</summary>
    private int _reportEpoch;

    /// <summary>Marks the start of a preview: the shell has asked the engine to plan a run and is waiting
    /// for its <c>run-planned</c> event. Clears the previous preview now rather than when the rows
    /// arrive, so the tab does not sit showing a stale report throughout the scan.
    /// <para>The clear releases the previous preview's rows and forest — without it the old report stays
    /// alive while the next one is built, so consecutive previews peak at ~2x the row footprint (the
    /// Gen2 pressure that makes a re-preview feel worse). It is the state-only <see cref="ClearReport"/>,
    /// never <see cref="ReportClosed"/>: a preview about to start is not a safe moment for
    /// <c>UiMemoryTrim</c>'s blocking two-pass collect on the UI thread.</para></summary>
    public void BeginPlanning()
    {
        ClearReport();
        // The PLAN is no longer declined here. Retained previews mean a parked run outlives the tab that
        // showed it, and it is the PreviewStore — keyed by profile — that decides when one is superseded:
        // re-previewing profile A must not decline the result held for profile B. The store declines the
        // entry this new plan replaces, once it knows what that plan is.
        //
        // What IS still abandoned is the footer's own state, so the tab cannot offer to approve the
        // previous plan while the next one is being computed. ForgetPendingPlan is state-only — it tells
        // the engine nothing, which is exactly right: the run it names is still retained.
        ForgetPendingPlan();
        PreviewTakenAtUtc = null;
        // And a run still PLANNING is superseded outright. This one IS cancelled, not retained: an
        // unfinished scan has no result to come back to, and leaving it running would mean the engine keeps
        // walking a tree nobody will answer for — and its late run-planned event could still arrive, be
        // recognized as ours, and overwrite this preview's rows.
        AbandonPlanningRun();
        EmptyStateText = DefaultEmptyState;   // a previous "nothing to do" says nothing about this run
        IsPreviewing = true;
        RunStatusText = "Working out what this will do…";
    }

    /// <summary>Cancels the planning run this preview supersedes, if there is one.
    /// <para>Fire-and-forget like <see cref="AbandonPendingRun"/>: the caller is mid-transition, and a
    /// cancel that loses a race with the run's own completion is the normal case, not a fault.</para></summary>
    private void AbandonPlanningRun()
    {
        if (PlanningRunId is not Guid runId)
            return;
        PlanningRunId = null;
        Superseded?.Invoke(runId);
        _ = _gateway.CancelRunAsync(runId)
            .ContinueWith(
                t => Log.Debug(t.Exception, "Cancelling superseded planning run {RunId} failed", runId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>Set by the shell, and called with the id of a run this window has stopped caring about.
    /// The shell filters engine events by the runs it started, so it has to be told to forget this one —
    /// otherwise a late <c>run-planned</c> for a superseded scan is still treated as ours and applied over
    /// the newer plan. A null callback is a no-op, so headless tests are not blocked.</summary>
    public Action<Guid>? Superseded { get; set; }

    // ── Staleness of a retained preview ───────────────────────────────────────────────────────────
    /// <summary>When the plan on screen was frozen, or null when there is no plan.
    /// <para>Set from the <c>run-planned</c> event's own <c>AtUtc</c>, which is the instant the engine
    /// stamped into the snapshot header — so the age shown is the age of the WORK LIST, not of the moment
    /// this window happened to render it. A restored preview therefore reports its original age rather than
    /// looking freshly taken, which is the entire point of the warning.</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewStale), nameof(PreviewAgeText), nameof(HasPreviewAge))]
    public partial DateTimeOffset? PreviewTakenAtUtc { get; set; }

    /// <summary>How old a preview may get before <see cref="IsPreviewStale"/> trips. Pushed in from
    /// <c>ClientSettings.PreviewStaleAfter</c> by the shell, so this view model does not read settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewStale), nameof(PreviewAgeText))]
    public partial TimeSpan PreviewStaleAfter { get; set; } =
        TimeSpan.FromMinutes(ClientSettings.DefaultPreviewStaleAfterMinutes);

    /// <summary>Re-read on every tick so the age and the staleness flag advance without a plan reload.
    /// Not an <c>[ObservableProperty]</c> of its own — the tick raises the three derived properties.</summary>
    private TimeSpan PreviewAge =>
        PreviewTakenAtUtc is { } taken ? _time.GetUtcNow() - taken : TimeSpan.Zero;

    /// <summary>Whether there is a preview old enough to warn about.
    /// <para>Deliberately advisory. The plan is still exactly what the run will do — it was frozen, and the
    /// executor re-screens and re-checks-unchanged so it can only ever do LESS than planned, never more.
    /// What may have drifted is the FILESYSTEM, so a stale plan risks being incomplete, never wrong. The
    /// banner says "may be out of date" for that reason and Approve stays enabled.</para></summary>
    public bool IsPreviewStale => PreviewTakenAtUtc is not null && PreviewAge >= PreviewStaleAfter;

    public bool HasPreviewAge => PreviewTakenAtUtc is not null;

    /// <summary>The preview's age in prose, shown whether or not it is stale — a user who can always see
    /// how old a result is never has to guess whether the threshold has been crossed.</summary>
    public string PreviewAgeText
    {
        get
        {
            if (PreviewTakenAtUtc is not { } taken)
                return "";
            TimeSpan age = PreviewAge;
            string when = taken.ToLocalTime().ToString("HH:mm");
            return age < TimeSpan.FromMinutes(1)
                ? $"Previewed just now ({when})"
                : age < TimeSpan.FromHours(1)
                    ? $"Previewed {(int)age.TotalMinutes} min ago ({when})"
                    : age < TimeSpan.FromDays(1)
                        ? $"Previewed {(int)age.TotalHours} hr ago ({when})"
                        : $"Previewed {taken.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
    }

    /// <summary>The staleness banner's text. Names the age and what to do about it, and states the limit of
    /// the claim — an out-of-date preview may be INCOMPLETE, never wrong.</summary>
    public string PreviewStaleNotice =>
        $"This preview is {PreviewAgeText.Replace("Previewed ", "", StringComparison.Ordinal)} and may be out of "
        + "date — files may have been added, changed or removed since. Approving still runs exactly the work "
        + "listed below; anything that changed since will be picked up by the next run. Preview again to refresh.";

    /// <summary>Recomputes the age-derived properties. Called on a timer by the shell (and directly by
    /// tests), because nothing else changes when time passes.</summary>
    public void RefreshPreviewAge()
    {
        if (PreviewTakenAtUtc is null)
            return;
        OnPropertyChanged(nameof(PreviewAgeText));
        OnPropertyChanged(nameof(PreviewStaleNotice));
        OnPropertyChanged(nameof(IsPreviewStale));
    }

    /// <summary>The run whose PLANNING is in flight, once the engine has accepted it. Distinct from
    /// <see cref="PendingRunId"/>, which is a plan already on screen: this one exists only during the
    /// scan, and it exists so that scan can be cancelled. Without it a large profile's preview would be
    /// an uninterruptible wait — the affordance the old dry run had and this must not lose.</summary>
    [ObservableProperty] public partial Guid? PlanningRunId { get; set; }

    /// <summary>Called by the shell once <c>run-profile</c> has been accepted, so the planning scan
    /// becomes cancellable.</summary>
    public void PlanningStarted(Guid runId) => PlanningRunId = runId;

    /// <summary>Renders a planning-phase progress sample as the tab's caption.
    ///
    /// <para>Restores what the dry run had and the two-phase run lost: previewing a 400k-file profile over
    /// a slow share showed one unchanging sentence for minutes, so a wedged walk and a slow one looked
    /// identical. Ignored once a plan is on screen — a late sample must not overwrite the footer's own
    /// state.</para></summary>
    /// <param name="stage">The sample's <c>RunPlanStages</c> value, or empty from a service that predates
    /// it. Worth rendering because the counts alone go quiet twice — while the walk's findings are written
    /// into the work list, and before the destination sweep has found anything — and a frozen count reads as
    /// a stalled scan.</param>
    /// <param name="unreadable">Entries the walk could not read. Said while it happens; the engine also
    /// warns about it once, at approval time.</param>
    public void PlanningProgress(long sources, long destinations, string stage = "", long unreadable = 0)
    {
        if (!IsPreviewing)
            return;
        string counts = (destinations > 0
                ? $"{sources:N0} source file(s), {destinations:N0} destination file(s)"
                : $"{sources:N0} source file(s)")
            + (unreadable > 0 ? $", {unreadable:N0} unreadable" : "");
        // The sweep keeps its own caption for its whole duration. Falling back to "Scanning…" at its first
        // hit — which is what a `when destinations == 0` guard does — tells the user the source scan is
        // still running for the entire sweep, minutes after it finished. This line is the tab's ONLY status
        // line, so there is nothing else on screen to correct it.
        RunStatusText = stage switch
        {
            RunPlanStages.Building => $"Building the plan… {counts} found",
            RunPlanStages.Sweeping => $"Sweeping the destination… {counts} found",
            _ => destinations > 0 ? $"Scanning… {counts} found" : $"Scanning sources… {counts} found",
        };
    }

    /// <summary>A preview parked behind the concurrent-plan limit.
    ///
    /// <para>Its own caption because its counters CANNOT move: rendering the queued sample as scan progress
    /// leaves the tab reading "Scanning sources… 0 file(s) found" for the whole wait, which is the wedged
    /// look the live counts exist to remove. The engine publishes this as a distinct phase for the same
    /// reason — see <c>RunCoordinator.WaitingPhase</c>.</para></summary>
    public void PlanningQueued()
    {
        if (!IsPreviewing)
            return;
        RunStatusText = "Waiting for another preview to finish…";
    }

    /// <summary>Stops the planning scan. Nothing has been touched at this point, so this is free —
    /// the run is dropped and its snapshot cleaned up by the coordinator.</summary>
    [RelayCommand]
    private async Task CancelPreviewAsync()
    {
        if (PlanningRunId is not Guid runId)
            return;
        PlanningRunId = null;
        // Ends the wait immediately rather than waiting for the engine to answer: the user asked for this
        // to stop, and the plan event for a cancelled run may never arrive at all.
        EndPlanning("Preview cancelled.");
        try
        {
            var cancelled = await _gateway.CancelRunAsync(runId);
            if (cancelled.TryGetError(out IpcError? error))
                Log.Debug("Cancelling the planning run {RunId} failed: {Message}", runId, error.Message);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a command has no exception boundary of its own. A failed
            // cancel is not worth a banner — the user's intent is already reflected on screen.
            Log.Error(ex, "Cancelling the planning run {RunId} failed", runId);
        }
    }

    /// <summary>What the tab says when there are no rows. Normally the invitation to preview; replaced by
    /// <see cref="EndPlanning"/> when a plan came back with nothing to do.
    /// <para>It has to be said HERE and not only as an activity notice: a preview navigates to this tab,
    /// and the activity panel is closed until a run is actually approved — so an "everything is already up
    /// to date" that lives only there is a message the user never sees.</para></summary>
    [ObservableProperty] public partial string EmptyStateText { get; set; } = DefaultEmptyState;

    private const string DefaultEmptyState = "Preview this profile to see exactly what a run would do.";

    /// <summary>Abandons a preview that never produced rows: a refused request, a plan the engine reported
    /// as failed, or a plan with nothing in it. <paramref name="error"/> raises the danger banner;
    /// <paramref name="emptyState"/> replaces the placeholder for the benign "nothing to do" case.</summary>
    public void EndPlanning(string? error = null, string? emptyState = null)
    {
        IsPreviewing = false;
        RunStatusText = "";
        PlanningRunId = null;   // the scan is over either way, so there is nothing left to cancel
        if (error is not null)
            ErrorMessage = error;
        if (emptyState is not null)
            EmptyStateText = emptyState;
    }

    /// <summary>Opens a planned run's frozen work list in the view and raises the approval footer.
    ///
    /// <para>This is the whole point of the two-phase run: the rows below are read back from the run's
    /// snapshot, not re-planned, so what is listed is exactly what <see cref="ApproveRunCommand"/> will
    /// execute.</para>
    ///
    /// <para><b>Three O(1) calls, and no rows.</b> This used to stream the entire plan into a
    /// <c>DryRunRowStore</c> and sort both halves of it before showing anything — affordable only while a
    /// plan was capped at 500,000 files, and the cap is gone because a bounded plan was a WRONG job
    /// rather than a smaller one. What it does instead: read the snapshot header for the whole-plan
    /// aggregates a windowed client cannot count for itself, open an unfiltered view over each half, and
    /// bind a window onto each. Everything here is independent of how large the plan is.</para>
    ///
    /// <para>The caller owns the decision to be here: a failed plan, or one with nothing to do, is
    /// answered by the shell and never reaches this method.</para></summary>
    public async Task LoadPlanAsync(RunPlannedEvent planned, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(planned);

        // The epoch this plan belongs to. BeginPlanning already bumped it; a later clear or a superseding
        // preview bumps it again, and the guard before ApplyPrepared drops this plan's rows if so.
        int epoch = _reportEpoch;
        IsPreviewing = true;
        PlanningRunId = null;   // the scan is done; from here the wait is this method's own open
        // Claims the stage between "planning" and "on screen", so a discard arriving mid-open has
        // something to match against — see ForgetDiscardedRun.
        _loadingRunId = planned.RunId;
        // Baseline for the churn figure on the sample below — taken after BeginPlanning's clear so it
        // measures this preview only, not the previous one's teardown.
        long allocatedBefore = UiMemoryLog.AllocatedSnapshot();

        try
        {
            RunStatusText = "Opening the plan…";

            // 1. The header: the facet keys and their counts, and the status totals. Folded during the
            //    plan's own walk and read back in O(1) — a preview holding a page at a time cannot
            //    compute a whole-plan aggregate, and a facet bar counting only the rows on screen would
            //    be worse than none.
            Result<RunDetailDto, IpcError> detailResult =
                await _gateway.GetRunDetailAsync(planned.RunId, ct).ConfigureAwait(true);
            if (detailResult.IsCanceled)
            {
                Log.Debug("Preview of run {RunId} cancelled by the user", planned.RunId);
                ErrorMessage = "Preview cancelled.";
                return;
            }
            if (_loadingRunId != planned.RunId)
                return;   // discarded from the job queue meanwhile; ForgetDiscardedRun already said so
            if (detailResult.TryGetError(out IpcError? detailError))
            {
                ErrorMessage = PreviewFailure(detailError);
                return;
            }
            detailResult.TryGetValue(out RunDetailDto? detail);

            // 2. An unfiltered view over each half. A filter that selects everything comes back with a
            //    null handle and the plan's own totals, at no view-build cost — so this costs two header
            //    reads, not two passes.
            Result<RunPlanViewResponse, IpcError> sourcesResult =
                await _gateway.GetRunPlanViewAsync(
                    new GetRunPlanViewRequest { RunId = planned.RunId, Side = RunPlanSide.Sources }, ct)
                .ConfigureAwait(true);
            Result<RunPlanViewResponse, IpcError> destinationsResult =
                await _gateway.GetRunPlanViewAsync(
                    new GetRunPlanViewRequest { RunId = planned.RunId, Side = RunPlanSide.Destinations }, ct)
                .ConfigureAwait(true);
            if (sourcesResult.IsCanceled || destinationsResult.IsCanceled)
            {
                Log.Debug("Preview of run {RunId} cancelled by the user", planned.RunId);
                ErrorMessage = "Preview cancelled.";
                return;
            }
            if (_loadingRunId != planned.RunId)
                return;
            if (sourcesResult.TryGetError(out IpcError? sourcesError))
            {
                ErrorMessage = PreviewFailure(sourcesError);
                return;
            }
            if (destinationsResult.TryGetError(out IpcError? destinationsError))
            {
                ErrorMessage = PreviewFailure(destinationsError);
                return;
            }
            sourcesResult.TryGetValue(out RunPlanViewResponse? sourcesView);
            destinationsResult.TryGetValue(out RunPlanViewResponse? destinationsView);

            // 3. Bind. No row has been read yet and none will be until the list is scrolled.
            PreparedReport prepared = PrepareReport(
                _gateway, planned.RunId, detail!, sourcesView!, destinationsView!,
                // SweepFaulted is the only thing that truncates a plan now — a tree that could not be
                // READ. The space projection is unsound over a partial graph, so the header omits it.
                new DryRunCompletion(planned.AtUtc, detail!.Truncated, detail.Space));
            if (_reportEpoch != epoch)
                return;   // superseded while opening — a stale plan must not apply
            ApplyPrepared(prepared);
            // Only now: the footer must never offer to approve a run whose rows are not the ones on
            // screen, so it appears with them and not a moment earlier.
            PlannedCopies = planned.PlannedCopies;
            PlannedDeletes = planned.PlannedDeletes;
            PlannedCopyBytes = planned.PlannedCopyBytes;
            PlannedDeleteBytes = planned.PlannedDeleteBytes;
            PlanTruncated = planned.Truncated;
            // Before PendingRunId, which is what raises the footer: the warnings and the Approve button
            // must appear together, never the button first.
            BlockingIssues = planned.BlockingIssues;
            AcknowledgedWarnings = false;
            // The event's own stamp, not "now": a RESTORED preview must report the age of the work list,
            // which is the whole basis of the staleness warning. Taking the current time here would make
            // every reopened result look freshly taken.
            PreviewTakenAtUtc = planned.AtUtc;
            PendingRunId = planned.RunId;
            // What the app now sits at with a preview on screen — the number a user reports. It should be
            // barely above idle whatever the plan's size; a figure that tracks the row count means
            // something is still holding every row.
            UiMemoryLog.Sample("preview-applied", allocatedBefore);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Preview of run {RunId} cancelled by the user", planned.RunId);
            ErrorMessage = "Preview cancelled.";
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a logged error banner instead of an
            // unobserved command fault.
            Log.Error(ex, "Preview of run {RunId} failed unexpectedly", planned.RunId);
            ErrorMessage = $"Preview failed unexpectedly: {ex.Message}";
        }
        finally
        {
            RunStatusText = "";
            IsPreviewing = false;
            // Only if still ours: a discard (or a superseding preview) may have claimed the stage already,
            // and clearing it blindly would let a later discard fall through every branch.
            if (_loadingRunId == planned.RunId)
                _loadingRunId = null;
        }
    }

    /// <summary>The banner text for a preview that could not be opened. A lost transport is called out
    /// separately because it points somewhere specific: the service died, and its log says why.</summary>
    private static string PreviewFailure(IpcError error) =>
        error.Code == "IPC_TRANSPORT"
            ? $"Preview failed: {error.Message}. The connection to the background service was lost unexpectedly — see the service log in %LOCALAPPDATA%\\FileManager\\logs."
            : $"Preview failed: {error.Message}";

    /// <summary>Re-shows a retained preview by re-streaming its plan from the run's snapshot.
    ///
    /// <para><b>Re-streamed rather than cached in memory, deliberately.</b> A materialized preview costs
    /// ~200–350 MB of process footprint at scale (see <c>docs/dry-run-ui-memory-next-steps.md</c>), so
    /// keeping several on screen's worth of rows alive to make a tab switch instant would trade the app's
    /// whole memory budget for a second or two of latency. The rows already exist on disk in the run's
    /// snapshot, and <c>get-run-plan-stream</c> replays them as the same frames a fresh preview ingests —
    /// so this reuses <see cref="LoadPlanAsync"/> whole rather than adding a second ingest path that could
    /// disagree with it.</para>
    ///
    /// <para>Returns false when the run is gone, which is the ORDINARY case after a service restart: the
    /// engine sweeps its runs directory at startup and keeps no run table across processes. The caller
    /// drops the stored entry and shows the normal empty state — this is not an error and raises no
    /// banner.</para></summary>
    public async Task<bool> RestoreAsync(StoredPreview stored, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stored);
        // Same clear-first sequence a fresh preview uses, so the tab shows a progress bar rather than the
        // previous profile's rows while this streams. State-only: the run being restored must survive.
        ClearReport();
        ForgetPendingPlan();
        AbandonPlanningRun();
        EmptyStateText = DefaultEmptyState;
        IsPreviewing = true;
        RunStatusText = "Reopening the last preview…";

        int epoch = _reportEpoch;
        await LoadPlanAsync(stored.Planned, ct).ConfigureAwait(true);
        if (_reportEpoch != epoch)
            return true;   // superseded mid-restore; the newer action owns the tab now

        // LoadPlanAsync reports a vanished run through ErrorMessage, since for a FRESH plan that really is a
        // failure. For a restore it is expected, so translate it back into the benign empty state and let
        // the caller forget the entry.
        if (PendingRunId is null)
        {
            ErrorMessage = null;
            EmptyStateText = "The saved preview has expired — preview again to see what a run would do.";
            PreviewTakenAtUtc = null;
            return false;
        }
        return true;
    }

    // ── Answering the pending run ─────────────────────────────────────────────────────────────
    /// <summary>Set by the shell, and called with the user's answer once the engine has accepted it.
    /// Mirrors <see cref="DraftProvider"/>: this view model owns the gateway call, the shell owns what
    /// happens around it (the activity notice, and opening the panel on an approval — the payoff is
    /// watching the run land). A null callback is silent, so headless tests are not blocked.</summary>
    public Action<bool>? RunAnswered { get; set; }

    /// <summary>Starts the run whose plan is on screen. THIS is the moment files begin to move — every
    /// preceding phase was read-only by construction.</summary>
    [RelayCommand(CanExecute = nameof(CanApproveRun))]
    private Task ApproveRunAsync() => AnswerPendingRunAsync(approve: true);

    /// <summary>Declines the run whose plan is on screen, closing it and changing nothing.</summary>
    [RelayCommand]
    private Task DeclineRunAsync() => AnswerPendingRunAsync(approve: false);

    private async Task AnswerPendingRunAsync(bool approve)
    {
        if (PendingRunId is not Guid runId)
            return;
        // Captured before the clear below drops it: the engine refuses an approval of a run with blocking
        // warnings unless this says the user confirmed them.
        bool acknowledged = AcknowledgedWarnings;
        // Cleared FIRST, so a double-click cannot answer the same run twice — the second call would be
        // refused with RUN_NOT_APPROVABLE and land in the error banner for no reason.
        PendingRunId = null;
        try
        {
            var answered = await _gateway.ApproveRunAsync(runId, approve, acknowledged);
            if (answered.IsCanceled)
                return;
            if (answered.TryGetError(out IpcError? error))
            {
                ErrorMessage = $"Could not {(approve ? "start" : "cancel")} the run: {error.Message}";
                return;
            }
            RunAnswered?.Invoke(approve);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a command has no exception boundary of its own.
            Log.Error(ex, "Answering run {RunId} with approve={Approve} failed", runId, approve);
            ErrorMessage = $"Could not {(approve ? "start" : "cancel")} the run: {ex.Message}";
        }
    }

    /// <summary>Declines the pending run without narrating it.
    ///
    /// <para><b>No longer called on a profile switch or a superseding preview</b> — those retain the run
    /// now, and <c>PreviewStore</c> owns declining what it supersedes. What remains is the case where the
    /// run must genuinely go and no store entry will account for it: a plan the shell answered on the
    /// user's behalf (nothing to do), and any path that decided this window is done with the run outright.
    ///
    /// <para>Fire-and-forget: the caller is mid-transition and the answer is not interesting, and an
    /// already-closed run answering RUN_NOT_APPROVABLE is a normal race here.</para></summary>
    public void AbandonPendingRun()
    {
        if (ForgetPendingPlan() is not Guid runId)
            return;
        Superseded?.Invoke(runId);
        _ = _gateway.ApproveRunAsync(runId, approve: false)
            .ContinueWith(
                t => Log.Debug(t.Exception, "Declining superseded run {RunId} failed", runId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>The run whose plan this tab is currently streaming, or null. Distinct from both
    /// <see cref="PlanningRunId"/> (the engine is still walking) and <see cref="PendingRunId"/> (the rows
    /// are on screen): this is the window in between, and it is the one a discard used to fall through.</summary>
    private Guid? _loadingRunId;

    private const string DiscardedEmptyState =
        "That preview was discarded — preview again to see what a run would do.";

    /// <summary>Lets go of a run the user discarded from the job queue, whatever stage it had reached here.
    ///
    /// <para><b>All three stages matter, and only one used to be handled.</b> A preview passes through
    /// PLANNING (the engine is walking, <see cref="PlanningRunId"/> set), then LOADING (the plan is
    /// streaming into the store), then PENDING (rows on screen, footer up). The original version checked
    /// only the last, so discarding a run that was still planning left <see cref="IsPreviewing"/> true and
    /// the tab's progress bar spinning forever — and it could not recover, because the shell drops the run
    /// from its own id set at the same moment, so the late <c>run-planned</c> that would otherwise have
    /// ended the wait was no longer recognized as ours.</para></summary>
    public void ForgetDiscardedRun(Guid runId)
    {
        if (PlanningRunId == runId)
        {
            // EndPlanning clears PlanningRunId, IsPreviewing and the caption — the whole spinning state.
            EndPlanning(emptyState: DiscardedEmptyState);
            return;
        }
        if (_loadingRunId == runId)
        {
            // Nulled so the in-flight LoadPlanAsync recognizes itself as superseded and returns quietly
            // instead of raising "Preview failed: no run with id …" — which is technically true and
            // entirely unhelpful, since the user is the one who discarded it.
            _loadingRunId = null;
            ForgetPendingPlan();
            EmptyStateText = DiscardedEmptyState;
            return;
        }
        if (PendingRunId == runId)
        {
            ForgetPendingPlan();
            EmptyStateText = DiscardedEmptyState;
        }
    }

    /// <summary>Drops the footer's state and returns the run id it was holding, if any. State only — it
    /// tells the engine nothing.</summary>
    private Guid? ForgetPendingPlan()
    {
        Guid? runId = PendingRunId;
        PendingRunId = null;
        PlannedCopies = PlannedDeletes = 0;
        PlannedCopyBytes = PlannedDeleteBytes = 0;
        PlanTruncated = false;
        // Both cleared with the plan they belong to: an acknowledgment is of THESE warnings on THIS run,
        // and carrying either into the next plan would acknowledge something the user never saw.
        BlockingIssues = [];
        AcknowledgedWarnings = false;
        PreviewTakenAtUtc = null;   // no plan on screen, so nothing has an age
        return runId;
    }

    /// <summary>Everything <see cref="ApplyPrepared"/> assigns: each tab's handle onto its half of the
    /// plan, plus the banner numbers. All of it comes from the run's snapshot header, so building it is
    /// O(1) in the plan's size — there is no pass over rows here to move off the UI thread.</summary>
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

    /// <summary>Turns a run's snapshot header and its two opened views into both tabs' bound state.
    ///
    /// <para><b>What used to be here.</b> Two <c>ComputeLoad</c> passes over every ingested row, run
    /// concurrently because together they were seconds of CPU: each folded the facet and status counts
    /// and then sorted the whole half against a materialized array of relative-path keys — ~147 MB of
    /// transient at 500,000 files, on top of the ~72 MB the rows themselves cost, both linear in a plan
    /// size that is no longer bounded. The service records the counts during the plan's own walk and
    /// keeps the order in a sidecar, so all of it is now a header read.</para></summary>
    internal static PreparedReport PrepareReport(
        IIpcGateway gateway,
        Guid runId,
        RunDetailDto detail,
        RunPlanViewResponse sourcesView,
        RunPlanViewResponse destinationsView,
        DryRunCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(sourcesView);
        ArgumentNullException.ThrowIfNull(destinationsView);
        ArgumentNullException.ThrowIfNull(completion);

        RunPlanPreviewAggregates preview = detail.Preview ?? new RunPlanPreviewAggregates();

        // Both roots are derived HERE, from the facet keys, because the rule for reducing a set of roots
        // to the folder they share lives in DryRunPaths — which is a UI type the service cannot
        // reference. A service-side copy would be a second implementation free to disagree about UNC
        // shares. O(roots), which is a handful.
        string? sourceRoot = DryRunPaths.CommonRoot(preview.SourceRowsByRoot.Keys);
        string? destinationRoot = DryRunPaths.CommonRoot(preview.DestinationRowsByRoot.Keys);

        (int untouched, int added, int overwritten, int deleted) =
            DryRunDestinationsTab.ChipCounts(preview.DestinationRowsByKind);

        DryRunSourcesTab.LoadData sourcesLoad = new(
            gateway, runId, sourcesView.ViewId, sourcesView.RowCount,
            sourceRoot, destinationRoot,
            preview.UntouchedCount, preview.ProcessedCount, detail.DisposalCount,
            preview.SourceRowsByRoot, preview.DestinationRowsByRoot);

        DryRunDestinationsTab.LoadData destinationsLoad = new(
            gateway, runId, destinationsView.ViewId, destinationsView.RowCount,
            sourceRoot, destinationRoot,
            untouched, added, overwritten, deleted,
            preview.DestinationRowsByRoot);

        return new PreparedReport(
            sourcesLoad,
            destinationsLoad,
            TotalFiles: detail.SourceItemCount,
            OverwriteCount: detail.OverwriteCount,
            RenameCount: detail.RenameCount,
            DisposalCount: detail.DisposalCount,
            HasDestructiveActions:
                detail.OverwriteCount > 0 || detail.DisposalCount > 0 || detail.DeleteItemCount > 0,
            GeneratedAtText: $"Generated {completion.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            WasTruncated: completion.Truncated,
            TruncationNotice: completion.Truncated
                ? $"Report truncated: showing the first {detail.SourceItemCount:N0} files — the scan could not read the whole tree. Destination deletions are not shown for a truncated report."
                : "",
            Space: completion.Space is { Volumes.Count: > 0 } projection
                ? new DryRunSpaceViewModel(projection)
                : null);
    }

    /// <summary>The UI-thread half of applying a report: assigns the banner properties and hands each tab
    /// its load.</summary>
    internal void ApplyPrepared(PreparedReport prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
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
        // The run is NO LONGER declined here, and that is the change retained previews are made of. Looking
        // away from a profile is not abandoning its result: the run stays parked so the rows can be
        // re-streamed from its snapshot when the user comes back. Only the footer's state is dropped, so
        // the tab cannot approve a plan whose rows are no longer on screen.
        //
        // The obligation that used to live here has moved to PreviewStore, which declines an entry it
        // supersedes and declines every entry on window close. Nothing else may leave a run parked.
        ForgetPendingPlan();
        PreviewTakenAtUtc = null;
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
