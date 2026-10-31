using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageNotch.Services;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace UsageNotch.Controls;

/// <summary>Shared drawing helpers for the History charts.</summary>
internal static class ChartInk
{
    public static FormattedText Text(Visual visual, string text, double size, Color color, FontWeight? weight = null) => new(text, CultureInfo.CurrentCulture,
        System.Windows.FlowDirection.LeftToRight, new Typeface(Ui.Body, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size,
        Palette.Frozen(color), VisualTreeHelper.GetDpi(visual).PixelsPerDip);
    public static Pen Dashed(Color color, double width = 1) { var p = new Pen(Palette.Frozen(color), width) { DashStyle = new DashStyle([2, 3], 0) }; p.Freeze(); return p; }
}

/// <summary>
/// Daily usage bars that grow in one after another, with your average as a dashed line. Days without readings are drawn
/// as an outline, never as zero. Click a bar to open that day.
/// </summary>
public sealed class DailyBars : FrameworkElement
{
    private static readonly DependencyProperty GrowProperty = DependencyProperty.Register("Grow", typeof(double), typeof(DailyBars),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private IReadOnlyList<DayUsage> _days = [];
    private int? _hover;
    public Color Accent { get; set; } = Accents.FromArgb(DesignTokens.Live);
    public bool Reduced { get; set; }
    public DateTime? Selected { get; set; }
    public event Action<DayUsage>? DayClicked;

    public DailyBars()
    {
        Height = 170; Cursor = System.Windows.Input.Cursors.Hand;
        MouseMove += (_, e) => { var i = Index(e.GetPosition(this).X); if (i != _hover) { _hover = i; ToolTip = Tip(i); InvalidateVisual(); } };
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        MouseLeftButtonUp += (_, e) => { if (Index(e.GetPosition(this).X) is { } i && _days[i].Used is not null) DayClicked?.Invoke(_days[i]); };
        Theme.Changed += InvalidateVisual;
    }

    public IReadOnlyList<DayUsage> Days
    {
        get => _days;
        set
        {
            _days = value; InvalidateVisual();
            BeginAnimation(GrowProperty, null);
            if (!Reduced) BeginAnimation(GrowProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private const double Bottom = 22, Top = 12;
    private double Slot => _days.Count == 0 ? 1 : ActualWidth / _days.Count;
    private int? Index(double x) => _days.Count == 0 ? null : (int)Math.Clamp(Math.Floor(x / Slot), 0, _days.Count - 1);
    private string? Tip(int? i) => i is not { } k ? null : $"{_days[k].Date:dddd, MMM d}\n" + (_days[k].Used is { } u ? $"{u * 100:0.#}% of the limit used" : "No readings that day");

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(RenderSize));
        if (_days.Count == 0 || ActualWidth < 10) return;
        var t = Theme.Current;
        var recorded = _days.Where(d => d.Used is not null).Select(d => d.Used!.Value).ToArray();
        var max = Math.Max(.05, recorded.DefaultIfEmpty(0).Max()) * 1.1;
        var plot = ActualHeight - Bottom - Top; var slot = Slot; var width = Math.Clamp(slot * .62, 2, 34);
        var grow = (double)GetValue(GrowProperty);
        for (var i = 0; i < _days.Count; i++)
        {
            var d = _days[i]; var x = i * slot + (slot - width) / 2;
            var local = Math.Clamp(grow * 1.6 - i / (double)_days.Count * .6, 0, 1);
            var eased = 1 - Math.Pow(1 - local, 3);
            if (d.Used is not { } used)
            {
                dc.DrawRoundedRectangle(null, ChartInk.Dashed(Palette.Alpha(t.Faint, .5)), new Rect(x, Top + plot - 10, width, 10), 3, 3);
                continue;
            }
            var h = Math.Max(3, plot * used / max * eased);
            var highlight = _hover == i || Selected == d.Date;
            var top = Palette.Alpha(Accent, highlight ? 1 : .92); var bottom = Palette.Alpha(Accent, highlight ? .55 : .35);
            var fill = new LinearGradientBrush(top, bottom, 90); fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, new Rect(x, Top + plot - h, width, h), Math.Min(6, width / 2), Math.Min(6, width / 2));
            if (highlight) dc.DrawRoundedRectangle(null, new Pen(Palette.Frozen(Palette.Alpha(t.Ink, .6)), 1), new Rect(x - 1, Top + plot - h - 1, width + 2, h + 2), 6, 6);
        }
        if (recorded.Length > 0)
        {
            var average = recorded.Average(); var y = Top + plot - plot * average / max;
            dc.DrawLine(ChartInk.Dashed(Palette.Alpha(t.Muted, .8)), new Point(0, y), new Point(ActualWidth, y));
            var label = ChartInk.Text(this, $"avg {average * 100:0}%", 10, t.Muted);
            dc.DrawText(label, new Point(ActualWidth - label.Width, Math.Max(0, y - label.Height - 1)));
        }
        var step = _days.Count > 14 ? 7 : 1;
        for (var i = _days.Count - 1; i >= 0; i -= step)
        {
            var text = ChartInk.Text(this, _days.Count > 14 ? _days[i].Date.ToString("MMM d") : _days[i].Date.ToString("ddd"), 10, t.Faint);
            dc.DrawText(text, new Point(Math.Clamp(i * slot + slot / 2 - text.Width / 2, 0, ActualWidth - text.Width), ActualHeight - Bottom + 6));
        }
    }
}

/// <summary>90 days as a calendar of weeks, coloured by how much was used; days without readings are outlined.</summary>
public sealed class CalendarGrid : FrameworkElement
{
    private IReadOnlyList<DayUsage> _days = [];
    private int? _hover;
    public Color Accent { get; set; } = Accents.FromArgb(DesignTokens.Live);
    public event Action<DayUsage>? DayClicked;
    public CalendarGrid()
    {
        Height = 196; Cursor = System.Windows.Input.Cursors.Hand;
        MouseMove += (_, e) => { var i = Hit(e.GetPosition(this)); if (i != _hover) { _hover = i; ToolTip = i is { } k ? $"{_days[k].Date:dddd, MMM d}\n" + (_days[k].Used is { } u ? $"{u * 100:0.#}% used" : "No readings") : null; InvalidateVisual(); } };
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        MouseLeftButtonUp += (_, e) => { if (Hit(e.GetPosition(this)) is { } i && _days[i].Used is not null) DayClicked?.Invoke(_days[i]); };
        Theme.Changed += InvalidateVisual;
    }
    public IReadOnlyList<DayUsage> Days { get => _days; set { _days = value; InvalidateVisual(); } }

