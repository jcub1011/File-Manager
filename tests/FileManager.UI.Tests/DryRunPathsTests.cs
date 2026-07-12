using FileManager.UI.ViewModels;
using Xunit;

namespace FileManager.UI.Tests;

public sealed class DryRunPathsTests
{
    [Fact]
    public void Single_root_returns_itself_normalized()
    {
        Assert.Equal(@"C:\src", DryRunPaths.CommonRoot([@"c:\src\"]));
    }

    [Fact]
    public void Empty_input_returns_null()
    {
        Assert.Null(DryRunPaths.CommonRoot([]));
        Assert.Null(DryRunPaths.CommonRoot([null, "", "   "]));
    }

    [Fact]
    public void Common_parent_of_sibling_roots()
    {
        Assert.Equal(@"C:\proj", DryRunPaths.CommonRoot([@"C:\proj\a", @"C:\proj\b"]));
    }

    [Fact]
    public void Ancestor_and_descendant_reduce_to_the_ancestor()
    {
        Assert.Equal(@"C:\proj", DryRunPaths.CommonRoot([@"C:\proj", @"C:\proj\sub\deep"]));
    }

    [Fact]
    public void Same_drive_but_no_shared_folder_returns_the_drive_root()
    {
        Assert.Equal(@"C:\", DryRunPaths.CommonRoot([@"C:\a", @"C:\b"]));
    }

    [Fact]
    public void Different_drives_return_null()
    {
        Assert.Null(DryRunPaths.CommonRoot([@"C:\a", @"D:\a"]));
    }

    [Fact]
    public void Matching_is_case_insensitive_and_slash_tolerant()
    {
        Assert.Equal(@"C:\Proj", DryRunPaths.CommonRoot([@"C:/Proj/a", @"c:\proj\b"]));
    }

    [Fact]
    public void Blank_entries_are_ignored()
    {
        Assert.Equal(@"C:\a", DryRunPaths.CommonRoot([null, "", @"C:\a"]));
    }

    [Fact]
    public void Unc_shares_share_the_common_root_when_the_same_share()
    {
        Assert.Equal(@"\\srv\share", DryRunPaths.CommonRoot([@"\\srv\share\a", @"\\srv\share\b"]));
    }

    [Fact]
    public void Unc_different_share_or_server_returns_null()
    {
        Assert.Null(DryRunPaths.CommonRoot([@"\\srv\share1\a", @"\\srv\share2\b"]));
        Assert.Null(DryRunPaths.CommonRoot([@"\\srvA\share\a", @"\\srvB\share\b"]));
    }

    [Fact]
    public void SplitForDisplay_makes_the_parent_relative_to_the_common_root()
    {
        (string fileName, string parent) = DryRunPaths.SplitForDisplay(@"C:\src\a\b.txt", @"C:\src");
        Assert.Equal("b.txt", fileName);
        Assert.Equal(@"a\", parent);
    }

    [Fact]
    public void SplitForDisplay_file_directly_in_root_has_empty_parent()
    {
        (_, string parent) = DryRunPaths.SplitForDisplay(@"C:\src\b.txt", @"C:\src");
        Assert.Equal("", parent);
    }

    [Fact]
    public void SplitForDisplay_without_common_root_shows_the_absolute_directory()
    {
        (_, string parent) = DryRunPaths.SplitForDisplay(@"C:\src\a\b.txt", null);
        Assert.Equal(@"C:\src\a\", parent);
    }

    [Fact]
    public void SplitForDisplay_falls_back_to_absolute_when_path_is_outside_the_root()
    {
        (_, string parent) = DryRunPaths.SplitForDisplay(@"D:\x\y.txt", @"C:\src");
        Assert.Equal(@"D:\x\", parent);
    }

    [Fact]
    public void RelativeForTree_strips_the_common_root()
    {
        Assert.Equal(@"a\b.txt", DryRunPaths.RelativeForTree(@"C:\src\a\b.txt", @"C:\src"));
        Assert.Equal(@"C:\src\a\b.txt", DryRunPaths.RelativeForTree(@"C:\src\a\b.txt", null));
    }
}
