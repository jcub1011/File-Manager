using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>Native folder/file selection behind an interface so viewmodels stay Avalonia-free.</summary>
public interface IFolderPicker
{
    /// <summary>Null when the user cancels.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Opens a multi-select picker for JSON profile files. Empty when the user cancels.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);
}
