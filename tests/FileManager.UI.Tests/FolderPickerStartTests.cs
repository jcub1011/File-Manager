using FileManager.UI.Services;

namespace FileManager.UI.Tests;

/// <summary>Every folder picker opens at the PARENT of the folder the caller already points at, so
/// that folder is the visible entry, and falls back to Downloads (a null answer here) when there is
/// nothing usable. This is the one place existence is checked, so the fallback cases are pinned.</summary>
public sealed class FolderPickerStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-picker-" + Guid.NewGuid().ToString("N"));

    public FolderPickerStartTests() => Directory.CreateDirectory(Path.Combine(_root, "child"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp tree is not worth failing a test over.
        }
    }

    [Fact]
    public void An_existing_folder_resolves_to_its_parent()
    {
        Assert.Equal(_root, FolderPickerStart.ParentOf(Path.Combine(_root, "child")));
    }

    [Fact]
    public void A_trailing_separator_does_not_open_inside_the_chosen_folder()
    {
        // GetDirectoryName("…\child\") answers "…\child" — that would open INSIDE the selection.
        Assert.Equal(_root, FolderPickerStart.ParentOf(Path.Combine(_root, "child") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_drive_root_has_no_parent_so_it_opens_at_itself()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Equal(Path.TrimEndingDirectorySeparator(root), FolderPickerStart.ParentOf(root));
    }

    [Fact]
    public void A_folder_that_no_longer_exists_falls_back()
    {
        Assert.Null(FolderPickerStart.ParentOf(Path.Combine(_root, "gone")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\path")]
    [InlineData("\0not a path")]
    public void Nothing_usable_falls_back(string? chosen)
    {
        Assert.Null(FolderPickerStart.ParentOf(chosen));
    }

    // ============================ File pickers ============================
    // A file picker opens at the folder CONTAINING the chosen file, one level lower than ParentOf —
    // which is why it needs its own rule rather than reusing that one.

    [Fact]
    public void An_existing_file_resolves_to_the_folder_containing_it()
    {
        string file = Path.Combine(_root, "child", "FileManager.Service.exe");
        File.WriteAllText(file, "x");

        Assert.Equal(Path.Combine(_root, "child"), FolderPickerStart.DirectoryOf(file));
    }

    [Fact]
    public void An_existing_folder_opens_at_itself_rather_than_at_its_parent()
    {
        // What a half-typed path looks like: the user has navigated to the folder but not picked a file.
        Assert.Equal(Path.Combine(_root, "child"), FolderPickerStart.DirectoryOf(Path.Combine(_root, "child")));
    }

    [Fact]
    public void A_file_that_no_longer_exists_falls_back()
    {
        Assert.Null(FolderPickerStart.DirectoryOf(Path.Combine(_root, "child", "gone.exe")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0not a path")]
    public void Nothing_usable_falls_back_for_files_too(string? chosen)
    {
        Assert.Null(FolderPickerStart.DirectoryOf(chosen));
    }
}
