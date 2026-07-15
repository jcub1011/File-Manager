using Avalonia;
using Avalonia.Styling;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;

namespace FileManager.UI.Tests;

/// <summary>Verifies the one runtime effect of the theme selector: <see cref="ThemeApplier"/> maps a
/// <see cref="ThemeMode"/> onto the running application's <see cref="Application.RequestedThemeVariant"/>.
/// Runs inside the headless session so <see cref="Application.Current"/> is a real app.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class ThemeApplierTests(HeadlessSessionFixture headless)
{
    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.System)]
    public async Task Apply_sets_the_requested_theme_variant(ThemeMode mode)
    {
        await headless.Session.Dispatch(() =>
        {
            ThemeApplier.Apply(mode);

            ThemeVariant expected = mode switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            Assert.Equal(expected, Application.Current!.RequestedThemeVariant);
        }, CancellationToken.None);
    }
}
