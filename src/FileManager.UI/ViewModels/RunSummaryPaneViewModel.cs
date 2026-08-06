using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.UI.Extensions;
using System.Collections.Generic;
using System.Linq;

namespace FileManager.UI.ViewModels;

/// <summary>One icon-and-count chip in the summary's blast-radius strip.
///
/// <para>A LIST of these rather than the Preview tab's hand-declared strip of <c>StatusCount</c>s, each
/// behind its own <c>IsVisible</c>: this pane shows six figures instead of four, and building only the
/// chips that apply keeps the conditional logic in one readable place rather than spread across six
/// bindings. <see cref="IconKey"/> and <see cref="ColorKey"/> are resource-key strings resolved in XAML by
/// <c>IconConverters</c>, which is what keeps this file free of any Avalonia reference.</para></summary>
public sealed record RunSummaryChip(string IconKey, string ColorKey, string Text, string Tooltip);

/// <summary>One read-only label/value line in the settings block.</summary>
public sealed record RunSettingRow(string Label, string Value);

/// <summary>One source root as the summary lists it: its path, plus a note when the plan treated it
/// differently from the others.</summary>
public sealed record RunPathRow(string Path, string? Note)
{
    public bool HasNote => Note is not null;
}

/// <summary>A run's plan, summarized — immutable, built in one pass from a <see cref="RunDetailDto"/>.
///
/// <para><b>One object set as a single observable property</b>, rather than twenty properties on the pane
/// view model. That is how <c>DryRunViewModel.Space</c> already carries its own sub-model, and it means a
/// selection change raises one notification instead of twenty half-consistent ones — a pane that showed
/// the new run's counts beside the old run's storage bars for a frame would be worse than one that blinked.</para></summary>
public sealed class RunPlanSummary
{
    public RunPlanSummary(RunDetailDto detail)
    {
        Profile profile = detail.Profile;

        PlannedAtText = $"Planned {detail.PlannedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        // A narrowed run behaves differently in a way no count on this pane reveals: its orphan set is
        // unsound over a partial source tree, so a Mirror run scoped to a subfolder copies but removes
        // nothing. Worth a line of its own precisely because the deletion count below will read zero and
        // otherwise look like "there is nothing to remove".
        ScopeText = detail.ScopePath is { } scope
            ? $"This run covers only \"{scope}\", not the whole profile."
            : null;

        TruncationText = detail.Truncated
            ? "This plan does not cover everything it was asked to. The copies it lists are still safe to "
              + "make, but nothing will be removed — an incomplete plan's orphan list cannot be trusted."
            : null;
        SweepFaultText = detail.SweepFaultDetail;

        // Null on a truncated plan, whose totals over a partial graph would be unsound. The pane hides the
        // whole Storage section rather than showing empty bars.
        Space = detail.Space is { } projection ? new DryRunSpaceViewModel(projection) : null;

        (string dispositionText, bool destroys) =
            SourceDisposition.Describe(profile.Policies.OnSuccess, profile.Policies.ArchiveFolder);
        DispositionText = dispositionText;
        // Shown as a danger bar, not merely as a settings row: the originals are the one thing a run can
        // cost the user something they still had, and a row in a list of eight is skimmable.
        ShowDispositionWarning = destroys;

        Chips = BuildChips(detail);
        Sources = BuildSources(profile);
        Targets = profile.Targets.Select(t => new RunPathRow(t.Path, null)).ToList();
        Settings = BuildSettings(profile);
    }

    public string PlannedAtText { get; }
    public string? ScopeText { get; }
    public bool HasScope => ScopeText is not null;
    public string? TruncationText { get; }
    public bool IsTruncated => TruncationText is not null;
    public string? SweepFaultText { get; }
    public bool HasSweepFault => SweepFaultText is not null;

    public DryRunSpaceViewModel? Space { get; }
    public bool HasSpace => Space is not null;

    public string DispositionText { get; }
    public bool ShowDispositionWarning { get; }

    public IReadOnlyList<RunSummaryChip> Chips { get; }
    public IReadOnlyList<RunPathRow> Sources { get; }
    public IReadOnlyList<RunPathRow> Targets { get; }
    public IReadOnlyList<RunSettingRow> Settings { get; }

