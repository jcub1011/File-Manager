using Serilog;
using System;
using System.Diagnostics;
using System.IO;

namespace FileManager.UI.Services;

/// <summary>Opens %LOCALAPPDATA%\FileManager\logs in the shell, creating it first so the
/// button never fails before the first log is written.</summary>
public sealed class LogFolderService : ILogFolderService
{
    public void OpenLogFolder()
    {
        string path = UiPaths.LogsDirectory;
        try
        {
            Directory.CreateDirectory(path);
            // UseShellExecute = true → Explorer opens the directory (cf. ServiceLauncher's launch).
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the log folder {Path}", path);
        }
    }
}
