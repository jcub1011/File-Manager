using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.UI.Extensions;
using FileManager.UI.Undo;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Input;

namespace FileManager.UI.ViewModels.Settings;

/// <summary>One editable setting in the Settings window, carrying its own presentation metadata
/// (title, description, search keywords) rather than leaving it as literal strings in XAML. That is
/// what lets the search box, the navigation tree, and the scroll anchors all be generated from the
/// catalog: registering a setting is a single entry in
/// <see cref="SettingsViewModel"/>.<c>BuildCatalog</c> with no view changes.
///
/// To add a setting whose editor is one of the stock kinds below, use the <see cref="Setting"/>
/// factories. To add one that needs a hand-written editor, derive from this class and add one
/// <c>DataTemplate</c> to <c>SettingsWindow.axaml</c> — see <see cref="DriveOverridesSettingViewModel"/>
/// for the worked example.</summary>
public abstract partial class SettingItemViewModel : ViewModelBase
{
    protected SettingItemViewModel(string id, string title, string description, IReadOnlyList<string>? keywords = null)
    {
        Id = id;
        Title = title;
        Description = description;
        Keywords = keywords ?? [];
    }

    /// <summary>Stable identity, used by the navigation tree to address this setting and by tests to
    /// assert the catalog has no duplicates. Never shown to the user.</summary>
    public string Id { get; }

    public string Title { get; }
    public string Description { get; }

    /// <summary>Extra search terms that do not appear in the title or description — synonyms and the
    /// words a user is likely to type ("cpu", "concurrency", "folder"). Searched like the rest.</summary>
    public IReadOnlyList<string> Keywords { get; }

    /// <summary>The owning category, assigned when the item is registered. Its title participates in
    /// search, so "performance" finds every setting under Performance.</summary>
    public SettingsCategoryViewModel Category { get; internal set; } = null!;

    /// <summary>False for a setting whose edit is NOT reversible, which excludes it from both the undo
    /// stack and the window's unsaved-changes flag. Only the profiles directory needs it: changing that
    /// moves files on disk immediately, so there is nothing to undo and nothing pending to save. Cannot
    /// be decided by editor kind — the profiles and scratch directories are both
    /// <see cref="TextSettingViewModel"/>.</summary>
    public bool IsUndoable { get; init; } = true;

    /// <summary>Whether this setting is stored in the service-owned <c>GlobalSettings</c>, and so cannot
    /// be edited while the service is unreachable. Read from the owning category — ownership is what the
    /// categories group by, so declaring it per setting would just be a chance to get it wrong.</summary>
    public bool RequiresService => Category is { RequiresService: true };

    /// <summary>False while this setting cannot be edited — today, a service-owned setting whose stored
    /// values could not be loaded. The editor greys out and <see cref="DisabledTooltip"/> explains why;
    /// the card around it stays enabled so that tooltip can actually be shown.</summary>
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    /// <summary>Why this setting is greyed out, or null while it is editable — bound straight to
    /// <c>ToolTip.Tip</c>, where null means "no tooltip", so an editable setting shows nothing.</summary>
    public string? DisabledTooltip => IsEnabled ? null : ServiceUnavailableTooltip;

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(DisabledTooltip));

    /// <summary>The only disabled-reason there is so far. Points at the fix rather than just stating the
    /// problem: the service executable path is the one setting that stays editable, and it is the usual
    /// cause of an unreachable service.</summary>
    internal const string ServiceUnavailableTooltip =
        "The background service could not be reached, so its settings cannot be changed. Check the " +
        "service executable path above, then reopen this window once the service is running.";

    /// <summary>False when the current search query does not match this setting. The view collapses the
    /// item rather than removing it, so no collection churn and no lost focus.</summary>
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    /// <summary>True for the setting the navigation tree last scrolled to; the view draws it with an
    /// accent edge so the target is obvious after a jump.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    private string? _haystack;

    /// <summary>The lower-cased text this setting is searched against, built once on first use from the
    /// title, description, keywords, category title, and whatever <see cref="AppendSearchText"/>
    /// contributes. Cached because the catalog is immutable once built.</summary>
    internal string Haystack => _haystack ??= BuildHaystack();

    /// <summary>Hook for a subclass to contribute searchable text that is not in the title or
    /// description — most importantly the labels of the values a user can pick, so typing "dark" finds
    /// the Theme setting even though "dark" appears nowhere in its prose.</summary>
    protected virtual void AppendSearchText(List<string> parts) { }

    private string BuildHaystack()
    {
        List<string> parts = [Title, Description, Category?.Title ?? ""];
        parts.AddRange(Keywords);
        AppendSearchText(parts);
        return string.Join(' ', parts).ToLowerInvariant();
    }
}

