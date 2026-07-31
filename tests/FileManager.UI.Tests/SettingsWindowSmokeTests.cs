using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using FileManager.Contracts.Settings;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.ViewModels.Settings;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real SettingsWindow and ConfirmWindow so runtime-only XAML failures (the per-kind
/// setting DataTemplates, the navigation tree bindings, the ConfirmWindow layout) surface here, not in
/// the app.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class SettingsWindowSmokeTests(HeadlessSessionFixture headless)
{
    [Fact]
    public async Task Settings_window_loads_with_the_startup_mode_combo()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new()
            {
                GetSettingsResult = new GlobalSettings
                {
                    ServiceStartupMode = ServiceStartupMode.RunOnStartup,
                    ThemeMode = ThemeMode.Dark,
                },
            };
            SettingsViewModel vm = new(gateway, new FakeFolderPicker());
            await vm.LoadAsync();

            SettingsWindow window = new() { DataContext = vm };
            window.Show();

            Assert.Equal(ServiceStartupMode.RunOnStartup, vm.StartupMode.Value);
            Assert.Contains(vm.StartupMode.Options, o => Equals(o.Value, ServiceStartupMode.StartAndStopWithProgram));
            Assert.Equal(ThemeMode.Dark, vm.Theme.Value);
            Assert.Contains(vm.Theme.Options, o => Equals(o.Value, ThemeMode.System));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Navigating_to_a_setting_scrolls_it_into_view()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker());
            await vm.LoadAsync();

            SettingsWindow window = new() { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            ScrollViewer scroll = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("SettingsScroll"));
            Assert.Equal(0, scroll.Offset.Y);

            // The last category's setting sits well below the fold, so reaching it must move the scroller.
            SettingItemViewModel last = vm.Categories[^1].Items[^1];
            vm.SelectedNavNode = FindLeaf(vm.NavNodes, last);

            Assert.True(scroll.Offset.Y > 0, "selecting a setting below the fold should scroll the document");

            // The accent edge comes from a style-class binding, which fails silently if the syntax is
            // wrong — so assert the class actually landed on the navigated-to setting's anchor.
            Border anchor = window.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("settingCard") && ReferenceEquals(b.DataContext, last));
            Assert.Contains("selected", anchor.Classes);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_search_box_carries_its_magnifier()
    {
        // An absent InnerLeftContent is a silent failure — no binding error, just no icon — and it is the
        // only affordance marking the box as a search field. (Its Data stays null here: HeadlessTestApp
        // deliberately omits App.axaml's geometries. A mistyped key would throw under the real App.)
        await headless.Session.DispatchAsync(async () =>
        {
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker());
            await vm.LoadAsync();

            SettingsWindow window = new() { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            TextBox search = Assert.IsType<TextBox>(window.FindControl<TextBox>("SearchBox"));
            Assert.IsType<PathIcon>(search.InnerLeftContent);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task No_setting_is_clipped_at_the_narrowest_allowed_window()
    {
        // The document scrolls vertically only, so a setting whose editor cannot shrink gets cut off at
        // the right edge instead of scrolling into reach. Every editor has to give width back.
        await headless.Session.DispatchAsync(async () =>
        {
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker());
            await vm.LoadAsync();
            // Populate the override lists so their (widest) editors get measured too.
            vm.DriveOverrides.AddDriveTypeOverrideCommand.Execute(null);
            vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);

            // Width has to be set before Show: the headless platform ignores a resize afterwards.
            SettingsWindow window = new() { DataContext = vm, Width = 720 };
            window.Show();
            window.UpdateLayout();

            ScrollViewer scroll = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("SettingsScroll"));
            Assert.Equal(720, window.Bounds.Width);
            foreach (Border card in window.GetVisualDescendants().OfType<Border>()
                         .Where(b => b.Classes.Contains("settingCard")))
            {
                Point origin = Assert.IsType<Point>(card.TranslatePoint(default, scroll));
                double right = origin.X + card.Bounds.Width;
                Assert.True(
                    right <= scroll.Bounds.Width + 0.5,
                    $"\"{(card.DataContext as SettingItemViewModel)?.Title}\" reaches {right:F0}px, past the " +
                    $"{scroll.Bounds.Width:F0}px document width — it will be clipped.");
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Settings_stay_anchored_to_the_left_edge_on_a_wide_window()
    {
        // A MaxWidth on a Stretch-aligned setting centres it in the surplus width, so the content drifts
        // right as the window grows. The cap belongs to the document's star column instead, which leaves
        // the surplus empty and every setting flush left.
        await headless.Session.DispatchAsync(async () =>
        {
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker());
            await vm.LoadAsync();

            SettingsWindow window = new() { DataContext = vm, Width = 1800 };
            window.Show();
            window.UpdateLayout();

            ScrollViewer scroll = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("SettingsScroll"));
            const double inset = 16;   // the document Margin

            foreach (Border card in window.GetVisualDescendants().OfType<Border>()
                         .Where(b => b.Classes.Contains("settingCard")))
            {
                Point origin = Assert.IsType<Point>(card.TranslatePoint(default, scroll));
                Assert.Equal(inset, origin.X, 1);

                // Each description fills the card rather than floating in the middle of it.
                TextBlock description = card.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Classes.Contains("secondary"));
                Point textOrigin = Assert.IsType<Point>(description.TranslatePoint(default, card));
                Assert.True(
                    textOrigin.X < 20,
                    $"\"{(card.DataContext as SettingItemViewModel)?.Title}\" description starts {textOrigin.X:F0}px "
                    + "into its card — it is being centred, not stretched.");
            }
        }, CancellationToken.None);
    }

    private static SettingsNavNodeViewModel FindLeaf(
        IEnumerable<SettingsNavNodeViewModel> nodes, SettingItemViewModel target) =>
        TryFind(nodes, target) ?? throw new InvalidOperationException($"No navigation leaf for setting \"{target.Id}\".");

    private static SettingsNavNodeViewModel? TryFind(
        IEnumerable<SettingsNavNodeViewModel> nodes, SettingItemViewModel target)
    {
        foreach (SettingsNavNodeViewModel node in nodes)
        {
            if (ReferenceEquals(node.Setting, target))
                return node;
            SettingsNavNodeViewModel? found = TryFind(node.Children, target);
            if (found is not null)
                return found;
        }
        return null;
    }

    [Fact]
    public async Task The_specific_drive_row_renders_its_drive_picker()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), new FakeSystemDrives());
            await vm.LoadAsync();
            vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);

            SettingsWindow window = new() { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            // The picker's ItemsSource/ItemTemplate are compiled bindings inside a nested DataTemplate —
            // a mistake there fails at runtime, not at build, and leaves an empty combo behind.
            ComboBox picker = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => c.DataContext is SpecificDriveOverrideRowViewModel);
            Assert.Equal(2, picker.ItemCount);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task No_override_row_control_is_clipped_at_the_narrowest_allowed_window()
    {
        // Measures the CONTROLS, not their container. The existing card-level check cannot catch this and
        // neither could a row-level one: both the card and the row panel live in a width-capped star
        // column, so they are always exactly as wide as the viewport allows no matter how far the buttons
        // and boxes inside them run past the edge.
        await headless.Session.DispatchAsync(async () =>
        {
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), new FakeSystemDrives());
            await vm.LoadAsync();
            vm.DriveOverrides.AddDriveTypeOverrideCommand.Execute(null);
            vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
            vm.DriveOverrides.SpecificDriveOverrides[0].Auto = false;    // widest state: the spinner is shown
            vm.DriveOverrides.DriveTypeOverrides[0].Auto = false;

            SettingsWindow window = new() { DataContext = vm, Width = 720 };
            window.Show();
            window.UpdateLayout();

            ScrollViewer scroll = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("SettingsScroll"));
            foreach (Control control in window.GetVisualDescendants().OfType<Control>()
                         .Where(c => c is ComboBox or TextBox or CheckBox or NumericUpDown or Button)
                         .Where(c => c.DataContext is DriveTypeOverrideRowViewModel or SpecificDriveOverrideRowViewModel))
            {
                Point origin = Assert.IsType<Point>(control.TranslatePoint(default, scroll));
                double right = origin.X + control.Bounds.Width;
                Assert.True(
                    right <= scroll.Bounds.Width + 0.5,
                    $"A {control.GetType().Name} in an override row reaches {right:F0}px, past the "
                    + $"{scroll.Bounds.Width:F0}px document width — it will be clipped.");
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_override_row_still_fits_on_one_line_at_the_default_window_width()
    {
        // The flip side of the wrapping check: wrapping is the narrow-window fallback, not the normal
        // look. If a future control widens the row past the default width, every override becomes two
        // lines tall and this catches it.
        await headless.Session.DispatchAsync(async () =>
        {
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), new FakeSystemDrives());
            await vm.LoadAsync();
            vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
            vm.DriveOverrides.SpecificDriveOverrides[0].Auto = false;

            SettingsWindow window = new() { DataContext = vm };     // default 980 width
            window.Show();
            window.UpdateLayout();

            WrapPanel row = window.GetVisualDescendants().OfType<WrapPanel>()
                .Single(p => p.DataContext is SpecificDriveOverrideRowViewModel);
            Control[] children = [.. row.GetVisualChildren().OfType<Control>()];
            Assert.Equal(5, children.Length);

            // One line means the panel is no taller than its tallest child. Comparing each child's Y
            // would be wrong: they are centre-aligned, so shorter controls legitimately start a couple of
            // pixels down.
            double tallest = children.Max(c => c.Bounds.Height + c.Margin.Top + c.Margin.Bottom);
            Assert.True(
                row.Bounds.Height <= tallest + 1,
                $"The specific-drive row is {row.Bounds.Height:F0}px tall against a tallest control of "
                + $"{tallest:F0}px — it has wrapped onto a second line at the default window width.");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Ctrl_Z_in_a_text_box_undoes_the_setting_not_the_text_box()
    {
        // The window claims the gesture in the tunnel phase, deliberately superseding the TextBox's own
        // character-level undo, so undo means the same thing wherever focus happens to be.
        await headless.Session.DispatchAsync(async () =>
        {
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), new FakeSystemDrives());
            await vm.LoadAsync();
            string original = vm.ScratchDirectory.Value;

            SettingsWindow window = new() { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            TextBox box = window.GetVisualDescendants().OfType<TextBox>()
                .Single(t => ReferenceEquals(t.DataContext, vm.ScratchDirectory));
            box.Focus();
            box.Text = @"D:\spill";
            Assert.True(vm.IsDirty);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

            Assert.Equal(original, vm.ScratchDirectory.Value);
            Assert.False(vm.IsDirty);

            window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
            Assert.Equal(@"D:\spill", vm.ScratchDirectory.Value);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal(@"D:\spill", vm.ScratchDirectory.Value);   // Ctrl+Shift+Z redoes too
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_footer_close_button_names_the_consequence_when_there_are_unsaved_edits()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), new FakeSystemDrives());
            await vm.LoadAsync();

            SettingsWindow window = new() { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            Button close = window.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Content, "Close"));

            vm.Theme.Value = ThemeMode.Dark;
            window.UpdateLayout();
            Assert.Equal("Discard changes", close.Content);

            vm.History.UndoCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal("Close", close.Content);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Confirm_window_loads_with_its_message()
    {
        await headless.Session.Dispatch(() =>
        {
            ConfirmWindow window = new("2 job(s) are still running. Close anyway?");
            window.Show();
        }, CancellationToken.None);
    }
}