    private const double Left = 30, TopPad = 18;
    private (int Weeks, double Cell, DateTime First) Layout()
    {
        if (_days.Count == 0) return (0, 0, DateTime.Today);
        var first = _days[0].Date.AddDays(-(int)_days[0].Date.DayOfWeek);
        var weeks = (int)Math.Ceiling(((_days[^1].Date - first).TotalDays + 1) / 7);
        var cell = Math.Min((ActualHeight - TopPad) / 7, (ActualWidth - Left) / Math.Max(1, weeks));
        return (weeks, cell, first);
    }
    private Rect CellRect(int index)
    {
        var (_, cell, first) = Layout(); var d = _days[index].Date;
        var week = (int)((d - first).TotalDays / 7); var row = (int)d.DayOfWeek;
        return new Rect(Left + week * cell + 1.5, TopPad + row * cell + 1.5, cell - 3, cell - 3);
    }
    private int? Hit(Point p) { for (var i = 0; i < _days.Count; i++) if (CellRect(i).Contains(p)) return i; return null; }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(RenderSize));
        if (_days.Count == 0) return;
        var t = Theme.Current; var (_, cell, first) = Layout();
        var max = Math.Max(.05, _days.Where(d => d.Used is not null).Select(d => d.Used!.Value).DefaultIfEmpty(0).Max());
        foreach (var (label, row) in new[] { ("Mon", 1), ("Wed", 3), ("Fri", 5) })
            dc.DrawText(ChartInk.Text(this, label, 9.5, t.Faint), new Point(0, TopPad + row * cell + cell / 2 - 7));
        var lastMonth = -1;
        for (var i = 0; i < _days.Count; i++)
        {
            var r = CellRect(i); var d = _days[i];
            if (d.Date.Day <= 7 && d.Date.DayOfWeek == DayOfWeek.Sunday && d.Date.Month != lastMonth || i == 0)
            { lastMonth = d.Date.Month; dc.DrawText(ChartInk.Text(this, d.Date.ToString("MMM"), 9.5, t.Faint), new Point(r.X, 0)); }
            var radius = Math.Max(2, cell * .22);
            if (d.Used is not { } used) { dc.DrawRoundedRectangle(null, ChartInk.Dashed(Palette.Alpha(t.Faint, .45)), r, radius, radius); continue; }
            var level = used <= .0001 ? .08 : .25 + .75 * Math.Sqrt(used / max);
            dc.DrawRoundedRectangle(Palette.Frozen(Palette.Alpha(Accent, level)), _hover == i ? new Pen(Palette.Frozen(t.Ink), 1.2) : null, r, radius, radius);
        }
    }
}