    /// <summary>The same figures the Preview tab's chip strip shows, in the same order and with the same
    /// glyphs — this pane and that strip describe one plan, and a reader who has seen one must recognize
    /// the other.
    ///
    /// <para>"Files scanned" is <see cref="RunDetailDto.SourceItemCount"/> and NOT the copy count, which is
    /// the distinction that lets a synchronized profile read as "1,204 scanned, 0 to copy" — up to date —
    /// rather than as a plan that found nothing.</para></summary>
    private static List<RunSummaryChip> BuildChips(RunDetailDto detail)
    {
        List<RunSummaryChip> chips =
        [
            new("IconDocument", "Brush.Text.Secondary", $"{detail.SourceItemCount:N0}",
                "Files the plan looked at"),
            new("IconAdd", "Brush.Info",
                $"{detail.CopyItemCount:N0} ({ByteSize.Format(detail.CopyBytes)})",
                "Files to copy or update"),
        ];
        if (detail.OverwriteCount > 0)
            chips.Add(new("IconOverwrite", "Brush.Danger", $"{detail.OverwriteCount:N0}",
                "Destination files that will be overwritten"));
        if (detail.RenameCount > 0)
            chips.Add(new("IconRename", "Brush.Warning", $"{detail.RenameCount:N0}",
                "Copies that will be written under a suffixed name, because something is already there"));
        if (detail.DisposalCount > 0)
            chips.Add(new("IconTrash", "Brush.Danger", $"{detail.DisposalCount:N0}",
                "Source files that will be moved or deleted once copied"));
        if (detail.DeleteItemCount > 0)
            chips.Add(new("IconTrash", "Brush.Danger",
                $"{detail.DeleteItemCount:N0} ({ByteSize.Format(detail.DeleteBytes)})",
                "Destination files that will be moved to the Recycle Bin"));
        // Only when there is genuinely nothing destructive, and last, so it reads as the verdict on the
        // chips beside it rather than as one more count.
        if (detail.OverwriteCount == 0 && detail.DisposalCount == 0 && detail.DeleteItemCount == 0)
            chips.Add(new("IconCheckmark", "Brush.Success", "", "Nothing will be overwritten or removed"));
        return chips;
    }

    private static List<RunPathRow> BuildSources(Profile profile)
    {
        List<RunPathRow> rows = new(profile.Sources.Count);
        foreach (SourceConfig source in profile.Sources)
        {
            // A per-source filter override is the one thing about a source that changes which files this
            // run touched, so it is the one thing worth noting beside the path. Settle delay and stability
            // interval govern the WATCHER, not a manual run's plan, so they would be noise here.
            rows.Add(new RunPathRow(source.Path,
                source.Filters is not null ? "has its own filters" : null));
        }
        return rows;
    }

    /// <summary>The settings that change what a run DOES — deliberately not a read-only clone of the whole
    /// editor. Anything a reader would need to know to predict this plan's behaviour is here; the rest
    /// (logging verbosity, watcher timings, notification preferences) describes the profile rather than the
    /// run and belongs on the Profile tab.</summary>
    private static List<RunSettingRow> BuildSettings(Profile profile)
    {
        PolicySettings policies = profile.Policies;
        List<RunSettingRow> rows =
        [
            new("Sync mode", profile.SyncMode.GetTitle()),
            new("Target layout", profile.TargetLayout.GetTitle()),
            // The EFFECTIVE value, not the stored flag: Mirror always sweeps whatever ScanDestination
            // says, and reporting the raw field would tell a Mirror reader "No" about a sweep that
            // certainly happened.
            new("Scan destinations", profile.EffectiveScanDestination
                ? profile.SyncMode == SyncMode.Mirror ? "Yes — always, under Mirror" : "Yes"
                : "No"),
            new("If the destination already has the file", policies.ConflictResolution.GetTitle()),
            new("When overwriting", policies.OverwriteHandling.GetTitle()),
            new("Verification", policies.VerificationMethod.GetTitle()),
        ];
        // Only under Mirror, where it is the only setting that says WHEN the destructive half runs.
        // Meaningless otherwise, and a row reading "After copy" on an additive profile that deletes
        // nothing would imply it deletes something.
        if (profile.SyncMode == SyncMode.Mirror)
            rows.Add(new RunSettingRow("Remove orphans", policies.MirrorDeletion.GetTitle()));
        rows.Add(new RunSettingRow("Filters", DescribeFilters(profile.Filters)));
        return rows;
    }

