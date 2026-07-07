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
    public async Task Confirmed_delete_calls_the_gateway_and_refreshes()
    {
        var (list, gateway) = NewList();
        await list.RefreshAsync();
        list.CanNavigate = () => true;

        list.RequestDelete(list.Profiles[1]);
        Assert.NotNull(list.PendingDelete);
        await list.ConfirmDeleteAsync();

        Assert.Equal([IdB], gateway.DeleteCalls);
        Assert.Null(list.PendingDelete);
    }

    [Fact]
    public async Task Cancelled_delete_touches_nothing()
    {
        var (list, gateway) = NewList();
        await list.RefreshAsync();

        list.RequestDelete(list.Profiles[0]);
        list.CancelDelete();
        await list.ConfirmDeleteAsync();       // nothing pending → no-op

        Assert.Empty(gateway.DeleteCalls);
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
