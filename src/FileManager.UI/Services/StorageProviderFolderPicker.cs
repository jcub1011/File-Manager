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
    public async Task<string?> PickFolderAsync(string title, string? startNear = null)
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await window.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    SuggestedStartLocation = await ResolveStartAsync(startNear),
                });
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

    /// <summary>The dialog's opening location: the parent of <paramref name="startNear"/> (so that
    /// folder is the visible entry), else Downloads — the folder this tool's users live in. Null is
    /// a valid answer too: it leaves the platform to pick, which is what happens when even Downloads
    /// cannot be resolved.</summary>
    private async Task<IStorageFolder?> ResolveStartAsync(string? startNear)
    {
        if (FolderPickerStart.ParentOf(startNear) is { } parent
            && await window.StorageProvider.TryGetFolderFromPathAsync(parent) is { } folder)
            return folder;
        return await window.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        try
        {
            FilePickerFileType jsonFiles = new("Profile files") { Patterns = ["*.json"] };
            IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { Title = title, AllowMultiple = true, FileTypeFilter = [jsonFiles] });

            List<string> paths = [];
            foreach (IStorageFile file in files)
            {
                string? path = file.TryGetLocalPath();
                if (path is not null)
                    paths.Add(path);
            }
            return paths;
        }
        catch (Exception ex)
        {
            // Last resort: a failed native picker reads as "nothing picked" — logged so it is
            // traceable — instead of faulting the Import command unobserved.
            Log.Error(ex, "File picker failed unexpectedly");
            return [];
        }
    }
}
