using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GuardianX.App.Views;

/// <summary>The health gauge: an arc from 0 to 100% with today's health and a tick for where it started.</summary>
public sealed class HealthRing : FrameworkElement
{
    public double Health { get => (double)GetValue(HealthProperty); set => SetValue(HealthProperty, value); }
    public static readonly DependencyProperty HealthProperty = DependencyProperty.Register(
        nameof(Health), typeof(double), typeof(HealthRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Frozen(Color.FromRgb(0x1F, 0x26, 0x30));
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xE8, 0xED, 0xF2));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x8A, 0x96, 0xA3));

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double thick = size * 0.075, r = size / 2 - thick;
        const double start = 135, sweep = 270; // open at the bottom

        dc.DrawGeometry(null, new Pen(Track, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(c, r, start, sweep));
        double h = Math.Clamp(Health, 0, 100);
        var color = h >= 90 ? Color.FromRgb(0x3D, 0xDC, 0x97) : h >= 80 ? Color.FromRgb(0xFF, 0xB5, 0x47) : Color.FromRgb(0xFF, 0x5D, 0x5D);
        if (h > 0)
            dc.DrawGeometry(null, new Pen(Frozen(color), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Arc(c, r, start, sweep * h / 100));

        // 80% is where most makers call a battery worn out.
        double a80 = (start + sweep * 0.8) * Math.PI / 180;
        dc.DrawLine(new Pen(Muted, 2), new Point(c.X + (r - thick) * Math.Cos(a80), c.Y + (r - thick) * Math.Sin(a80)),
            new Point(c.X + (r + thick) * Math.Cos(a80), c.Y + (r + thick) * Math.Sin(a80)));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var big = Text($"{h:0.0}", size * 0.22, Ink, "Segoe UI Variable Display", FontWeights.SemiBold, dpi);
        var pct = Text("%", size * 0.09, Muted, "Segoe UI Variable Display", FontWeights.Normal, dpi);
        double totalW = big.Width + pct.Width + 2;
        double y = c.Y - big.Height / 2 - size * 0.03;
        dc.DrawText(big, new Point(c.X - totalW / 2, y));
        dc.DrawText(pct, new Point(c.X - totalW / 2 + big.Width + 2, y + big.Baseline - pct.Baseline));
        var label = Text("BATTERY HEALTH", size * 0.045, Muted, "Cascadia Mono", FontWeights.Normal, dpi);
        dc.DrawText(label, new Point(c.X - label.Width / 2, y + big.Height + size * 0.01));
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.9);
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var fig = new PathFigure { StartPoint = p0, IsClosed = false };
        fig.Segments.Add(new ArcSegment(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }

    internal static FormattedText Text(string s, double size, Brush b, string family, FontWeight weight, double dpi) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(family + ", Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, b, dpi);

    internal static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>A time line chart. Points can be split into segments (gaps are not joined).</summary>
public sealed class LineChart : FrameworkElement
{
    public sealed record Point2(DateTimeOffset Time, double Value, bool Highlight);

    public IReadOnlyList<Point2> Points { get; set; } = Array.Empty<Point2>();
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public double Min { get; set; } = 0;
    public double Max { get; set; } = 100;
    public double? BandFrom { get; set; }   // shaded band, e.g. 95-100% "stress zone"
    public string BandLabel { get; set; } = "";
    public string Unit { get; set; } = "%";
    public TimeSpan MaxGap { get; set; } = TimeSpan.FromMinutes(6);
    public bool DailyTicks { get; set; }
    public bool ShowDots { get; set; }
    public Color LineColor { get; set; } = Color.FromRgb(0x6C, 0xB6, 0xFF);
    public Color HighlightColor { get; set; } = Color.FromRgb(0xFF, 0xB5, 0x47);
    public string EmptyText { get; set; } = "Collecting data. The first readings appear within a minute.";

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = HealthRing.Frozen(Color.FromRgb(0x8A, 0x96, 0xA3));
        var grid = new Pen(HealthRing.Frozen(Color.FromRgb(0x1F, 0x26, 0x30)), 1);
        double left = 40, bottom = 22, top = 6, right = 8;
        double pw = w - left - right, ph = h - top - bottom;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double X(DateTimeOffset t) => left + pw * (t - From).TotalSeconds / Math.Max(1, (To - From).TotalSeconds);
        double Y(double v) => top + ph * (1 - (Math.Clamp(v, Min, Max) - Min) / Math.Max(0.0001, Max - Min));

        if (BandFrom is double bf)
        {
            var band = HealthRing.Frozen(Color.FromArgb(0x22, 0xFF, 0xB5, 0x47));
            dc.DrawRectangle(band, null, new Rect(left, Y(Max), pw, Y(bf) - Y(Max)));
            var t = HealthRing.Text(BandLabel, 10, HealthRing.Frozen(Color.FromRgb(0xC9, 0x8E, 0x3A)), "Cascadia Mono", FontWeights.Normal, dpi);
            dc.DrawText(t, new Point(left + pw - t.Width - 4, Y(Max) + 2));
        }

        for (int i = 0; i <= 4; i++)
        {
            double v = Min + (Max - Min) * i / 4;
            dc.DrawLine(grid, new Point(left, Y(v)), new Point(left + pw, Y(v)));
            var t = HealthRing.Text($"{v:0.#}{Unit}", 10, muted, "Cascadia Mono", FontWeights.Normal, dpi);
            dc.DrawText(t, new Point(left - t.Width - 6, Y(v) - t.Height / 2));
        }

        // Time ticks: every 6 hours, or every few days for long ranges.
        var span = To - From;
        var step = DailyTicks ? TimeSpan.FromDays(Math.Max(1, Math.Ceiling(span.TotalDays / 7))) : TimeSpan.FromHours(6);
        var tick = DailyTicks ? new DateTimeOffset(From.Date, From.Offset) : new DateTimeOffset(From.Year, From.Month, From.Day, From.Hour / 6 * 6, 0, 0, From.Offset);
        for (; tick <= To; tick += step)
        {
            if (tick < From) continue;
            var t = HealthRing.Text(DailyTicks ? tick.ToString("d MMM") : tick.ToString("HH:mm"), 10, muted, "Cascadia Mono", FontWeights.Normal, dpi);
            dc.DrawText(t, new Point(Math.Min(X(tick) - t.Width / 2, w - t.Width), h - bottom + 6));
        }

        if (Points.Count < 2)
        {
            var t = HealthRing.Text(EmptyText, 12, muted, "Segoe UI", FontWeights.Normal, dpi);
            dc.DrawText(t, new Point(left + (pw - t.Width) / 2, top + ph / 2 - t.Height / 2));
            return;
        }

        var normal = new Pen(HealthRing.Frozen(LineColor), 2) { LineJoin = PenLineJoin.Round };
        var hi = new Pen(HealthRing.Frozen(HighlightColor), 2) { LineJoin = PenLineJoin.Round };
        for (int i = 1; i < Points.Count; i++)
        {
            var a = Points[i - 1];
            var b = Points[i];
            if (b.Time - a.Time > MaxGap) continue;
            dc.DrawLine(a.Highlight ? hi : normal, new Point(X(a.Time), Y(a.Value)), new Point(X(b.Time), Y(b.Value)));
        }
        if (ShowDots)
            foreach (var p in Points)
                dc.DrawEllipse(HealthRing.Frozen(LineColor), null, new Point(X(p.Time), Y(p.Value)), 2.5, 2.5);
        var last = Points[^1];
        dc.DrawEllipse(HealthRing.Frozen(last.Highlight ? HighlightColor : LineColor), null, new Point(X(last.Time), Y(last.Value)), 4, 4);
    }
}
