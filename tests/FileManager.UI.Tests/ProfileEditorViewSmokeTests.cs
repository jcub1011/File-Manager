using Avalonia.Controls;
using Avalonia.VisualTree;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real ProfileEditorView so runtime-only XAML failures surface here rather than in the
/// app: a typed <c>EnumTitleConverters</c> member that does not exist, an <c>x:DataType</c> that does not
/// match its ItemsSource, or a policy Grid whose RowDefinitions were not grown when a row was added (which
/// silently stacks two controls in the last row instead of failing).</summary>
[Collection(HeadlessCollection.Name)]
public sealed class ProfileEditorViewSmokeTests(HeadlessSessionFixture headless)
{
    [Fact]
    public async Task Profile_editor_loads_with_every_policy_row_laid_out()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            ProfileEditorViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker());
            vm.LoadNew();

            ProfileEditorView view = new() { DataContext = vm };
            Window window = new() { Content = view };
            window.Show();
            window.UpdateLayout();

            // Every policy control resolved its bindings and converters (a broken x:Static converter
            // reference throws during load, before this point).
            Assert.Equal(LargeFileIdentity.FullHash, vm.LargeFileIdentity);
            Assert.Equal(4, vm.LargeFileIdentityOptions.Count);

            // The threshold row is hidden under the default and appears when a cheap method is chosen.
            Assert.False(vm.ShowLargeFileIdentityThreshold);
            vm.LargeFileIdentity = LargeFileIdentity.SampledHash;
            window.UpdateLayout();
            Assert.True(vm.ShowLargeFileIdentityThreshold);

            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_policy_grid_has_a_row_for_every_control_it_holds()
    {
        // Guards the failure mode a RowDefinitions/Grid.Row mismatch produces: Avalonia clamps an
        // out-of-range Grid.Row into the last row, so two unrelated controls overlap and nothing throws.
        // Comparing the highest Grid.Row actually used against the row count catches it.
        await headless.Session.DispatchAsync(async () =>
        {
            ProfileEditorViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker());
            vm.LoadNew();
            vm.LargeFileIdentity = LargeFileIdentity.SampledHash;   // reveal the conditional rows

            ProfileEditorView view = new() { DataContext = vm };
            Window window = new() { Content = view };
            window.Show();
            window.UpdateLayout();

            int examined = 0;
            int deepestRowSeen = -1;
            foreach (Grid grid in view.GetVisualDescendants().OfType<Grid>())
            {
                if (grid.RowDefinitions.Count == 0)
                    continue;
                examined++;
                int highest = grid.Children.Count == 0 ? -1 : grid.Children.Max(Grid.GetRow);
                deepestRowSeen = Math.Max(deepestRowSeen, highest);
                Assert.True(highest < grid.RowDefinitions.Count,
                    $"a Grid uses Grid.Row={highest} but declares only {grid.RowDefinitions.Count} rows — "
                    + "controls are silently overlapping in the last row");
            }

            // Without this the test passes vacuously whenever the visual tree is not realized, which would
            // make it useless exactly when it is needed.
            Assert.True(examined > 0, "no row-based Grid was realized — the check inspected nothing");
            Assert.True(deepestRowSeen >= 8,
                $"expected to reach the policies grid (9 rows, deepest Grid.Row 8) but the deepest row seen "
                + $"was {deepestRowSeen} — the check is not looking at the rows it was written for");

            await Task.CompletedTask;
        }, CancellationToken.None);
    }
}
