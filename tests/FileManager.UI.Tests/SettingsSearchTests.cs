using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.ViewModels.Settings;

namespace FileManager.UI.Tests;

/// <summary>The Settings window's search box and navigation tree, both generated from the settings
/// catalog. These are pure view-model tests — no headless session needed.</summary>
public sealed class SettingsSearchTests
{
    private static SettingsViewModel New() =>
        new(new FakeIpcGateway(), new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());

    private static SettingItemViewModel Item(SettingsViewModel vm, string id) =>
        vm.Categories.SelectMany(c => c.Items).Single(i => i.Id == id);

    private static SettingsNavNodeViewModel Leaf(SettingsViewModel vm, string id)
    {
        SettingItemViewModel target = Item(vm, id);
        return Flatten(vm.NavNodes).Single(n => ReferenceEquals(n.Setting, target));
    }

    private static IEnumerable<SettingsNavNodeViewModel> Flatten(IEnumerable<SettingsNavNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    [Fact]
    public void Every_setting_is_registered_with_a_unique_id_and_real_prose()
    {
        // The guard rail for future additions: search and navigation are only as good as this metadata.
        SettingsViewModel vm = New();
        List<SettingItemViewModel> items = [.. vm.Categories.SelectMany(c => c.Items)];

        Assert.NotEmpty(items);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (SettingItemViewModel item in items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.False(string.IsNullOrWhiteSpace(item.Description));
            Assert.NotNull(item.Category);
        }
    }

    [Fact]
    public void Every_setting_has_a_navigation_leaf()
    {
        SettingsViewModel vm = New();

        IEnumerable<SettingItemViewModel> leaves = Flatten(vm.NavNodes).Select(n => n.Setting).OfType<SettingItemViewModel>();

        Assert.Equal(
            vm.Categories.SelectMany(c => c.Items).OrderBy(i => i.Id, StringComparer.Ordinal),
            leaves.OrderBy(i => i.Id, StringComparer.Ordinal));
    }

    [Fact]
    public void A_nested_category_hangs_off_its_parent_branch()
    {
        SettingsViewModel vm = New();

        SettingsNavNodeViewModel performance = vm.NavNodes.Single(n => n.Category?.Id == "performance");

        Assert.Contains(performance.Children, n => n.Category?.Id == "performance.advanced");
        Assert.DoesNotContain(vm.NavNodes, n => n.Category?.Id == "performance.advanced");
    }

    [Theory]
    [InlineData("theme", "application.theme")]              // the title
    [InlineData("dark", "application.theme")]               // an option label, absent from the prose
    [InlineData("run on startup", "startup.serviceMode")]  // an option label spanning several words
    [InlineData("login", "startup.serviceMode")]           // a keyword, absent from the prose
    [InlineData("hashing", "performance.maxHashThreads")]  // a word only the description has
    [InlineData("volume key", "performance.driveOverrides")]
    [InlineData("service executable", "application.serviceExePath")]
    [InlineData("not found", "application.serviceExePath")]   // what a stuck user would actually type
    public void A_query_matches_the_setting_it_should(string query, string expectedId)
    {
        SettingsViewModel vm = New();

        vm.SearchText = query;

        Assert.True(vm.HasVisibleSettings);
        Assert.True(Item(vm, expectedId).IsVisible, $"\"{query}\" should match {expectedId}");
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        SettingsViewModel vm = New();

        vm.SearchText = "DaRk";

        Assert.True(Item(vm, "application.theme").IsVisible);
    }

    [Fact]
    public void Extra_terms_narrow_the_result_rather_than_widening_it()
    {
        SettingsViewModel vm = New();

        vm.SearchText = "threads";
        int broad = vm.Categories.SelectMany(c => c.Items).Count(i => i.IsVisible);

        vm.SearchText = "threads hash";
        int narrow = vm.Categories.SelectMany(c => c.Items).Count(i => i.IsVisible);

        Assert.True(broad > narrow, $"expected fewer matches for the second query ({broad} then {narrow})");
        Assert.True(Item(vm, "performance.maxHashThreads").IsVisible);
        Assert.False(Item(vm, "performance.maxScanThreads").IsVisible);
    }

    [Fact]
    public void A_category_with_no_matches_disappears_along_with_its_tree_node()
    {
        SettingsViewModel vm = New();

        vm.SearchText = "theme";

        Assert.True(vm.Categories.Single(c => c.Id == "application").IsVisible);
        Assert.False(vm.Categories.Single(c => c.Id == "storage").IsVisible);
        Assert.False(vm.NavNodes.Single(n => n.Category?.Id == "storage").IsVisible);
    }

    [Fact]
    public void A_parent_branch_survives_when_only_a_nested_category_matches()
    {
        SettingsViewModel vm = New();

        vm.SearchText = "volume key";   // matches only Performance → Per-drive overrides

        SettingsNavNodeViewModel performance = vm.NavNodes.Single(n => n.Category?.Id == "performance");
        Assert.False(performance.Category!.IsVisible);   // none of its own settings matched
        Assert.True(performance.IsVisible);              // ...but the branch must stay to reach the child
        Assert.True(performance.Children.Single(n => n.Category?.Id == "performance.advanced").IsVisible);
    }

    [Fact]
    public void A_query_that_matches_nothing_reports_an_empty_result()
    {
        SettingsViewModel vm = New();

        vm.SearchText = "zzzznotasetting";

        Assert.False(vm.HasVisibleSettings);
        Assert.All(vm.Categories, c => Assert.False(c.IsVisible));
    }

    [Fact]
    public void Clearing_the_search_restores_everything()
    {
        SettingsViewModel vm = New();
        vm.SearchText = "zzzznotasetting";

        vm.ClearSearchCommand.Execute(null);

        Assert.True(vm.HasVisibleSettings);
        Assert.All(vm.Categories, c => Assert.True(c.IsVisible));
        Assert.All(vm.Categories.SelectMany(c => c.Items), i => Assert.True(i.IsVisible));
        Assert.All(Flatten(vm.NavNodes), n => Assert.True(n.IsVisible));
    }

    [Fact]
    public void Selecting_a_leaf_asks_the_view_to_scroll_to_that_setting()
    {
        SettingsViewModel vm = New();
        List<SettingItemViewModel> requested = [];
        vm.ScrollToRequested += (_, item) => requested.Add(item);

        vm.SelectedNavNode = Leaf(vm, "storage.scratchDirectory");

        SettingItemViewModel expected = Item(vm, "storage.scratchDirectory");
        Assert.Equal(expected, Assert.Single(requested));
        Assert.Same(expected, vm.SelectedSetting);
        Assert.True(expected.IsSelected);
    }

    [Fact]
    public void Selecting_a_category_asks_the_view_to_scroll_to_that_categorys_header()
    {
        SettingsViewModel vm = New();
        List<SettingsCategoryViewModel> requested = [];
        vm.ScrollToCategoryRequested += (_, category) => requested.Add(category);

        SettingsNavNodeViewModel node = vm.NavNodes.Single(n => n.Category?.Id == "performance");
        vm.SelectedNavNode = node;

        Assert.Same(node.Category, Assert.Single(requested));
    }

    [Fact]
    public void Selecting_a_category_leaves_no_setting_highlighted()
    {
        SettingsViewModel vm = New();
        vm.SelectedNavNode = Leaf(vm, "application.theme");   // give it a setting to clear

        vm.SelectedNavNode = vm.NavNodes.Single(n => n.Category?.Id == "performance");

        Assert.Null(vm.SelectedSetting);
        Assert.False(Item(vm, "application.theme").IsSelected);
    }

    [Fact]
    public void Only_one_setting_is_highlighted_at_a_time()
    {
        SettingsViewModel vm = New();

        vm.SelectedNavNode = Leaf(vm, "application.theme");
        vm.SelectedNavNode = Leaf(vm, "storage.profilesDirectory");

        Assert.False(Item(vm, "application.theme").IsSelected);
        Assert.True(Item(vm, "storage.profilesDirectory").IsSelected);
    }
}
