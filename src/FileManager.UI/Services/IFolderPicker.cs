using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>Native folder selection behind an interface so viewmodels stay Avalonia-free.</summary>
public interface IFolderPicker
{
    /// <summary>Null when the user cancels.</summary>
    Task<string?> PickFolderAsync(string title);
}
