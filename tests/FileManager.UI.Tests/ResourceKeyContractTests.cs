using Avalonia.Controls;
using Avalonia.Media;

namespace FileManager.UI.Tests;

/// <summary>The view models hand the views resource-key STRINGS (resolved at bind time through
/// IconConverters), so a typo cannot fail a compile and renders as a silently missing glyph. These
/// load the real App.axaml resource dictionary and assert every key a view model can emit exists.
/// <para>Only <c>Initialize()</c> is called — that is just the XAML load; the IPC/service composition
/// lives in OnFrameworkInitializationCompleted and never runs here.</para></summary>
[Collection(HeadlessCollection.Name)]
public sealed class ResourceKeyContractTests(HeadlessSessionFixture headless)
{
    /// <summary>Every glyph key <see cref="ViewModels.ActivityRow.StatusIconKey"/> can return, plus the
    /// two the pause toggle switches between, plus the dry-run keys.</summary>
    public static TheoryData<string> IconKeys() =>
    [
        "IconOverwrite", "IconCheckmark", "IconSkip", "IconWarning", "IconQuestion",
        "IconPause", "IconPlay",
        "IconRefresh", "IconClose", "IconDocument",
        // The DestinationRowKind vocabulary, which the dry-run view models write out across seven
        // separate sites (StatusIconKey, TreeSpecs, BuildStatusFilters, ...). This list previously
        // covered ActivityRow's keys only, so a typo in any of those sites shipped as a blank glyph.
        "IconUntouched", "IconAdd", "IconTrash",
        // ...and the source-row target-op keys from PrimaryKindIconKey.
        "IconRename",
    ];

    /// <summary>Every brush key <see cref="ViewModels.ActivityRow.StatusColorKey"/> can return, plus the
    /// dry-run status/kind brushes.</summary>
    public static TheoryData<string> BrushKeys() =>
    [
        "Brush.Info", "Brush.Success", "Brush.Muted", "Brush.Danger",
        "Brush.StatusBar.Foreground",
        // Emitted by DryRunDestinationEntry.StatusColorKey / TreeSpecs / BuildStatusFilters and by
        // DryRunFileRow.PrimaryKindColorKey, none of which were covered here.
        "Brush.Warning",
    ];

    [Theory]
    [MemberData(nameof(IconKeys))]
    public async Task Every_status_icon_key_resolves_to_a_geometry(string key)
    {
        await headless.Session.Dispatch(() =>
        {
            App app = new();
            app.Initialize();

            Assert.True(app.Resources.TryGetResource(key, null, out object? value),
                $"App.axaml has no resource named \"{key}\" — a view model emits it as an icon key.");
            Assert.IsAssignableFrom<Geometry>(value);
        }, CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(BrushKeys))]
    public async Task Every_status_brush_key_resolves(string key)
    {
        await headless.Session.Dispatch(() =>
        {
            App app = new();
            app.Initialize();

            // Brushes live in the merged theme dictionaries and are theme-variant scoped, so ask for
            // an explicit variant rather than the null (current) one.
            Assert.True(
                app.Resources.TryGetResource(key, Avalonia.Styling.ThemeVariant.Dark, out object? dark)
                || app.Resources.TryGetResource(key, Avalonia.Styling.ThemeVariant.Light, out dark),
                $"App.axaml has no resource named \"{key}\" — a view model emits it as a brush key.");
            Assert.NotNull(dark);
        }, CancellationToken.None);
    }
}