/// <summary>One selectable value of a <see cref="ChoiceSettingViewModel"/>. <see cref="Value"/> is
/// boxed so the view can bind the non-generic base; the typed subclass unboxes it.</summary>
public sealed record ChoiceOption(object? Value, string Label);

/// <summary>A setting picked from a fixed list of values (rendered as a ComboBox). The non-generic base
/// exists purely so XAML has something to bind — Avalonia's compiled bindings need a concrete
/// <c>x:DataType</c>, which a generic type cannot supply.</summary>
public abstract partial class ChoiceSettingViewModel : SettingItemViewModel, IUndoTrackable
{
    protected ChoiceSettingViewModel(
        string id, string title, string description, IReadOnlyList<ChoiceOption> options, IReadOnlyList<string>? keywords)
        : base(id, title, description, keywords)
        => Options = options;

    public IReadOnlyList<ChoiceOption> Options { get; }

    [ObservableProperty] public partial ChoiceOption? SelectedOption { get; set; }

    /// <summary>Only <see cref="SelectedOption"/>, never the typed <c>Value</c> of the subclass: that is a
    /// pass-through with no storage of its own, re-announced from
    /// <see cref="OnSelectedOptionChangedCore"/>, so tracking both would record one pick twice. Never
    /// coalesced — each pick from the list is a decision worth its own undo step.</summary>
    public IEnumerable<UndoableProperty> UndoableProperties =>
        [UndoableProperty.For(nameof(SelectedOption), () => SelectedOption, v => SelectedOption = v)];

    /// <summary>Every option label is searchable, so a user can find a setting by the value they want
    /// ("dark", "run on startup") rather than having to know what the setting is called.</summary>
    protected override void AppendSearchText(List<string> parts)
    {
        foreach (ChoiceOption option in Options)
            parts.Add(option.Label);
    }

    // The generated change hook belongs to whichever class declares the property, so the typed subclass
    // cannot implement it directly — it overrides this instead to re-announce its typed Value.
    partial void OnSelectedOptionChanged(ChoiceOption? value) => OnSelectedOptionChangedCore();

    protected virtual void OnSelectedOptionChangedCore() { }
}

/// <summary>A <see cref="ChoiceSettingViewModel"/> over an enum, exposing a strongly-typed
/// <see cref="Value"/> for the load/save mapping and for tests while the view binds the base.</summary>
public sealed partial class ChoiceSettingViewModel<T> : ChoiceSettingViewModel
    where T : struct, Enum
{
    public ChoiceSettingViewModel(
        string id, string title, string description, IReadOnlyList<ChoiceOption> options, IReadOnlyList<string>? keywords)
        : base(id, title, description, options, keywords)
        => SelectedOption = options.Count > 0 ? options[0] : null;

    /// <summary>The selected value. Reads and writes go through <see cref="SelectedOption"/> so the
    /// ComboBox and the typed surface can never disagree.</summary>
    public T Value
    {
        get => SelectedOption?.Value is T typed ? typed : default;
        set
        {
            foreach (ChoiceOption option in Options)
            {
                if (option.Value is T candidate && EqualityComparer<T>.Default.Equals(candidate, value))
                {
                    SelectedOption = option;
                    return;
                }
            }
        }
    }

    protected override void OnSelectedOptionChangedCore() => OnPropertyChanged(nameof(Value));
}