/// <summary>Weekday × hour map of when you use a limit most. Hours never observed are hatched, not shown as quiet.</summary>
public sealed class HeatGrid : FrameworkElement
{
    private double[] _heat = new double[168];
    private int[] _observed = new int[168];
    public Color Accent { get; set; } = Accents.FromArgb(DesignTokens.Live);
    public bool Use24 { get; set; }
    public HeatGrid() { Height = 190; Theme.Changed += InvalidateVisual; MouseMove += (_, e) => UpdateTip(e.GetPosition(this)); }
    public void Set(double[] heat, int[] observed) { _heat = heat; _observed = observed; InvalidateVisual(); }

    private const double Left = 34, Top = 4, Bottom = 18;
    private double CellW => (ActualWidth - Left) / 24;
    private double CellH => (ActualHeight - Top - Bottom) / 7;
    private static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    private void UpdateTip(Point p)
    {
        var hour = (int)((p.X - Left) / CellW); var day = (int)((p.Y - Top) / CellH);
        if (hour is < 0 or > 23 || day is < 0 or > 6) { ToolTip = null; return; }
        var i = day * 24 + hour;
        ToolTip = $"{Days[day]} {BudgetSetting.Clock(hour * 60, Use24)}\n" + (_observed[i] == 0 ? "No readings in this hour" : $"{_heat[i] * 100:0.#}% used over {_observed[i]} observed hour(s)");
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth < 60) return;
        var t = Theme.Current; var max = Math.Max(.0001, _heat.Max());
        for (var d = 0; d < 7; d++)
        {
            dc.DrawText(ChartInk.Text(this, Days[d], 9.5, t.Faint), new Point(0, Top + d * CellH + CellH / 2 - 7));
            for (var h = 0; h < 24; h++)
            {
                var i = d * 24 + h; var r = new Rect(Left + h * CellW + 1, Top + d * CellH + 1, CellW - 2, CellH - 2);
                if (_observed[i] == 0) { dc.DrawRoundedRectangle(null, ChartInk.Dashed(Palette.Alpha(t.Faint, .3)), r, 3, 3); continue; }
                var level = _heat[i] <= .0001 ? .06 : .18 + .82 * Math.Sqrt(_heat[i] / max);
                dc.DrawRoundedRectangle(Palette.Frozen(Palette.Alpha(Accent, level)), null, r, 3, 3);
            }
        }
        foreach (var h in new[] { 0, 6, 12, 18 })
            dc.DrawText(ChartInk.Text(this, BudgetSetting.Clock(h * 60, Use24), 9.5, t.Faint), new Point(Left + h * CellW, ActualHeight - Bottom + 3));
    }
}

