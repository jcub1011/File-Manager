using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.UI.Extensions;
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
public abstract partial class ChoiceSettingViewModel : SettingItemViewModel
{
    protected ChoiceSettingViewModel(
        string id, string title, string description, IReadOnlyList<ChoiceOption> options, IReadOnlyList<string>? keywords)
        : base(id, title, description, keywords)
        => Options = options;

    public IReadOnlyList<ChoiceOption> Options { get; }

    [ObservableProperty] public partial ChoiceOption? SelectedOption { get; set; }

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

/// <summary>A free-text setting, optionally read-only and optionally paired with a trailing action
/// button ("Browse…", "Change…").</summary>
public sealed partial class TextSettingViewModel : SettingItemViewModel
{
    public TextSettingViewModel(
        string id, string title, string description, IReadOnlyList<string>? keywords = null)
        : base(id, title, description, keywords) { }

    [ObservableProperty] public partial string Value { get; set; } = "";

    public string? PlaceholderText { get; init; }
    public bool IsReadOnly { get; init; }

    /// <summary>Label of the trailing button. Null means no button.</summary>
    public string? ActionButtonText { get; init; }

    public ICommand? ActionCommand { get; init; }

    public bool HasAction => ActionButtonText is not null && ActionCommand is not null;
}

/// <summary>A numeric setting with an "Auto" escape hatch: while <see cref="Auto"/> is checked the
/// engine picks the number and the spinner is hidden. Covers the scan/hash thread budgets.</summary>
public sealed partial class AutoNumberSettingViewModel : SettingItemViewModel
{
    public AutoNumberSettingViewModel(
        string id, string title, string description, IReadOnlyList<string>? keywords = null)
        : base(id, title, description, keywords) { }

    [ObservableProperty] public partial bool Auto { get; set; } = true;
    [ObservableProperty] public partial int Value { get; set; } = 1;

    public int Minimum { get; init; } = 1;

    public bool ShowValue => !Auto;

    partial void OnAutoChanged(bool value) => OnPropertyChanged(nameof(ShowValue));
}

/// <summary>An on/off setting. Not used by the current catalog — it is here because it is the most
/// likely next editor kind, and having it keeps the first boolean setting a one-line registration.</summary>
public sealed partial class BoolSettingViewModel : SettingItemViewModel
{
    public BoolSettingViewModel(
        string id, string title, string description, IReadOnlyList<string>? keywords = null)
        : base(id, title, description, keywords) { }

    [ObservableProperty] public partial bool Value { get; set; }

    /// <summary>Text beside the checkbox. Defaults to "Enabled" so a registration can omit it.</summary>
    public string CheckBoxLabel { get; init; } = "Enabled";
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

    public static TextSettingViewModel Text(
        string id, string title, string description, string[]? keywords = null,
        string? placeholder = null, bool readOnly = false,
        string? actionButtonText = null, ICommand? actionCommand = null) =>
        new(id, title, description, keywords)
        {
            PlaceholderText = placeholder,
            IsReadOnly = readOnly,
            ActionButtonText = actionButtonText,
            ActionCommand = actionCommand,
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