/// <summary>How loudly a <see cref="TextSettingViewModel.Notice"/> reads.</summary>
public enum SettingNoticeSeverity
{
    /// <summary>Confirms what the value resolves to. Nothing is wrong.</summary>
    Info,

    /// <summary>The value is not doing what the user probably expects, but the app still works —
    /// it fell back, or the file is not the program it should be.</summary>
    Warning,

    /// <summary>The value cannot work and nothing else covers for it.</summary>
    Error,
}

/// <summary>A free-text setting, optionally read-only and optionally paired with a trailing action
/// button ("Browse…", "Change…"), and optionally carrying a <see cref="Notice"/> — a live line under
/// the box saying what the value actually resolves to.</summary>
public sealed partial class TextSettingViewModel : SettingItemViewModel, IUndoTrackable
{
    public TextSettingViewModel(
        string id, string title, string description, IReadOnlyList<string>? keywords = null)
        : base(id, title, description, keywords) { }

    [ObservableProperty] public partial string Value { get; set; } = "";

    /// <summary>Live feedback about what <see cref="Value"/> resolves to, shown under the box. Null
    /// when there is nothing to say. This is deliberately NOT the same thing as the window's
    /// save-time error banner: a path can be wrong in a way that is worth telling the user about
    /// without being worth refusing to save, and the user needs to see it while typing rather than
    /// only after committing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeIsWarning))]
    [NotifyPropertyChangedFor(nameof(NoticeIsError))]
    public partial SettingNoticeSeverity NoticeSeverity { get; set; }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    // Bound as style classes. Booleans rather than a converter because compiled bindings want a
    // concrete member, and two of them are cheaper than a value converter for three states.
    public bool NoticeIsWarning => NoticeSeverity == SettingNoticeSeverity.Warning;
    public bool NoticeIsError => NoticeSeverity == SettingNoticeSeverity.Error;

    /// <summary>Sets both halves of the notice in one step.</summary>
    public void SetNotice(string? text, SettingNoticeSeverity severity = SettingNoticeSeverity.Info)
    {
        NoticeSeverity = severity;
        Notice = text;
    }

    /// <summary>Coalesced: a typed-in path arrives one keystroke at a time and should step back as one
    /// edit, not character by character.</summary>
    public IEnumerable<UndoableProperty> UndoableProperties =>
        [UndoableProperty.For(nameof(Value), () => Value, v => Value = v, coalesce: true)];

    public string? PlaceholderText { get; init; }
    public bool IsReadOnly { get; init; }

    /// <summary>Label of the trailing button. Null means no button.</summary>
    public string? ActionButtonText { get; init; }

    public ICommand? ActionCommand { get; init; }

    public bool HasAction => ActionButtonText is not null && ActionCommand is not null;
}

/// <summary>A numeric setting with an "Auto" escape hatch: while <see cref="Auto"/> is checked the
/// engine picks the number and the spinner is hidden. Covers the scan/hash thread budgets.</summary>
public sealed partial class AutoNumberSettingViewModel : SettingItemViewModel, IUndoTrackable
{
    public AutoNumberSettingViewModel(
        string id, string title, string description, IReadOnlyList<string>? keywords = null)
        : base(id, title, description, keywords) { }

    [ObservableProperty] public partial bool Auto { get; set; } = true;
    [ObservableProperty] public partial int Value { get; set; } = 1;

    public int Minimum { get; init; } = 1;

    public bool ShowValue => !Auto;

    partial void OnAutoChanged(bool value) => OnPropertyChanged(nameof(ShowValue));

    /// <summary>The checkbox is a discrete decision; the spinner is held down and coalesces.
    /// <see cref="ShowValue"/> is derived from <see cref="Auto"/> and deliberately absent — recording it
    /// would double every toggle.</summary>
    public IEnumerable<UndoableProperty> UndoableProperties =>
    [
        UndoableProperty.For(nameof(Auto), () => Auto, v => Auto = v),
        UndoableProperty.For(nameof(Value), () => Value, v => Value = v, coalesce: true),
    ];
}

