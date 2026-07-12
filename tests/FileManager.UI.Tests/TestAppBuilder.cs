using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace FileManager.UI.Tests;

/// <summary>Boots a headless Avalonia app for control-load smoke tests. Uses a bare Application with the
/// Fluent theme rather than <c>FileManager.UI.App</c> so no IPC/service composition runs during tests.
/// Mirrors the app-level resources the views need at load time (e.g. the monospaced path font) so
/// StaticResource lookups resolve exactly as they do under the real App.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.Add("MonoFontFamily", new FontFamily("Consolas, Cascadia Mono, Menlo, monospace"));
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
