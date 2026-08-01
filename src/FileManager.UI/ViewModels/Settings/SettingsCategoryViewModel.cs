using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FileManager.UI.ViewModels.Settings;

/// <summary>A group of settings — one section header in the scrolling document and one branch in the
/// navigation tree. Categories are declared in <see cref="SettingsViewModel"/>.<c>BuildCatalog</c>;
/// nesting is expressed by giving a category a <see cref="Parent"/>, so a future
/// "Performance → Advanced → …" level needs no new type.</summary>
public sealed partial class SettingsCategoryViewModel : ViewModelBase
{
    public SettingsCategoryViewModel(string id, string title, string? description = null, SettingsCategoryViewModel? parent = null)
    {
        Id = id;
        Title = title;
        Description = description;
        Parent = parent;
    }

    public string Id { get; }
    public string Title { get; }

    /// <summary>Optional prose for the group as a whole, shown under the section header. Per-setting
    /// explanations belong on the settings themselves so search can find them.</summary>
    public string? Description { get; }

    public SettingsCategoryViewModel? Parent { get; }

    /// <summary>True when this category's settings live in the service-owned <c>GlobalSettings</c> and
    /// therefore cannot be read or written while the service is unreachable. Declared once per category
    /// rather than per setting: ownership is exactly what the categories now group by, and each item
    /// reads it back through <see cref="SettingItemViewModel.RequiresService"/>.</summary>
    public bool RequiresService { get; init; }

    /// <summary>Header text in the document. A nested category reads as "Performance → Per-drive
    /// overrides" so a filtered view still says where the group sits.</summary>
    public string HeaderText => Parent is null ? Title : $"{Parent.Title} → {Title}";

    public ObservableCollection<SettingItemViewModel> Items { get; } = [];

    /// <summary>False when the search query matches none of this category's settings, which hides the
    /// whole section (header included) from the document and the tree.</summary>
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    /// <summary>Registers settings into this category, back-linking each one so its category title
    /// participates in search.</summary>
    internal SettingsCategoryViewModel With(params IEnumerable<SettingItemViewModel> items)
    {
        foreach (SettingItemViewModel item in items)
        {
            item.Category = this;
            Items.Add(item);
        }
        return this;
    }
}
