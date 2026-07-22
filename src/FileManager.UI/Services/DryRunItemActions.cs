using Avalonia.Controls;
using Avalonia.Input.Platform;
using Serilog;
using System;
using System.Diagnostics;

namespace FileManager.UI.Services;

/// <summary>Window-injected implementation of the dry-run right-click actions (clipboard access
/// needs a <see cref="TopLevel"/>). Every action is best-effort: a failure is logged and swallowed
/// so a right-click can never fault the app (cf. <see cref="LogFolderService"/>).</summary>
public sealed class DryRunItemActions(Window window) : IDryRunItemActions
{
    public void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            // Fire-and-forget: SetTextAsync is async, but the menu command is sync and the result
            // is not awaited anywhere; observe faults so a clipboard failure is logged, not lost.
            _ = window.Clipboard?.SetTextAsync(text).ContinueWith(
                t => Log.Warning(t.Exception, "Could not copy text to the clipboard"),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not copy text to the clipboard");
        }
    }

    // UseShellExecute = true → the shell opens the file in its default app / the folder in Explorer
    // (cf. LogFolderService).
    public void OpenFile(string path) => ShellOpen(path);

    public void OpenFolderInExplorer(string path) => ShellOpen(path);

    public void RevealInExplorer(string path)
    {
        try
        {
            // explorer.exe /select,"path" opens the containing folder with the file highlighted.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not reveal {Path} in Explorer", path);
        }
    }

    private static void ShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Path}", path);
        }
    }
}
