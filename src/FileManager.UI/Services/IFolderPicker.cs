using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>Native folder/file selection behind an interface so viewmodels stay Avalonia-free.</summary>
public interface IFolderPicker
{
    /// <summary>Null when the user cancels. <paramref name="startNear"/> is a folder the caller
    /// already points at: the picker opens at its PARENT, so that folder is the visible entry. When
    /// it is null, blank, or no longer exists the picker opens at the user's Downloads folder.</summary>
    Task<string?> PickFolderAsync(string title, string? startNear = null);

    /// <summary>Opens a multi-select picker for JSON profile files. Empty when the user cancels.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);
}
