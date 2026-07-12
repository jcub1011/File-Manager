using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Serilog;

namespace FileManager.UI.Controls;

/// <summary>A single-line text control that shortens its text to fit the width it is arranged into,
/// using a monospaced font so the fit is a simple character count. Rendering directly (rather than
/// mutating a <see cref="TextBlock"/>'s <c>Text</c> during layout) keeps truncation out of the
/// measure/arrange pass, so there is no re-entrancy: <see cref="Render"/> reads the final
/// <see cref="Visual.Bounds"/> and only ever draws.</summary>
public sealed class PathText : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<PathText, string?>(nameof(Text));

    public static readonly StyledProperty<PathTruncationMode> ModeProperty =
        AvaloniaProperty.Register<PathText, PathTruncationMode>(nameof(Mode), PathTruncationMode.MiddleEllipsis);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<PathText>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<PathText>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<PathText>();

    static PathText()
    {
        // Text/Mode/Foreground only change what is drawn. Font changes also change the line height and
        // the per-character advance, so they invalidate measure as well.
        AffectsRender<PathText>(TextProperty, ModeProperty, ForegroundProperty);
        AffectsMeasure<PathText>(FontFamilyProperty, FontSizeProperty);
        AffectsRender<PathText>(FontFamilyProperty, FontSizeProperty);
    }

    // Clip so a mis-measured glyph (e.g. a non-monospace fallback face) can never bleed past the
    // control's arranged width into the adjacent pills.
    public PathText() => ClipToBounds = true;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A width change re-arranges (which repaints), but repaint explicitly to also cover the
        // same-width-different-content case cheaply (InvalidateVisual only schedules a draw).
        if (change.Property == BoundsProperty)
            InvalidateVisual();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public PathTruncationMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    // The control never asks for width (asking for the full text width would defeat truncation and
    // could ping-pong measure); it takes whatever its column gives and truncates to it. Height is the
    // font's line height, which is width-independent.
    protected override Size MeasureOverride(Size availableSize) => new(0, Measure("0").Height);

    public override void Render(DrawingContext context)
    {
        try
        {
            string text = Text ?? "";
            if (text.Length == 0)
                return;

            double width = Bounds.Width;
            if (width <= 0)
                return;

            double advance = Measure("0").Width;
            int maxChars = advance > 0 ? (int)Math.Floor(width / advance) : text.Length;

            string display = maxChars >= text.Length
                ? text
                : Mode == PathTruncationMode.EndPreservingExtension
                    ? PathTruncation.TruncateEndPreservingExtension(text, maxChars)
                    : PathTruncation.TruncateMiddle(text, maxChars);

            context.DrawText(Build(display), new Point(0, 0));
        }
        catch (Exception ex)
        {
            // Last resort: a truncation/render fault becomes a logged warning and a best-effort draw
            // of the raw text rather than a crashed render pass.
            Log.Warning(ex, "PathText failed to render {Text}", Text);
            try { context.DrawText(Build(Text ?? ""), new Point(0, 0)); }
            catch { /* nothing more to do; leave the row blank */ }
        }
    }

    private FormattedText Measure(string sample) => Build(sample);

    private FormattedText Build(string text) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(FontFamily),
        FontSize > 0 ? FontSize : 12,
        Foreground ?? Brushes.Gray);
}
