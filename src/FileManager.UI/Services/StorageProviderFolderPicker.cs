using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>Folder pickers use the native Avalonia storage providers, not IPC browse
/// (architecture §2.3 — the GUI runs as the same user on the same machine).</summary>
public sealed class StorageProviderFolderPicker(Window window) : IFolderPicker
{
    public async Task<string?> PickFolderAsync(string title)
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await window.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            // Last resort: a failed native picker reads as "nothing picked" — logged so it is
            // traceable — instead of faulting the Browse command unobserved.
            Log.Error(ex, "Folder picker failed unexpectedly");
            return null;
        }
    }
}
