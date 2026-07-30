using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

public sealed class ProfileListViewModelTests
{
    private static readonly Guid IdA = Guid.NewGuid();
    private static readonly Guid IdB = Guid.NewGuid();

    private static (ProfileListViewModel List, FakeIpcGateway Gateway) NewList()
    {
        FakeIpcGateway gateway = new()
        {
            ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success(
            [
                new ProfileSummary(IdA, "Alpha", true, "Manual"),
                new ProfileSummary(IdB, "Beta", false, "Manual"),
            ]),
        };
        return (new ProfileListViewModel(gateway), gateway);
    }

    [Fact]
    public async Task Refresh_populates_sorted_rows()
    {
        var (list, _) = NewList();
        await list.RefreshAsync();

        Assert.Equal(2, list.Profiles.Count);
        Assert.Equal("Alpha", list.Profiles[0].Name);
        Assert.Null(list.ErrorMessage);
    }

    [Fact]
    public async Task HasProfiles_tracks_whether_any_profiles_exist()
    {
        var (list, gateway) = NewList();
        Assert.False(list.HasProfiles);            // nothing loaded yet

        await list.RefreshAsync();
        Assert.True(list.HasProfiles);             // two profiles present

        gateway.ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success([]);
        await list.RefreshAsync();
        Assert.False(list.HasProfiles);            // now empty
    }

    [Fact]
    public async Task Refresh_failure_surfaces_and_keeps_old_rows()
    {
        var (list, gateway) = NewList();
        await list.RefreshAsync();
        gateway.ListResult = new IpcError("SERVICE_UNAVAILABLE", "pipe down");

        await list.RefreshAsync();

        Assert.NotNull(list.ErrorMessage);
        Assert.Equal(2, list.Profiles.Count);
    }

    [Fact]
    public async Task Blocked_navigation_reverts_the_selection()
    {
        var (list, _) = NewList();
        await list.RefreshAsync();
        List<ProfileListItem?> committed = [];
        bool blocked = false;
        list.SelectionCommitted = committed.Add;
        list.NavigationBlocked = () => blocked = true;

        list.CanNavigate = () => true;
        list.SelectedProfile = list.Profiles[0];
        Assert.Single(committed);

        list.CanNavigate = () => false;        // dirty editor
        list.SelectedProfile = list.Profiles[1];

        Assert.True(blocked);
        Assert.Equal(list.Profiles[0], list.SelectedProfile);
        Assert.Single(committed);              // no second commit
    }

    [Fact]
    public async Task Delete_calls_the_gateway_and_refreshes()
    {
        // The confirmation is the shell's (a modal); by the time DeleteAsync runs the user said yes.
        var (list, gateway) = NewList();
        await list.RefreshAsync();
        list.CanNavigate = () => true;

        await list.DeleteAsync(list.Profiles[1]);

        Assert.Equal([IdB], gateway.DeleteCalls);
        Assert.Null(list.ErrorMessage);
    }

    [Fact]
    public async Task Deleting_the_open_profile_clears_the_selection()
    {
        var (list, gateway) = NewList();
        await list.RefreshAsync();
        list.CanNavigate = () => true;
        list.SelectedProfile = list.Profiles[0];

        await list.DeleteAsync(list.Profiles[0]);

        Assert.Equal([IdA], gateway.DeleteCalls);
        Assert.Null(list.SelectedProfile);
    }

    [Fact]
    public async Task Search_filters_rows_by_name_case_insensitively()
    {
        var (list, _) = NewList();
        list.SearchDebounce = TimeSpan.Zero;
        await list.RefreshAsync();

        list.SearchText = "bet";
        await list.PendingSearch!;

        Assert.Single(list.FilteredProfiles);
        Assert.Equal("Beta", list.FilteredProfiles[0].Name);
    }

    [Fact]
    public async Task Clearing_search_restores_all_rows()
    {
        var (list, _) = NewList();
        list.SearchDebounce = TimeSpan.Zero;
        await list.RefreshAsync();

        list.SearchText = "bet";
        await list.PendingSearch!;
        list.SearchText = "";
        await list.PendingSearch!;

        Assert.Equal(2, list.FilteredProfiles.Count);
    }

    [Fact]
    public async Task Selected_row_stays_visible_even_when_the_search_excludes_it()
    {
        var (list, _) = NewList();
        list.SearchDebounce = TimeSpan.Zero;
        await list.RefreshAsync();
        list.SelectedProfile = list.Profiles.First(p => p.Name == "Alpha");

        list.SearchText = "zzz";                       // matches nothing
        await list.PendingSearch!;

        Assert.Contains(list.FilteredProfiles, p => p.Name == "Alpha");   // force-included
        Assert.DoesNotContain(list.FilteredProfiles, p => p.Name == "Beta");
        Assert.Equal("Alpha", list.SelectedProfile?.Name);                // editor keeps its profile
    }

    [Fact]
    public async Task Refresh_preserves_the_active_search_filter()
    {
        var (list, _) = NewList();
        list.SearchDebounce = TimeSpan.Zero;
        await list.RefreshAsync();
        list.SearchText = "alpha";
        await list.PendingSearch!;

        await list.RefreshAsync();

        Assert.Single(list.FilteredProfiles);
        Assert.Equal("Alpha", list.FilteredProfiles[0].Name);
    }

    [Fact]
    public async Task RefreshAndSelect_reselects_without_firing_navigation()
    {
        var (list, _) = NewList();
        int commits = 0;
        list.SelectionCommitted = _ => commits++;

        await list.RefreshAndSelectAsync(IdB);

        Assert.Equal(IdB, list.SelectedProfile?.ProfileId);
        Assert.Equal(0, commits);
    }
}
