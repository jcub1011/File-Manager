using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Serilog;

namespace FileManager.UI.Controls;

/// <summary>A render-only horizontal bar showing one destination volume's storage picture as nested
/// increments against the volume's total capacity. Every figure it draws is a threshold measured from
/// zero, so every band is a bar from zero to its threshold and the bands are painted largest-first —
/// each smaller bar covers the one under it, leaving each colour's right edge at its own threshold and
/// the remainder free. Typically that reads <c>used-now (CSU) → +at-rest (SUAR) → +transient (RPSU) →
/// +worst-case (APSU)</c>, but the order is decided by the values, not assumed: a run that frees space
/// settles below where the volume started, and the "will free" band takes the place of the at-rest one.
/// A dashed line marks <c>capacity − safety-margin</c>. Colours stay calm unless a threshold crosses
/// that line: the worst-case headroom turns amber when the safe ceiling crosses it, and the transient
/// + worst-case turn red when the realistic peak crosses it.
/// <para>
/// Each coloured band is labelled with its acronym when the band is wide enough; a per-band tooltip
/// (expanded form + acronym, e.g. "Realistic Peak Storage Usage (RPSU)") is shown on hover for every
/// band, including the ones too narrow to print their label. Hover tracking updates
/// <see cref="ToolTip.TipProperty"/> from the band the pointer is over — the bands are recomputed on
/// every <see cref="Render"/> and cached for hit-testing.
/// </para>
/// <para>
/// Drawing directly (like <see cref="PathText"/>) keeps everything out of the measure/arrange pass —
/// <see cref="Render"/> only reads the arranged <see cref="Visual.Bounds"/> and draws.
/// </para></summary>
public sealed class StorageBar : Control
{
    // The semantic band colours are resolved from the shared palette tokens at render time (see
    // ResolveBands) so the bar tracks Tokens.axaml — the single source of truth. Only the palette-
    // independent neutrals (track, worst-case-calm headroom, dashed margin) are fixed here. Fallback
    // hexes below match the tokens in case resolution fails (e.g. no application in a headless test).
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
    private static readonly IBrush WorstCalmBrush = new SolidColorBrush(Color.FromArgb(90, 158, 158, 158)); // faint grey
    private static readonly IPen MarginPen =
        new Pen(new SolidColorBrush(Color.FromArgb(170, 120, 120, 120)), 1.5) { DashStyle = DashStyle.Dash };

