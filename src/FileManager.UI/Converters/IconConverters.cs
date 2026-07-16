using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FileManager.UI.Converters;

/// <summary>Resolves a resource key (string) to an app resource, for data-templated bindings that
/// can't use <c>{StaticResource}</c> with a bound key. The dry-run tree pills carry their glyph and
/// colour as resource keys (e.g. "IconTrash", "Brush.Danger") chosen per node kind by the view model,
/// so the icon set and palette stay defined once (App.axaml / Tokens.axaml) and the view model needs
/// no Avalonia/resource dependency (keeping the pure view-model tests app-free). The lookups run at
/// bind time — an <see cref="Application"/> is always live by the time a report renders; in a headless
/// test with no app they simply return null and the icon draws nothing.</summary>
public static class IconConverters
{
    public static readonly FuncValueConverter<string?, Geometry?> Geometry =
        new(key => Resolve(key) as Geometry);

    public static readonly FuncValueConverter<string?, IBrush?> Brush =
        new(key => Resolve(key) as IBrush);

    private static object? Resolve(string? key) =>
        key is not null && Application.Current is { } app && app.TryGetResource(key, null, out object? value)
            ? value
            : null;
}
