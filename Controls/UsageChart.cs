using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UsageNotch.Services;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace UsageNotch.Controls;

/// <summary>Time-scaled observations; gaps and reset periods are never joined.</summary>
public sealed class UsageChart : FrameworkElement
{
    public IReadOnlyList<UsagePoint> Points { get; set; } = [];
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = Math.Max(1, ActualWidth - 56);
        var height = Math.Max(1, ActualHeight - 42);
        var ink = new SolidColorBrush(System.Windows.Media.Color.FromRgb(145, 151, 173));
        var accent = new SolidColorBrush(System.Windows.Media.Color.FromRgb(117, 224, 191));
        var grid = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 255, 255, 255)), 1);
        var max = Math.Max(1, Points.Count > 0 ? Math.Ceiling(Points.Max(p => p.Used) * 4) / 4 : 1);
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        for (var i = 0; i <= 4; i++)
        {
            var y = 8 + height * i / 4;
            dc.DrawLine(grid, new Point(40, y), new Point(40 + width, y));
            Label($"{max * (4 - i) * 25:0}%", 0, y - 6);
        }
        var span = Math.Max(1, (End - Start).TotalSeconds);
        Point Map(UsagePoint p) => new(40 + Math.Clamp((p.At - Start).TotalSeconds / span, 0, 1) * width, 8 + height * (1 - p.Used / max));
        for (var i = 0; i < Points.Count; i++)
        {
            var p = Map(Points[i]);
            if (i > 0 && Points[i].Period == Points[i - 1].Period && Points[i].At - Points[i - 1].At <= TimeSpan.FromMinutes(20))
                dc.DrawLine(new Pen(accent, 2), Map(Points[i - 1]), p);
            if (Points.Count < 80 || i == Points.Count - 1) dc.DrawEllipse(accent, null, p, 2.5, 2.5);
        }
        Label(Start.ToLocalTime().ToString("MMM d HH:mm"), 40, height + 22);
        Label(End.ToLocalTime().ToString("MMM d HH:mm"), Math.Max(40, width - 38), height + 22);
        if (Points.Count == 0) Label("History begins with your next successful reading", 66, height / 2);
    }
}