/// <summary>An on/off setting. Not used by the current catalog — it is here because it is the most
/// likely next editor kind, and having it keeps the first boolean setting a one-line registration.</summary>
public sealed partial class BoolSettingViewModel : SettingItemViewModel, IUndoTrackable
{
    public BoolSettingViewModel(
        string id, string title, string description, IReadOnlyList<string>? keywords = null)
        : base(id, title, description, keywords) { }

    [ObservableProperty] public partial bool Value { get; set; }

    /// <summary>Text beside the checkbox. Defaults to "Enabled" so a registration can omit it.</summary>
    public string CheckBoxLabel { get; init; } = "Enabled";

    public IEnumerable<UndoableProperty> UndoableProperties =>
        [UndoableProperty.For(nameof(Value), () => Value, v => Value = v)];
}

/// <summary>Factories for the stock setting kinds, kept terse so a registration reads as a
/// declaration. See <see cref="SettingsViewModel"/>.<c>BuildCatalog</c> for usage.</summary>
internal static class Setting
{
    /// <summary>An enum-valued setting. Option labels come from <see cref="EnumTitleExtensions.GetTitle"/>
    /// (the <c>[Tooltip]</c> title on the member), so the ComboBox text and the search terms are the
    /// same strings the rest of the app shows. The trimmer annotation must be propagated from
    /// <c>GetTitle</c> — this project publishes NativeAOT.</summary>
    public static ChoiceSettingViewModel<T> Choice<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(
        string id, string title, string description, IReadOnlyList<T> options, string[]? keywords = null)
        where T : struct, Enum
    {
        List<ChoiceOption> choices = new(options.Count);
        foreach (T option in options)
            choices.Add(new ChoiceOption(option, option.GetTitle()));
        return new ChoiceSettingViewModel<T>(id, title, description, choices, keywords);
    }

    /// <summary>A free-text setting. Pass <paramref name="undoable"/> false for one whose action applies
    /// immediately and irreversibly — see <see cref="SettingItemViewModel.IsUndoable"/>.</summary>
    public static TextSettingViewModel Text(
        string id, string title, string description, string[]? keywords = null,
        string? placeholder = null, bool readOnly = false,
        string? actionButtonText = null, ICommand? actionCommand = null, bool undoable = true) =>
        new(id, title, description, keywords)
        {
            PlaceholderText = placeholder,
            IsReadOnly = readOnly,
            ActionButtonText = actionButtonText,
            ActionCommand = actionCommand,
            IsUndoable = undoable,
        };

    public static AutoNumberSettingViewModel AutoNumber(
        string id, string title, string description, string[]? keywords = null, int minimum = 1) =>
        new(id, title, description, keywords) { Minimum = minimum };

    public static BoolSettingViewModel Bool(
        string id, string title, string description, string[]? keywords = null, string? checkBoxLabel = null) =>
        new(id, title, description, keywords) { CheckBoxLabel = checkBoxLabel ?? "Enabled" };
}

/// <summary>Query matching for the settings search box. Split out from the view model so the rule is
/// testable on its own and stays the single definition of "matches".</summary>
internal static class SettingsSearch
{
    private static readonly char[] Separators = [' ', '\t'];

    /// <summary>The query split into terms. An empty or whitespace query yields an empty array, which
    /// <see cref="Matches"/> treats as "everything matches".</summary>
    public static string[] Terms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>All terms must appear somewhere in the haystack (AND, not OR) so adding words narrows
    /// the result the way it does in a search engine. <paramref name="haystack"/> is already
    /// lower-cased by <see cref="SettingItemViewModel.Haystack"/>.</summary>
    public static bool Matches(string haystack, string[] terms)
    {
        foreach (string term in terms)
        {
            if (!haystack.Contains(term, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
