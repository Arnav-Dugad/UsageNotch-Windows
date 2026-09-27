using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UsageNotch.Services;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace UsageNotch.Controls;

/// <summary>Measured readings and explicitly separated projections. Never joins gaps or periods.</summary>
public sealed class UsageChart : FrameworkElement
{
    public IReadOnlyList<UsagePoint> Points { get; set; } = [];
    public IReadOnlyList<UsageEvent> Events { get; set; } = [];
    public PaceForecast? Forecast { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public DateTimeOffset? SelectionStart { get; set; }
    public DateTimeOffset? SelectionEnd { get; set; }
    public bool Overview { get; set; }
    public event Action<DateTimeOffset, DateTimeOffset>? IntervalSelected;
    public event Action<UsageEvent>? EventClicked;
    private double? _drag;
    private Point? _hover;
    private double PlotWidth => Math.Max(1, ActualWidth - 58);
    private double PlotHeight => Math.Max(1, ActualHeight - 54);
    private double X(DateTimeOffset at) => 40 + (at - Start).TotalSeconds / Math.Max(1, (End - Start).TotalSeconds) * PlotWidth;
    private DateTimeOffset Time(double x) => Start.AddSeconds(Math.Clamp((x - 40) / PlotWidth, 0, 1) * Math.Max(1, (End - Start).TotalSeconds));
    public UsageChart()
    {
        Focusable = true; Cursor = System.Windows.Input.Cursors.Cross;
        System.Windows.Automation.AutomationProperties.SetHelpText(this, "Drag to select an interval. Control+wheel zooms. Home restores the range. Hover shows exact observed readings. Event markers open explanations.");
        MouseMove += (_, e) => { _hover = e.GetPosition(this); UpdateTooltip(); InvalidateVisual(); };
        MouseLeave += (_, _) => { if (_drag is null) _hover = null; InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) => { Focus(); _drag = e.GetPosition(this).X; CaptureMouse(); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_drag is not { } first) return;
            var pos = e.GetPosition(this); _drag = null; ReleaseMouseCapture();
            if (Math.Abs(first - pos.X) > 8) IntervalSelected?.Invoke(Time(Math.Min(first, pos.X)), Time(Math.Max(first, pos.X)));
            else if (pos.Y >= PlotHeight + 10)
            {
                var hit = Events.Where(v => v.At >= Start && v.At <= End).MinBy(v => Math.Abs(X(v.At) - pos.X));
                if (hit is not null && Math.Abs(X(hit.At) - pos.X) < 10) EventClicked?.Invoke(hit);
            }
            InvalidateVisual(); e.Handled = true;
        };
        LostMouseCapture += (_, _) => { _drag = null; InvalidateVisual(); };
        MouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            var center = Time(e.GetPosition(this).X); var scale = e.Delta > 0 ? .65 : 1.5;
            var seconds = Math.Max(60, (End - Start).TotalSeconds * scale);
            IntervalSelected?.Invoke(center.AddSeconds(-seconds / 2), center.AddSeconds(seconds / 2)); e.Handled = true;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Home && Points.Count > 1) { IntervalSelected?.Invoke(Points[0].At, Points[^1].At); e.Handled = true; } };
    }
    private void UpdateTooltip()
    {
        if (_hover is not { } pos) return;
        var evt = Events.Where(v => v.At >= Start && v.At <= End).MinBy(v => Math.Abs(X(v.At) - pos.X));
        if (pos.Y >= PlotHeight + 10 && evt is not null && Math.Abs(X(evt.At) - pos.X) < 10)
        { ToolTip = $"{evt.Kind} · {evt.At.ToLocalTime():MMM d, HH:mm:ss zzz}\n{evt.Explanation}\nClick to inspect."; return; }
        var point = Points.Where(p => p.At >= Start && p.At <= End).MinBy(p => Math.Abs(X(p.At) - pos.X));
        ToolTip = point is null ? "No observations in this interval." :
            $"Nearest observed reading · {point.At.ToLocalTime():MMM d yyyy, HH:mm:ss zzz}\n{point.Used * 100:0.###}% used\nReset: {(point.Reset is { } r ? r.ToLocalTime().ToString("ddd, MMM d HH:mm:ss zzz") : "not reported")}";
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var ink = new SolidColorBrush(Color.FromRgb(153, 162, 180));
        var accent = new SolidColorBrush(Color.FromRgb(117, 224, 191));
        var purple = new SolidColorBrush(Color.FromRgb(190, 164, 240));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), 1);
        var visible = Points.Where(p => p.At >= Start && p.At <= End).ToArray();
        var max = Math.Max(1, visible.Length > 0 ? Math.Ceiling(visible.Max(p => p.Used) * 4) / 4 : 1);
        var last = Points.LastOrDefault();
        var projectionEnd = last?.Reset is { } reset && reset < End ? reset : End;
        bool project = !Overview && last is not null && last.At < End && last.Reset > last.At && Forecast?.PercentPerHour is not null;
        if (project) max = Math.Max(max, last!.Used + (Forecast!.HighRate ?? 0) * Math.Max(0, (projectionEnd - last.At).TotalHours));
        Point Map(DateTimeOffset at, double used) => new(X(at), 8 + PlotHeight * (1 - Math.Clamp(used / max, 0, 1)));
        void Label(string text, double x, double y, System.Windows.Media.Brush? brush = null) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), Overview ? 9 : 10, brush ?? ink, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        for (var i = 0; i <= (Overview ? 1 : 4); i++)
        {
            double fraction = Overview ? i : i / 4d; var y = 8 + PlotHeight * fraction;
            dc.DrawLine(grid, new Point(40, y), new Point(40 + PlotWidth, y));
            Label($"{max * (1 - fraction) * 100:0}%", 0, y - 6);
        }
        dc.PushClip(new RectangleGeometry(new Rect(40, 7, PlotWidth, PlotHeight + 2)));
        for (var i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (i > 0 && UsageAnalytics.Continuous(Points[i - 1], p)) dc.DrawLine(new Pen(accent, Overview ? 1 : 2), Map(Points[i - 1].At, Points[i - 1].Used), Map(p.At, p.Used));
            if (p.At >= Start && p.At <= End && (visible.Length < 160 || Overview)) dc.DrawEllipse(accent, null, Map(p.At, p.Used), Overview ? 1 : 2, Overview ? 1 : 2);
        }
        if (project)
        {
            var p = last!; var hours = Math.Max(0, (projectionEnd - p.At).TotalHours);
            var geometry = new StreamGeometry(); using (var g = geometry.Open())
            {
                g.BeginFigure(Map(p.At, p.Used), true, true);
                g.LineTo(Map(projectionEnd, p.Used + (Forecast!.HighRate ?? 0) * hours), true, false);
                g.LineTo(Map(projectionEnd, p.Used + (Forecast.LowRate ?? 0) * hours), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, 190, 164, 240)), null, geometry);
            dc.DrawLine(new Pen(purple, 1.8) { DashStyle = DashStyles.Dash }, Map(p.At, p.Used), Map(projectionEnd, p.Used + Forecast!.PercentPerHour!.Value / 100 * hours));
            dc.DrawLine(new Pen(purple, 1) { DashStyle = DashStyles.Dot }, new Point(X(p.At), 8), new Point(X(p.At), PlotHeight + 8));
        }
        if (SelectionStart is { } ss && SelectionEnd is { } se)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 130, 164, 255)), new Pen(purple, 1), new Rect(new Point(X(ss), 8), new Point(X(se), PlotHeight + 8)));
        if (_hover is { } hover)
        {
            if (_drag is { } drag) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(45, 130, 164, 255)), null,
                new Rect(new Point(Math.Min(drag, hover.X), 8), new Point(Math.Max(drag, hover.X), PlotHeight + 8)));
            else dc.DrawLine(grid, new Point(hover.X, 8), new Point(hover.X, PlotHeight + 8));
        }
        dc.Pop();
        foreach (var evt in Events.Where(v => v.At >= Start && v.At <= End))
            dc.DrawEllipse(evt.Kind == "Confirmed reset" ? accent : purple, null, new Point(X(evt.At), PlotHeight + 17), 3, 3);
        Label(Start.ToLocalTime().ToString("MMM d HH:mm"), 40, PlotHeight + 30);
        Label(End.ToLocalTime().ToString("MMM d HH:mm"), Math.Max(40, ActualWidth - 111), PlotHeight + 30);
        if (project) Label("Observed | Estimates →", Math.Clamp(X(last!.At) - 80, 42, Math.Max(42, ActualWidth - 180)), 9, purple);
        if (visible.Length == 0) Label("No readings · missing time is unknown", 44, PlotHeight / 2);
    }
}