/// <summary>Your providers' daily usage side by side, one line each in its accent colour.</summary>
public sealed class CompareChart : FrameworkElement
{
    public sealed record Series(string Name, Color Color, IReadOnlyList<DayUsage> Days);
    private IReadOnlyList<Series> _series = [];
    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register("Reveal", typeof(double), typeof(CompareChart), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Reduced { get; set; }
    public CompareChart() { Height = 170; Theme.Changed += InvalidateVisual; }
    public IReadOnlyList<Series> Data
    {
        get => _series;
        set
        {
            _series = value; InvalidateVisual();
            if (!Reduced) BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_series.Count == 0 || ActualWidth < 20) return;
        var t = Theme.Current; const double top = 8, bottom = 20;
        var n = _series.Max(s => s.Days.Count); if (n < 2) return;
        var max = Math.Max(.05, _series.SelectMany(s => s.Days).Where(d => d.Used is not null).Select(d => d.Used!.Value).DefaultIfEmpty(0).Max()) * 1.1;
        double X(int i) => ActualWidth * i / (n - 1);
        double Y(double v) => top + (ActualHeight - top - bottom) * (1 - v / max);
        for (var g = 0; g <= 2; g++) dc.DrawLine(new Pen(Palette.Frozen(t.Hairline), 1), new Point(0, Y(max * g / 2.2)), new Point(ActualWidth, Y(max * g / 2.2)));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth * (double)GetValue(RevealProperty), ActualHeight)));
        foreach (var s in _series)
        {
            var pen = new Pen(Palette.Frozen(s.Color), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            Point? last = null;
            for (var i = 0; i < s.Days.Count; i++)
            {
                if (s.Days[i].Used is not { } v) { last = null; continue; }
                var p = new Point(X(i + n - s.Days.Count), Y(v));
                if (last is { } l) dc.DrawLine(pen, l, p);
                dc.DrawEllipse(Palette.Frozen(s.Color), null, p, 2.4, 2.4);
                last = p;
            }
        }
        dc.Pop();
        var first = _series[0].Days;
        if (first.Count > 0)
        {
            dc.DrawText(ChartInk.Text(this, first[0].Date.ToString("MMM d"), 10, t.Faint), new Point(0, ActualHeight - bottom + 5));
            var end = ChartInk.Text(this, "Today", 10, t.Faint); dc.DrawText(end, new Point(ActualWidth - end.Width, ActualHeight - bottom + 5));
        }
    }
}

/// <summary>Horizontal share bars: a name, its part of the whole and a figure, used for projects and models.</summary>
public static class ShareList
{
    public static UIElement Build(IEnumerable<(string Name, double Share, string Value)> rows, Color color, bool reduced)
    {
        var stack = new System.Windows.Controls.StackPanel();
        var index = 0;
        foreach (var (name, share, value) in rows)
        {
            var tinted = new ColorBar(color) { Value = Math.Clamp(share, 0, 1), Reduced = reduced, Delay = 60 * index, Height = 6, Margin = new Thickness(0, 6, 0, 0) };
            var head = Ui.Columns((Ui.Text(name, 12.5, FontWeights.Normal, "Ui.Text", false), new GridLength(1, GridUnitType.Star)), (Ui.Text(value, 12, FontWeights.SemiBold, "Ui.Muted", false), GridLength.Auto));
            var row = Ui.Stack(head, tinted); row.Margin = new Thickness(0, 0, 0, 10);
            stack.Children.Add(row); index++;
        }
        return stack;
    }
}

/// <summary>A capsule in one colour (not the usage ramp), for shares of a total.</summary>
public sealed class ColorBar(Color color) : FrameworkElement
{
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(ColorBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get; set; }
    public bool Reduced { get; set; }
    public int Delay { get; set; }
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) =>
        {
            if (Reduced) { SetValue(ShownProperty, Value); return; }
            BeginAnimation(ShownProperty, new DoubleAnimation(0, Value, TimeSpan.FromMilliseconds(760)) { BeginTime = TimeSpan.FromMilliseconds(Delay), EasingFunction = new SpringEase { Damping = .85 } });
        };
    }
    protected override void OnRender(DrawingContext dc)
    {
        var h = ActualHeight; var r = h / 2;
        dc.DrawRoundedRectangle(Palette.Frozen(Theme.Current.Track), null, new Rect(0, 0, ActualWidth, h), r, r);
        var w = ActualWidth * Math.Clamp((double)GetValue(ShownProperty), 0, 1);
        if (w > 1) { var fill = new LinearGradientBrush(Palette.Alpha(color, .6), color, 0); fill.Freeze(); dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, w, h), Math.Min(r, w / 2), r); }
    }
}
