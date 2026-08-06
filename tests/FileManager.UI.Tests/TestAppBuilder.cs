using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace FileManager.UI.Tests;

/// <summary>Boots a headless Avalonia app for control-load smoke tests. Uses a bare Application rather than
/// <c>FileManager.UI.App</c> so no IPC/service composition runs during tests — that lives in
/// <c>OnFrameworkInitializationCompleted</c>, which this app does not implement.
///
/// <para><b>It does load the app's real resource dictionary and styles.</b> That is the whole point: a
/// StaticResource lookup must resolve here exactly as it does under the real App, or a smoke test is
/// checking a view whose resource references all silently produced null. This class used to CLAIM that in a
/// comment while merging only the monospaced font, and the gap shipped a crash — a dashed placeholder bound
/// a shape's <c>RadiusX</c> (a double) to <c>Radius.Card</c> (a CornerRadius). The key resolved to nothing
/// here, so no test ever attempted the conversion, and it threw an InvalidCastException the moment the real
/// window was opened. Merging Tokens means a type mismatch like that fails in a test instead.</para>
///
/// <para>Icon geometries are the one thing still absent: they are declared inline in App.axaml rather than
/// in a dictionary this can include, so <c>{StaticResource IconX}</c> still resolves to null here.
/// <c>ResourceKeyContractTests</c> covers those against the real App instead.</para></summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());

        // Merged BEFORE the shared styles, matching App.axaml's order — those styles reference these
        // brushes, and the tokens must outrank the FluentTheme defaults.
        Resources.MergedDictionaries.Add(new ResourceInclude(BaseUri)
        {
            Source = new Uri("avares://FileManager.UI/Themes/Tokens.axaml"),
        });

        // Included after FluentTheme so the app's class-based styles layer over the Fluent defaults, which
        // is the ordering App.axaml documents. Without them Classes="card" and friends do nothing at all,
        // so a smoke test could not see a style-driven layout even in principle.
        Styles.Add(new StyleInclude(BaseUri)
        {
            Source = new Uri("avares://FileManager.UI/Themes/Controls.axaml"),
        });

        // Declared inline in App.axaml rather than in Tokens, so it has to be mirrored by hand.
        Resources.Add("MonoFontFamily", new FontFamily("Consolas, Cascadia Mono, Menlo, monospace"));
    }

    private static Uri BaseUri => new("avares://FileManager.UI/");
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
