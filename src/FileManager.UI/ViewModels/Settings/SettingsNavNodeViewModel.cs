using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace FileManager.UI.ViewModels.Settings;

/// <summary>One node of the Settings window's navigation tree. A node either stands for a category
/// (branch, with the category's settings and any nested categories as children) or for a single
/// setting (leaf); selecting it scrolls the document to that setting.
///
/// The tree is built from the catalog in <see cref="SettingsViewModel"/>.<c>BuildNavigation</c>, so a
/// newly registered setting appears here with no extra work.</summary>
public sealed partial class SettingsNavNodeViewModel : ViewModelBase
{
    private SettingsNavNodeViewModel(string title) => Title = title;

    public static SettingsNavNodeViewModel ForCategory(SettingsCategoryViewModel category) =>
        new(category.Title) { Category = category };

    public static SettingsNavNodeViewModel ForSetting(SettingItemViewModel setting) =>
        new(setting.Title) { Setting = setting };

    public string Title { get; }

    /// <summary>Set on a branch node. Selecting it jumps to the category's first visible setting.</summary>
    public SettingsCategoryViewModel? Category { get; private init; }

    /// <summary>Set on a leaf node — the setting to scroll into view.</summary>
    public SettingItemViewModel? Setting { get; private init; }

    public ObservableCollection<SettingsNavNodeViewModel> Children { get; } = [];

    /// <summary>False when the search query matches nothing under this node. Applied to the generated
    /// <c>TreeViewItem</c> (see the <c>TreeView.settingsNav</c> style) so a filtered-out node leaves no
    /// gap behind.</summary>
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    /// <summary>Categories start expanded — with a search active every surviving branch is expanded so
    /// the matches are visible without a click.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
}
