using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace FileManager.UI.Controls;

/// <summary>A compact status indicator: a colour-tinted glyph with an optional count/label beside it.
/// Replaces the dry-run view's filled status pills — the icon carries the meaning (pair it with a
/// <c>ToolTip.Tip</c> for the word), and the optional <see cref="Text"/> carries a count. When
/// <see cref="Text"/> is empty the control is icon-only. Templated in Themes/Controls.axaml.</summary>
public sealed class StatusCount : TemplatedControl
{
    /// <summary>The glyph geometry (from the shared App.axaml icon set).</summary>
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<StatusCount, Geometry?>(nameof(Icon));

    /// <summary>The icon's colour — a semantic brush token so it tracks the theme/palette.</summary>
    public static readonly StyledProperty<IBrush?> GlyphProperty =
        AvaloniaProperty.Register<StatusCount, IBrush?>(nameof(Glyph));

    /// <summary>Optional text beside the icon (a count). Empty/null → icon-only.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusCount, string?>(nameof(Text));

    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public IBrush? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
}
