using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
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

    [RelayCommand]
    public async Task BrowseAsync()
    {
        string? picked = await folderPicker.PickFolderAsync("Select a source folder");
        if (picked is not null)
            Path = picked;
    }
}

public sealed partial class TargetRowViewModel(IFolderPicker folderPicker) : ViewModelBase
{
    [ObservableProperty]
    public partial string Path { get; set; } = "";

    [RelayCommand]
    public async Task BrowseAsync()
    {
        string? picked = await folderPicker.PickFolderAsync("Select a target folder");
        if (picked is not null)
            Path = picked;
    }
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
