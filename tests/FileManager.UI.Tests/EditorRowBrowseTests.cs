using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.ViewModels.Editor;

namespace FileManager.UI.Tests;

/// <summary>Browse on a source/target row opens where the row already points, and on an empty row
/// where the row ABOVE points — so filling in a second source starts beside the first instead of at
/// Downloads. These pin what the row hands the picker; turning that into a directory (parent-of,
/// existence, Downloads fallback) is <see cref="FolderPickerStartTests"/>'s job.</summary>
public sealed class EditorRowBrowseTests
{
    private static (ProfileEditorViewModel Editor, FakeFolderPicker Picker) NewEditor()
    {
        FakeFolderPicker picker = new();
        return (new ProfileEditorViewModel(new FakeIpcGateway(), picker), picker);
    }

    [Fact]
    public async Task A_filled_row_browses_from_its_own_folder()
    {
        var (editor, picker) = NewEditor();
        editor.LoadNew();
        editor.Sources[0].Path = @"C:\in\photos";

        await editor.Sources[0].BrowseAsync();

        Assert.Equal(@"C:\in\photos", picker.LastStartNear);
    }

    [Fact]
    public async Task An_empty_second_row_browses_from_the_row_above()
    {
        var (editor, picker) = NewEditor();
        editor.LoadNew();
        editor.Sources[0].Path = @"C:\in\photos";
        editor.AddSource();

        await editor.Sources[1].BrowseAsync();

        Assert.Equal(@"C:\in\photos", picker.LastStartNear);
    }

    [Fact]
    public async Task An_empty_first_row_has_nothing_to_start_from()
    {
        var (editor, picker) = NewEditor();
        editor.LoadNew();

        await editor.Sources[0].BrowseAsync();

        Assert.Null(picker.LastStartNear);
    }

    [Fact]
    public async Task An_empty_row_above_does_not_dead_end_the_chain()
    {
        var (editor, picker) = NewEditor();
        editor.LoadNew();
        editor.Sources[0].Path = @"C:\in\photos";
        editor.AddSource();       // left blank
        editor.AddSource();

        await editor.Sources[2].BrowseAsync();

        Assert.Equal(@"C:\in\photos", picker.LastStartNear);
    }

    [Fact]
    public async Task Removing_the_row_above_re_points_the_chain()
    {
        // The lookup runs at click time against the live collection, so add/remove needs no re-wiring.
        var (editor, picker) = NewEditor();
        editor.LoadNew();
        editor.Sources[0].Path = @"C:\in\photos";
        editor.AddSource();
        editor.Sources[1].Path = @"C:\in\video";
        editor.AddSource();
        editor.RemoveSource(editor.Sources[1]);

        await editor.Sources[1].BrowseAsync();

        Assert.Equal(@"C:\in\photos", picker.LastStartNear);
    }

    [Fact]
    public async Task Target_rows_follow_the_same_rule()
    {
        var (editor, picker) = NewEditor();
        editor.LoadNew();
        editor.Targets[0].Path = @"D:\out\photos";
        editor.AddTarget();

        await editor.Targets[1].BrowseAsync();

        Assert.Equal(@"D:\out\photos", picker.LastStartNear);
    }

    [Fact]
    public async Task A_target_row_never_reads_a_source_row()
    {
        var (editor, picker) = NewEditor();
        editor.LoadNew();
        editor.Sources[0].Path = @"C:\in\photos";

        await editor.Targets[0].BrowseAsync();

        Assert.Null(picker.LastStartNear);
    }
}
