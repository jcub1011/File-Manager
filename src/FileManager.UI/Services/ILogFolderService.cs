namespace FileManager.UI.Services;

/// <summary>Opens the app's log folder in the OS file explorer. Behind an interface so
/// viewmodels stay testable and free of <c>Process.Start</c> (mirrors <see cref="IFolderPicker"/>).</summary>
public interface ILogFolderService
{
    void OpenLogFolder();
}
