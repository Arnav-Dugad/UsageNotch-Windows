using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageNotch.Services;
using Brush = System.Windows.Media.Brush;
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
    private static readonly DependencyProperty MorphProperty = DependencyProperty.Register("Morph", typeof(double), typeof(DailyBars),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private IReadOnlyList<DayUsage> _days = [];
    private IReadOnlyList<DayUsage> _old = [];
    private int? _hover;
    public Color Accent { get; set; } = Accents.FromArgb(DesignTokens.Live);
    public bool Reduced { get; set; }
    public DateTime? Selected { get; set; }
    /// <summary>Days with a note get a small dot above their bar.</summary>
    public IReadOnlySet<DateTime> Notes { get; set; } = new HashSet<DateTime>();
    /// <summary>Unusual days (well above the usual for their weekday) get a small ring above their bar.</summary>
    public IReadOnlyDictionary<DateTime, double> Unusual { get; set; } = new Dictionary<DateTime, double>();
    /// <summary>Off for charts that aren't in limits (tokens): no "1 full limit" or average line.</summary>
    public bool LimitLines { get; set; } = true;
    /// <summary>The tooltip for a day; defaults to usage in limits.</summary>
    public Func<DayUsage, string>? Describe { get; set; }
    public event Action<DayUsage>? DayClicked;

    public DailyBars()
    {
        Height = 170; Cursor = System.Windows.Input.Cursors.Hand;
        MouseMove += (_, e) => { var i = Index(e.GetPosition(this).X); if (i != _hover) { _hover = i; ToolTip = Tip(i); InvalidateVisual(); } };
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        MouseLeftButtonUp += (_, e) => { if (Index(e.GetPosition(this).X) is { } i && _days[i].Used is not null) DayClicked?.Invoke(_days[i]); };
        Theme.Changed += InvalidateVisual;
    }

    /// <summary>
    /// New days grow in on first show. After that, a new range morphs: days in both ranges slide and resize into place,
    /// new days grow from the baseline, and days that left shrink away, so 7 → 30 → 90 reads as one chart zooming out.
    /// </summary>
    public IReadOnlyList<DayUsage> Days
    {
        get => _days;
        set
        {
            var morph = _days.Count > 0 && IsLoaded && !Reduced;
            _old = morph ? _days : [];
            _days = value; InvalidateVisual();
            BeginAnimation(GrowProperty, null); BeginAnimation(MorphProperty, null);
            if (Reduced) { SetValue(GrowProperty, 1d); SetValue(MorphProperty, 1d); return; }
            if (morph)
            {
                SetValue(GrowProperty, 1d);
                BeginAnimation(MorphProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(DesignTokens.Motion.EmphasisMs + 240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
            }
            else
            {
                SetValue(MorphProperty, 1d);
                BeginAnimation(GrowProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
    }

    private const double Bottom = 22, Top = 12;
    private double Slot => _days.Count == 0 ? 1 : ActualWidth / _days.Count;
    private int? Index(double x) => _days.Count == 0 ? null : (int)Math.Clamp(Math.Floor(x / Slot), 0, _days.Count - 1);
    private string? Tip(int? i) => i is not { } k ? null : Describe is { } describe ? describe(_days[k]) : $"{_days[k].Date:dddd, MMM d}\n" + (_days[k].Used is { } u ? $"{Ui.Limits(u)} of the limit ({u * 100:0.#}%)\n{Math.Round(_days[k].Coverage * 100):0}% of the day observed" : "No readings that day")
        + (Unusual.TryGetValue(_days[k].Date, out var ratio) ? $"\n{ratio:0.#}× your normal {_days[k].Date:dddd}" : "") + (Notes.Contains(_days[k].Date) ? "\nHas a note" : "");

    private static double MaxOf(IReadOnlyList<DayUsage> days) => Math.Max(.05, days.Where(d => d.Used is not null).Select(d => d.Used!.Value).DefaultIfEmpty(0).Max()) * 1.1;
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(RenderSize));
        if (_days.Count == 0 || ActualWidth < 10) return;
        var t = Theme.Current;
        var recorded = _days.Where(d => d.Used is not null).Select(d => d.Used!.Value).ToArray();
        var m = Math.Clamp((double)GetValue(MorphProperty), 0, 1.15);
        var morphing = _old.Count > 0 && m < 1;
        var newMax = MaxOf(_days); var max = morphing ? Lerp(MaxOf(_old), newMax, Math.Min(1, m)) : newMax;
        var plot = ActualHeight - Bottom - Top; var slot = Slot; var width = Math.Clamp(slot * .62, 2, 34);
        var oldSlot = _old.Count == 0 ? slot : ActualWidth / _old.Count; var oldWidth = Math.Clamp(oldSlot * .62, 2, 34);
        var oldIndex = new Dictionary<DateTime, int>(); for (var j = 0; j < _old.Count; j++) oldIndex[_old[j].Date] = j;
        var grow = (double)GetValue(GrowProperty);
        Brush Fill(bool highlight) { var f = new LinearGradientBrush(Palette.Alpha(Accent, highlight ? 1 : .92), Palette.Alpha(Accent, highlight ? .55 : .35), 90); f.Freeze(); return f; }

        // Days that left the range shrink away towards the left edge.
        if (morphing)
        {
            var newDates = _days.Select(d => d.Date).ToHashSet();
            for (var j = 0; j < _old.Count; j++)
            {
                if (newDates.Contains(_old[j].Date) || _old[j].Used is not { } u) continue;
                var x = Lerp(j * oldSlot + (oldSlot - oldWidth) / 2, 0, m); var w = Lerp(oldWidth, 0, m); var h = plot * u / max * (1 - m);
                if (w > .5 && h > .5) { dc.PushOpacity(1 - m); dc.DrawRoundedRectangle(Fill(false), null, new Rect(x, Top + plot - h, w, h), Math.Min(6, w / 2), Math.Min(6, w / 2)); dc.Pop(); }
            }
        }
        for (var i = 0; i < _days.Count; i++)
        {
            var d = _days[i];
            var x = i * slot + (slot - width) / 2; var w = width;
            double ease;
            if (morphing)
            {
                if (oldIndex.TryGetValue(d.Date, out var j)) { x = Lerp(j * oldSlot + (oldSlot - oldWidth) / 2, x, m); w = Lerp(oldWidth, width, m); ease = 1; }
                else ease = Math.Clamp(m, 0, 1);
            }
            else { var local = Math.Clamp(grow * 1.6 - i / (double)_days.Count * .6, 0, 1); ease = 1 - Math.Pow(1 - local, 3); }
            if (d.Used is not { } used)
            {
                if (!morphing || m > .6) dc.DrawRoundedRectangle(null, ChartInk.Dashed(Palette.Alpha(t.Faint, .5)), new Rect(x, Top + plot - 10, w, 10), 3, 3);
                continue;
            }
            var h = Math.Max(3, plot * used / max * ease);
            var highlight = _hover == i || Selected == d.Date;
            dc.DrawRoundedRectangle(Fill(highlight), null, new Rect(x, Top + plot - h, w, h), Math.Min(6, w / 2), Math.Min(6, w / 2));
            if (highlight) dc.DrawRoundedRectangle(null, new Pen(Palette.Frozen(Palette.Alpha(t.Ink, .6)), 1), new Rect(x - 1, Top + plot - h - 1, w + 2, h + 2), 6, 6);
            // Markers: a dot for a note, a ring for an unusual day.
            var markerY = Math.Max(4, Top + plot - h - 7); var cx = x + w / 2; var r = Math.Clamp(w / 6, 2, 3.5);
            if (Unusual.ContainsKey(d.Date)) dc.DrawEllipse(null, new Pen(Palette.Frozen(Palette.Warning), 1.4), new Point(cx, markerY), r + 1, r + 1);
            if (Notes.Contains(d.Date)) dc.DrawEllipse(Palette.Frozen(t.Ink), null, new Point(cx, Unusual.ContainsKey(d.Date) ? markerY - 2 * r - 4 : markerY), r, r);
        }
        if (LimitLines && !morphing && max >= .9)
        {
            // One full limit, so bars read as "less than a limit" or "more than a limit" at a glance.
            var full = Top + plot - plot * 1 / max;
            dc.DrawLine(new Pen(Palette.Frozen(Palette.Alpha(t.Ink, .22)), 1), new Point(0, full), new Point(ActualWidth, full));
            var label = ChartInk.Text(this, "1 full limit", 10, t.Faint);
            dc.DrawText(label, new Point(0, Math.Max(0, full - label.Height - 1)));
        }
        if (LimitLines && !morphing && recorded.Length > 0)
        {
            var average = recorded.Average(); var y = Top + plot - plot * average / max;
            dc.DrawLine(ChartInk.Dashed(Palette.Alpha(t.Muted, .8)), new Point(0, y), new Point(ActualWidth, y));
            var label = ChartInk.Text(this, $"average {Ui.Limits(average)}", 10, t.Muted);
            dc.DrawText(label, new Point(ActualWidth - label.Width, Math.Max(0, y - label.Height - 1)));
        }
        if (morphing && m < .5) return;
        var step = _days.Count > 45 ? 14 : _days.Count > 14 ? 7 : 1;
        if (morphing) dc.PushOpacity((m - .5) * 2);
        for (var i = _days.Count - 1; i >= 0; i -= step)
        {
            var text = ChartInk.Text(this, _days.Count > 14 ? _days[i].Date.ToString("MMM d") : _days[i].Date.ToString("ddd"), 10, t.Faint);
            dc.DrawText(text, new Point(Math.Clamp(i * slot + slot / 2 - text.Width / 2, 0, ActualWidth - text.Width), ActualHeight - Bottom + 6));
        }
        if (morphing) dc.Pop();
    }
}

/// <summary>90 days as a calendar of weeks, coloured by how much was used; days without readings are outlined.</summary>
public sealed class CalendarGrid : FrameworkElement
{
    private IReadOnlyList<DayUsage> _days = [];
    private int? _hover;
    public Color Accent { get; set; } = Accents.FromArgb(DesignTokens.Live);
    /// <summary>Days with a note get a small dot in their corner.</summary>
    public IReadOnlySet<DateTime> Notes { get; set; } = new HashSet<DateTime>();
    public event Action<DayUsage>? DayClicked;
    public CalendarGrid()
    {
        Height = 196; Cursor = System.Windows.Input.Cursors.Hand;
        MouseMove += (_, e) => { var i = Hit(e.GetPosition(this)); if (i != _hover) { _hover = i; ToolTip = i is { } k ? $"{_days[k].Date:dddd, MMM d}\n" + (_days[k].Used is { } u ? $"{Ui.Limits(u)} of the limit · {Math.Round(_days[k].Coverage * 100):0}% of the day observed" : "No readings") : null; InvalidateVisual(); } };
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
            if (Notes.Contains(d.Date)) dc.DrawEllipse(Palette.Frozen(t.Ink), new Pen(Palette.Frozen(t.Background), 1), new Point(r.Right - 2.5, r.Top + 2.5), 2.6, 2.6);
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