    /// <summary>The filter set in one line. A plan's file count is only interpretable against what was
    /// filtered out of it, so "none" is as load-bearing an answer as a list of globs.</summary>
    internal static string DescribeFilters(FilterSet? filters)
    {
        if (filters is null)
            return "None — every file under the sources";

        List<string> parts = [];
        if (filters.Include is { Count: > 0 } include)
            parts.Add($"include {string.Join(", ", include)}");
        if (filters.ExcludeGlob is { Count: > 0 } exclude)
            parts.Add($"exclude {string.Join(", ", exclude)}");
        if (filters.IncludeRegex is { Count: > 0 } includeRegex)
            parts.Add($"include matching {string.Join(", ", includeRegex)}");
        if (filters.ExcludeRegex is { Count: > 0 } excludeRegex)
            parts.Add($"exclude matching {string.Join(", ", excludeRegex)}");
        if (filters.MinSizeBytes is { } min)
            parts.Add($"at least {ByteSize.Format(min)}");
        if (filters.MaxSizeBytes is { } max)
            parts.Add($"at most {ByteSize.Format(max)}");
        if (filters.ModifiedWithin is { } within)
            parts.Add($"modified within {within}");
        if (filters.ModifiedOlderThan is { } older)
            parts.Add($"modified more than {older} ago");
        if (filters.CreatedWithin is { } created)
            parts.Add($"created within {created}");
        if (filters.MaxDepth is { } depth)
            parts.Add($"at most {depth} level(s) deep");
        if (filters.Attributes is { } attributes)
        {
            // Named only when ON: these three default to off, and listing "hidden: no" for every profile
            // would bury the one case that matters.
            if (attributes.IncludeHidden)
                parts.Add("including hidden files");
            if (attributes.IncludeSystem)
                parts.Add("including system files");
            if (attributes.FollowSymlinks)
                parts.Add("following symlinks");
        }
        // A FilterSet can exist with every member unset, which filters nothing — same answer as no set
        // at all, and it must not read as "some filters, unspecified".
        return parts.Count > 0 ? string.Join(" · ", parts) : "None — every file under the sources";
    }
}

/// <summary>The job queue's summary pane: what the SELECTED run is going to do.
///
/// <para>Fed by <c>get-run-detail</c>, which reads the run's snapshot header alone — so this can be
/// re-fetched on every selection change without replaying a plan that may hold half a million rows. The
/// loaded answer is <see cref="Plan"/>, an immutable snapshot; everything else here is load state.</para>
///
/// <para><b><see cref="HasNoPlanYet"/> is not an error.</b> A run's snapshot header is written when
/// planning FINISHES, so a run the user is watching mid-scan legitimately has none — which is the phase
/// they are most likely to be looking at. It is held apart from <see cref="ErrorText"/> for that reason:
/// the pane says "still working out what this will do" and shows the live scan counts from the row, rather
/// than showing a failure for something that is simply not ready.</para>
///
/// <para>No Avalonia dependency: glyphs and colours leave here as resource-key strings, resolved in XAML
/// by <c>IconConverters</c>, so these types can be tested without an Application. <c>ResourceKeyContractTests</c>
/// is what keeps those keys honest.</para></summary>
public sealed partial class RunSummaryPaneViewModel : ViewModelBase
{
    /// <summary>The loaded summary, or null when there is nothing to show. Set as ONE property — see
    /// <see cref="RunPlanSummary"/> for why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    public partial RunPlanSummary? Plan { get; set; }

    public bool HasPlan => Plan is not null;

    /// <summary>A fetch is in flight. Distinct from <see cref="HasNoPlanYet"/>: this is "asking", that is
    /// "asked, and the answer is that there is no plan yet".</summary>
    [ObservableProperty] public partial bool IsLoading { get; set; }

    /// <summary>The run exists but has not finished planning, so there is no plan to summarize. The normal
    /// state for a run being watched mid-scan, and NOT an error.</summary>
    [ObservableProperty] public partial bool HasNoPlanYet { get; set; }

    /// <summary>A genuine failure — the run vanished, or the service could not answer. Null otherwise, and
    /// never set for <see cref="HasNoPlanYet"/>.</summary>
    [ObservableProperty] public partial string? ErrorText { get; set; }

    /// <summary>Whatever was on screen is no longer what is selected. Called before a fetch starts and when
    /// the selection clears, so the pane can never show one run's summary under another run's name.</summary>
    public void Clear()
    {
        Plan = null;
        HasNoPlanYet = false;
        ErrorText = null;
        IsLoading = false;
    }

    public void BeginLoading()
    {
        Plan = null;
        HasNoPlanYet = false;
        ErrorText = null;
        IsLoading = true;
    }

    public void Show(RunDetailDto detail)
    {
        Plan = new RunPlanSummary(detail);
        HasNoPlanYet = false;
        ErrorText = null;
        IsLoading = false;
    }

    /// <summary>Applies a failed fetch, splitting the one code that is not a failure out of the rest.
    /// <para><c>RUN_NOT_FOUND</c> lands in <see cref="ErrorText"/> rather than being swallowed like the
    /// queue's other run-scoped calls do: elsewhere it means a click raced a run that had already gone and
    /// the row is about to disappear anyway, but here the row is still selected and in front of the user, so
    /// an empty pane with no explanation would just look broken.</para></summary>
    public void Fail(IpcError error)
    {
        Plan = null;
        IsLoading = false;
        if (error.Code == "RUN_PLAN_UNAVAILABLE")
        {
            HasNoPlanYet = true;
            ErrorText = null;
            return;
        }
        HasNoPlanYet = false;
        ErrorText = error.Code == "RUN_NOT_FOUND"
            ? "That run is no longer in the queue."
            : $"Could not load this run's summary: {error.Message}";
    }
}
