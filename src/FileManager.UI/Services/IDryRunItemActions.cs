namespace FileManager.UI.Services;

/// <summary>The clipboard + shell actions behind the dry-run tree/list right-click menus. Behind
/// an interface so the row/node viewmodels stay testable and free of <c>Process.Start</c> and
/// Avalonia clipboard types (mirrors <see cref="ILogFolderService"/> / <see cref="IFolderPicker"/>).
/// A failed action is logged, never thrown — a right-click must never fault the app.</summary>
public interface IDryRunItemActions
{
    /// <summary>Puts <paramref name="text"/> on the system clipboard (no-op when null/empty).</summary>
    void CopyText(string? text);

    /// <summary>Opens the file in its default application (shell execute).</summary>
    void OpenFile(string path);

    /// <summary>Opens Explorer with the file selected (<c>explorer.exe /select,"path"</c>).</summary>
    void RevealInExplorer(string path);

    /// <summary>Opens the folder in Explorer (shell execute).</summary>
    void OpenFolderInExplorer(string path);
}
