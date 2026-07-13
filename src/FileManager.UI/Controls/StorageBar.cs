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
/// increments against the volume's total capacity. Painted end-to-end so each colour's right edge is
/// a threshold: <c>used-now (CSU) → +at-rest (SUAR) → +transient (RPSU) → +worst-case (APSU)</c>,
/// with the remainder free. A dashed line marks <c>capacity − safety-margin</c>. Colours stay calm
/// unless a threshold crosses that line: the worst-case headroom turns amber when the safe ceiling
/// crosses it, and the transient + worst-case turn red when the realistic peak crosses it.
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
    // Palette mirrors DryRunPalette / the axaml chip colours so the bar reads the same as the pills.
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
    private static readonly IBrush UsedNowBrush = new SolidColorBrush(Color.Parse("#757575"));   // grey
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#2E7D32"));      // green
    private static readonly IBrush FreedBrush = new SolidColorBrush(Color.FromArgb(120, 46, 125, 50)); // faint green
    private static readonly IBrush TransientBrush = new SolidColorBrush(Color.Parse("#1E88E5"));  // blue
    private static readonly IBrush WorstCalmBrush = new SolidColorBrush(Color.FromArgb(90, 158, 158, 158)); // faint grey
    private static readonly IBrush AmberBrush = new SolidColorBrush(Color.Parse("#FFA000"));      // amber
    private static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#E53935"));        // red
    private static readonly IPen MarginPen =
        new Pen(new SolidColorBrush(Color.FromArgb(170, 120, 120, 120)), 1.5) { DashStyle = DashStyle.Dash };

    private static readonly IBrush LabelLight = Brushes.White;
    private static readonly IBrush LabelDark = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20));
    private static readonly Typeface LabelTypeface = new(FontFamily.Default);
    private const double LabelFontSize = 10;
    private const double LabelPadding = 4;   // min free px on each side before a label is printed

    private const double CornerRadius = 3;
    private const double DefaultHeight = 20;

    // The bands painted by the last Render, left→right, for hover hit-testing. Overlapping bands are
    // resolved by scanning newest-first so the visually-topmost band wins the tooltip.
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

            double scale = w / cap;
            double Clamp(double v) => Math.Clamp(v, 0, cap) * scale;
            void Seg(double from, double to, IBrush brush, string acronym, string expanded, IBrush labelBrush)
            {
                double x0 = Clamp(from);
                double x1 = Clamp(to);
                if (x1 <= x0)
                    return;
                context.DrawRectangle(brush, null, new Rect(x0, 0, x1 - x0, h));
                _bands.Add((x0, x1, $"{expanded} ({acronym})"));
                DrawLabel(context, x0, x1, h, acronym, labelBrush);
            }

            double used = UsedNowBytes;
            double settled = SettledBytes;
            double peak = Math.Max(settled, RealisticPeakBytes);
            double ceiling = Math.Max(peak, SafeCeilingBytes);
            double marginLine = cap - MarginBytes;

            bool overRed = RealisticPeakBytes > marginLine;
            bool overAmber = SafeCeilingBytes > marginLine;

            // Used-now baseline; then the at-rest delta (green growth, or a faint "will free" band when
            // the run net-frees space on this volume).
            if (settled >= used)
            {
                Seg(0, used, UsedNowBrush, "CSU", "Current Storage Used", LabelLight);
                Seg(used, settled, AddedBrush, "SUAR", "Storage Used At Rest", LabelLight);
            }
            else
            {
                // Net-frees space: the grey that remains is the settled at-rest total; the faint band
                // above it is what the run releases.
                Seg(0, settled, UsedNowBrush, "SUAR", "Storage Used At Rest", LabelLight);
                Seg(settled, used, FreedBrush, "Freed", "Space Freed", LabelDark);
            }

            // Transient (settled → realistic peak) and worst-case headroom (peak → safe ceiling).
            Seg(settled, peak, overRed ? RedBrush : TransientBrush,
                "RPSU", "Realistic Peak Storage Usage", LabelLight);
            Seg(peak, ceiling, overRed ? RedBrush : overAmber ? AmberBrush : WorstCalmBrush,
                "APSU", "Absolute Peak Storage Usage", overRed ? LabelLight : LabelDark);

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
