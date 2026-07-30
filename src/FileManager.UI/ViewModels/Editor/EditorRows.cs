using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using System;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels.Editor;

public sealed partial class SourceRowViewModel(IFolderPicker folderPicker) : ViewModelBase
{
    [ObservableProperty]
    public partial string Path { get; set; } = "";

    [ObservableProperty]
    public partial int SettleDelaySeconds { get; set; } = 2;

    [ObservableProperty]
    public partial int StabilityIntervalMs { get; set; } = 500;

    /// <inheritdoc cref="TargetRowViewModel.PreviousRowPath"/>
    public Func<string?>? PreviousRowPath { get; set; }

    [RelayCommand]
    public async Task BrowseAsync()
    {
        string? picked = await folderPicker.PickFolderAsync(
            "Select a source folder", RowBrowsing.StartNear(Path, PreviousRowPath));
        if (picked is not null)
            Path = picked;
    }
}

public sealed partial class TargetRowViewModel(IFolderPicker folderPicker) : ViewModelBase
{
    [ObservableProperty]
    public partial string Path { get; set; } = "";

    /// <summary>Set by the editor: the path of the nearest filled row ABOVE this one, so Browse on
    /// an empty row starts where the previous row points instead of at Downloads. Resolved on demand
    /// (not captured at construction) so adding and removing rows needs no re-wiring.</summary>
    public Func<string?>? PreviousRowPath { get; set; }

    [RelayCommand]
    public async Task BrowseAsync()
    {
        string? picked = await folderPicker.PickFolderAsync(
            "Select a target folder", RowBrowsing.StartNear(Path, PreviousRowPath));
        if (picked is not null)
            Path = picked;
    }
}

/// <summary>Shared by both row kinds: the folder a Browse click should open near.</summary>
internal static class RowBrowsing
{
    /// <summary>This row's own folder when it has one, else the row above's. Existence is not checked
    /// here — <c>FolderPickerStart</c> does that once, so a stale path still lands on Downloads.</summary>
    public static string? StartNear(string path, Func<string?>? previousRowPath) =>
        string.IsNullOrWhiteSpace(path) ? previousRowPath?.Invoke() : path;
}

/// <summary>Display projection of a ValidationIssue (severity glyph + code + message).</summary>
public sealed record ValidationIssueItem(ValidationSeverity Severity, string Code, string Message)
{
    public string Glyph => Severity switch
    {
        ValidationSeverity.Error => "⛔",
        ValidationSeverity.BlockingWarning => "⚠️",
        _ => "ℹ️",
    };

    public bool IsError => Severity == ValidationSeverity.Error;
    public bool IsBlockingWarning => Severity == ValidationSeverity.BlockingWarning;
}