    private static readonly IBrush LabelLight = Brushes.White;
    private static readonly IBrush LabelDark = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20));
    private static readonly Typeface LabelTypeface = new(FontFamily.Default);
    private const double LabelFontSize = 10;
    private const double LabelPadding = 4;   // min free px on each side before a label is printed

    private const double CornerRadius = 3;
    private const double DefaultHeight = 20;

    // The visible span of each band painted by the last Render, for hover hit-testing. Spans are the
    // uncovered slices of the layered bars, so they never overlap and any scan order finds the same
    // band; kept as a scan for the handful of entries involved.
    private readonly List<(double X0, double X1, string Tip)> _bands = [];
    private string? _currentTip;

    public static readonly StyledProperty<double> CapacityBytesProperty =
        AvaloniaProperty.Register<StorageBar, double>(nameof(CapacityBytes));
    public static readonly StyledProperty<double> UsedNowBytesProperty =
        AvaloniaProperty.Register<StorageBar, double>(nameof(UsedNowBytes));
    public static readonly StyledProperty<double> SettledBytesProperty =
        AvaloniaProperty.Register<StorageBar, double>(nameof(SettledBytes));
    public static readonly StyledProperty<double> RealisticPeakBytesProperty =
        AvaloniaProperty.Register<StorageBar, double>(nameof(RealisticPeakBytes));
    public static readonly StyledProperty<double> SafeCeilingBytesProperty =
        AvaloniaProperty.Register<StorageBar, double>(nameof(SafeCeilingBytes));
    public static readonly StyledProperty<double> MarginBytesProperty =
        AvaloniaProperty.Register<StorageBar, double>(nameof(MarginBytes));

    static StorageBar() =>
        AffectsRender<StorageBar>(
            CapacityBytesProperty, UsedNowBytesProperty, SettledBytesProperty,
            RealisticPeakBytesProperty, SafeCeilingBytesProperty, MarginBytesProperty);

    public StorageBar() => ClipToBounds = true;

    public double CapacityBytes { get => GetValue(CapacityBytesProperty); set => SetValue(CapacityBytesProperty, value); }
    public double UsedNowBytes { get => GetValue(UsedNowBytesProperty); set => SetValue(UsedNowBytesProperty, value); }
    public double SettledBytes { get => GetValue(SettledBytesProperty); set => SetValue(SettledBytesProperty, value); }
    public double RealisticPeakBytes { get => GetValue(RealisticPeakBytesProperty); set => SetValue(RealisticPeakBytesProperty, value); }
    public double SafeCeilingBytes { get => GetValue(SafeCeilingBytesProperty); set => SetValue(SafeCeilingBytesProperty, value); }
    public double MarginBytes { get => GetValue(MarginBytesProperty); set => SetValue(MarginBytesProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, DefaultHeight);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double x = e.GetPosition(this).X;
        string? tip = null;
        for (int i = _bands.Count - 1; i >= 0; i--)   // newest (topmost) band wins
        {
            if (x >= _bands[i].X0 && x < _bands[i].X1)
            {
                tip = _bands[i].Tip;
                break;
            }
        }
        if (tip != _currentTip)
        {
            _currentTip = tip;
            ToolTip.SetTip(this, tip);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _currentTip = null;
        ToolTip.SetTip(this, null);
    }

    public override void Render(DrawingContext context)
    {
        _bands.Clear();
        try
        {
            double w = Bounds.Width;
            double h = Bounds.Height;
            double cap = CapacityBytes;
            if (w <= 0 || h <= 0)
                return;

            var track = new Rect(0, 0, w, h);
            context.DrawRectangle(TrackBrush, null, track, CornerRadius, CornerRadius);
            if (cap <= 0)
                return;   // capacity unknown — the VM shows a text fallback instead of a bar

            // Semantic band colours from the shared palette tokens (Tokens.axaml); fallbacks match the
            // tokens for the headless/no-application case. Resolved per render — a cheap dictionary walk.
            IBrush usedNow = Band("Brush.Muted", Color.Parse("#717780"));
            IBrush added = Band("Brush.Success", Color.Parse("#56986E"));
            IBrush transient = Band("Brush.Info", Color.Parse("#4E86B6"));
            IBrush amber = Band("Brush.Warning", Color.Parse("#C39A3C"));
            IBrush red = Band("Brush.Danger", Color.Parse("#C05A54"));
            // "Will free" band: the success hue at ~47% alpha.
            Color addedColor = added is ISolidColorBrush s ? s.Color : Color.Parse("#56986E");
            IBrush freed = new SolidColorBrush(Color.FromArgb(120, addedColor.R, addedColor.G, addedColor.B));

            IBrush Band(string key, Color fallback) =>
                this.TryFindResource(key, out object? r) && r is ISolidColorBrush b ? b : new SolidColorBrush(fallback);

            double scale = w / cap;
            double Clamp(double v) => Math.Clamp(v, 0, cap) * scale;

            double marginLine = cap - MarginBytes;
            bool overRed = RealisticPeakBytes > marginLine;
            bool overAmber = SafeCeilingBytes > marginLine;

            // Largest band first: every figure is a threshold from zero, so each smaller bar simply
            // covers the one beneath it and no band needs to know where the others landed.
            StorageBand[] bands = StorageBandLayout.Compute(
                UsedNowBytes, SettledBytes, RealisticPeakBytes, SafeCeilingBytes);

            foreach (StorageBand band in bands)
            {
                double x1 = Clamp(band.Threshold);
                if (x1 > 0)
                    context.DrawRectangle(BrushFor(band.Kind), null, new Rect(0, 0, x1, h));
            }

            // Labels and hover targets go on afterwards, over the finished stack, and describe the
            // visible slice of each band rather than its full bar — the covered part belongs to
            // whichever smaller band is painted on top of it.
            foreach (StorageBand band in bands)
            {
                if (!band.IsVisible)
                    continue;
                double x0 = Clamp(band.VisibleFrom);
                double x1 = Clamp(band.Threshold);
                if (x1 <= x0)
                    continue;   // survives clamping only as a hairline; nothing to label or hover
                (string acronym, string expanded) = NameOf(band.Kind);
                _bands.Add((x0, x1, $"{expanded} ({acronym})"));
                DrawLabel(context, x0, x1, h, acronym, LabelBrushFor(band.Kind, overRed));
            }

            IBrush BrushFor(StorageBandKind kind) => kind switch
            {
                StorageBandKind.CurrentUsed => usedNow,
                StorageBandKind.UsedAtRest => added,
                StorageBandKind.Freed => freed,
                StorageBandKind.RealisticPeak => overRed ? red : transient,
                _ => overRed ? red : overAmber ? amber : WorstCalmBrush,
            };

            if (marginLine > 0 && marginLine < cap)
            {
                double mx = marginLine * scale;
                context.DrawLine(MarginPen, new Point(mx, 0), new Point(mx, h));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "StorageBar failed to render");
        }
    }

    // The bar's vocabulary: the acronym printed in the band and the expanded form its tooltip carries.
    private static (string Acronym, string Expanded) NameOf(StorageBandKind kind) => kind switch
    {
        StorageBandKind.CurrentUsed => ("CSU", "Current Storage Used"),
        StorageBandKind.UsedAtRest => ("SUAR", "Storage Used At Rest"),
        StorageBandKind.Freed => ("Freed", "Space Freed"),
        StorageBandKind.RealisticPeak => ("RPSU", "Realistic Peak Storage Usage"),
        _ => ("APSU", "Absolute Peak Storage Usage"),
    };

    // Label ink is chosen for contrast against the band's own fill: the two pale bands take dark text,
    // and the worst-case band flips to light once the danger colour turns it red.
    private static IBrush LabelBrushFor(StorageBandKind kind, bool overRed) => kind switch
    {
        StorageBandKind.Freed => LabelDark,
        StorageBandKind.AbsolutePeak => overRed ? LabelLight : LabelDark,
        _ => LabelLight,
    };

    // Prints the acronym centred in a band only when it fits with padding; otherwise the band relies
    // on its hover tooltip alone.
    private static void DrawLabel(DrawingContext context, double x0, double x1, double h, string acronym, IBrush brush)
    {
        var text = new FormattedText(
            acronym, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, LabelFontSize, brush);
        double available = x1 - x0;
        if (text.Width + 2 * LabelPadding > available)
            return;
        double tx = x0 + (available - text.Width) / 2;
        double ty = (h - text.Height) / 2;
        context.DrawText(text, new Point(tx, ty));
    }
}
