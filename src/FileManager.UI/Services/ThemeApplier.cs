using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using FileManager.Contracts.Settings;

namespace FileManager.UI.Services;

/// <summary>Applies a persisted <see cref="ThemeMode"/> to the running application by setting
/// <see cref="Application.RequestedThemeVariant"/>. This is the single Avalonia touch-point for
/// theming, so the viewmodels stay free of framework theming APIs. The brush palette
/// (Themes/Tokens.axaml) fully defines both variants via <c>{DynamicResource}</c>, so changing the
/// variant restyles the whole app live.</summary>
public static class ThemeApplier
{
    /// <summary>Maps <paramref name="mode"/> to a theme variant and applies it. No-ops when there is no
    /// running application (e.g. plain unit tests). Marshals onto the UI thread so it is safe to call
    /// from an awaited continuation on any thread.</summary>
    public static void Apply(ThemeMode mode)
    {
        if (Application.Current is not { } app)
            return;

        ThemeVariant variant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,   // System: follow the OS preference
        };

        if (Dispatcher.UIThread.CheckAccess())
            app.RequestedThemeVariant = variant;
        else
            Dispatcher.UIThread.Post(() => app.RequestedThemeVariant = variant);
    }
}
