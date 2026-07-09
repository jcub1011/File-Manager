using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace FileManager.UI.Tests;

/// <summary>Boots a headless Avalonia app for control-load smoke tests. Uses a bare Application with the
/// Fluent theme rather than <c>FileManager.UI.App</c> so no IPC/service composition runs during tests.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
