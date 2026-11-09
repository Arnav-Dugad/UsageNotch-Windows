using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageNotch.Models;
using UsageNotch.Services;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace UsageNotch.Controls;

/// <summary>
/// A ring that fills from empty when it appears, after an optional delay, so a row of them fills one after another. Values
/// above 1 draw a second lap in a brighter tone (1.4 = a full ring and 40% more).
/// </summary>
public sealed class ArcRing : FrameworkElement
{
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(ArcRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get; set; }
    public Color Ink { get; set; } = Accents.FromArgb(DesignTokens.Live);
    public double Thickness { get; set; } = 6;
    public TimeSpan Delay { get; set; }
    public bool Reduced { get; set; }

    public ArcRing()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Play();
        Theme.Changed += InvalidateVisual;
    }

    /// <summary>Fills from empty to the value with the "drop" spring's feel; instant with reduced motion.</summary>
    public void Play()
    {
        var target = Math.Clamp(Value, 0, 2);
        BeginAnimation(ShownProperty, null);
        if (Reduced) { SetValue(ShownProperty, target); return; }
        SetValue(ShownProperty, 0d);
        BeginAnimation(ShownProperty, new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(900)) { BeginTime = Delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight); if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2); var radius = size / 2 - Thickness / 2;
        var t = Theme.Current;
        dc.DrawEllipse(null, new Pen(Palette.Frozen(t.Track), Thickness), center, radius, radius);
        var shown = (double)GetValue(ShownProperty);
        Arc(dc, center, radius, Math.Min(1, shown), Ink);
        if (shown > 1) Arc(dc, center, radius, shown - 1, Palette.Lighten(Ink, t.Dark ? .45 : .25));
    }

    private void Arc(DrawingContext dc, Point center, double radius, double fraction, Color ink)
    {
        if (fraction <= .002) return;
        var pen = new Pen(Palette.Frozen(ink), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (fraction >= .999) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = fraction * 2 * Math.PI;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            c.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)), new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>A number that counts up from zero when it appears, formatted by <see cref="Format"/> on every frame.</summary>
public sealed class CountUpText : TextBlock
{
    private static readonly DependencyProperty NumberProperty = DependencyProperty.Register("Number", typeof(double), typeof(CountUpText),
        new PropertyMetadata(0d, (d, e) => ((CountUpText)d).Text = ((CountUpText)d).Format((double)e.NewValue)));
    public Func<double, string> Format { get; set; } = v => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
    public double Target { get; set; }
    public TimeSpan Delay { get; set; }
    public bool Reduced { get; set; }

    public CountUpText() => Loaded += (_, _) => Play();

    public void Play()
    {
        BeginAnimation(NumberProperty, null);
        if (Reduced || !double.IsFinite(Target)) { Text = Format(Target); return; }
        SetValue(NumberProperty, 0d); Text = Format(0);
        BeginAnimation(NumberProperty, new DoubleAnimation(0, Target, TimeSpan.FromMilliseconds(950)) { BeginTime = Delay, EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
    }
}

/// <summary>Overview's usage score: one ring for every provider, what it means, last week's number and each provider's share.</summary>
public static class ScoreCard
{
    public static Border Build(UsageScore score, IReadOnlyList<ProviderViewModel> items, bool reduced)
    {
        var ink = Palette.Usage(score.Value / 100d);
        var ring = new ArcRing { Width = 112, Height = 112, Value = score.Value / 100d, Ink = ink, Thickness = 10, Reduced = reduced };
        var number = new CountUpText { Target = score.Value, Reduced = reduced, FontSize = 34, FontWeight = FontWeights.Light, FontFamily = Ui.Display, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        number.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        var dial = new Grid { Width = 112, Height = 112, Margin = new Thickness(0, 0, 22, 0) }; dial.Children.Add(ring); dial.Children.Add(number);

        var title = Ui.Row(10, Ui.Text("Usage score", DesignTokens.TypeScale.Headline, FontWeights.SemiBold, "Ui.Text", false), Ui.Pill(UsageScore.Word(score.Value), ink, 11));
        var trend = score.Previous is { } p
            ? (score.Value - p) switch { > 0 and var d => $"▲ {d} from {p} last week", < 0 and var d => $"▼ {-d} from {p} last week", _ => $"Same as last week ({p})" }
            : "Last week's score appears after a full week of readings.";
        var explain = Ui.Text("The average of how much of each provider's weekly limit you've used (its main limit when it has no weekly one).", DesignTokens.TypeScale.Caption, FontWeights.Normal, "Ui.Faint");
        explain.Margin = new Thickness(0, 4, 0, 10);
        var rows = new WrapPanel();
        foreach (var e in score.Providers)
        {
            var bar = new UsageBar { Width = 90, Height = 5, Value = e.Used, Used = e.Used, Reduced = reduced, VerticalAlignment = VerticalAlignment.Center };
            var cell = Ui.Row(8, Ui.Logo(e.Id, 15), Ui.Text(e.Name, 12, FontWeights.SemiBold, "Ui.Text", false), bar, Ui.Text(Ui.Percent(e.Used), 12, FontWeights.Normal, "Ui.Muted", false));
            cell.Margin = new Thickness(0, 0, 22, 6); cell.ToolTip = $"{e.Label}: {Ui.Percent(e.Used)} used";
            rows.Children.Add(cell);
        }
        var text = Ui.Stack(title, Ui.Text(trend, DesignTokens.TypeScale.Callout, FontWeights.Normal, "Ui.Muted"), explain, rows);
        text.VerticalAlignment = VerticalAlignment.Center;
        var body = Ui.Columns((dial, GridLength.Auto), (text, new GridLength(1, GridUnitType.Star)));
        return Ui.Card(body, ink, DesignTokens.Radius.Card, new Thickness(22, 18, 22, 16));
    }
}

/// <summary>A provider's plans over time: a dot per plan, newest last.</summary>
public static class PlanTimeline
{
    public static UIElement? Build(IReadOnlyList<PlanChange> plans, Color accent, bool use24)
    {
        if (plans.Count == 0) return null;
        var stack = new StackPanel();
        for (var i = 0; i < plans.Count; i++)
        {
            var p = plans[i]; var latest = i == plans.Count - 1;
            var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Palette.Frozen(latest ? accent : Palette.Alpha(accent, .45)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
            var line = new Border { Width = 2, Background = Palette.Frozen(Palette.Alpha(accent, .3)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, -4), Visibility = latest ? Visibility.Collapsed : Visibility.Visible };
            var marker = new Grid { Width = 12, Margin = new Thickness(0, 0, 14, 0) }; marker.Children.Add(line); marker.Children.Add(dot);
            var when = p.At.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
            var text = Ui.Stack(Ui.Text(p.Plan + (latest ? "  ·  now" : ""), 13, latest ? FontWeights.SemiBold : FontWeights.Normal, "Ui.Text", false),
                Ui.Text(i == 0 ? $"since {when} (when UsageNotch first saw it)" : $"from {when}", 11.5, FontWeights.Normal, "Ui.Muted", false));
            var row = Ui.Columns((marker, GridLength.Auto), (text, new GridLength(1, GridUnitType.Star))); row.Margin = new Thickness(0, 0, 0, latest ? 0 : 12);
            stack.Children.Add(row);
        }
        return stack;
    }
}

/// <summary>Eight weeks of tokens as stacked bars, one colour per model, with a legend.</summary>
public sealed class ModelMixChart : FrameworkElement
{
    public IReadOnlyList<ModelWeek> Weeks { get; set; } = [];
    public Color Accent { get; set; } = Palette.Accent;
    public bool Reduced { get; set; }
    private static readonly DependencyProperty GrowProperty = DependencyProperty.Register("Grow", typeof(double), typeof(ModelMixChart), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public ModelMixChart()
    {
        Height = 150; Theme.Changed += InvalidateVisual;
        Loaded += (_, _) => { if (!Reduced) BeginAnimation(GrowProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }); };
    }

    /// <summary>The models in the chart, biggest first and "Other" last, each with its colour.</summary>
    public IReadOnlyList<(string Name, Color Color)> Legend()
    {
        var names = Weeks.SelectMany(w => w.Models).GroupBy(m => m.Name).Select(g => (Name: g.Key, Total: g.Sum(m => m.Total)))
            .OrderBy(x => x.Name == "Other").ThenByDescending(x => x.Total).Select(x => x.Name).ToList();
        var t = Theme.Current;
        // The provider's colour first, then clearly different hues from the shared accent choices.
        var hues = new[] { Accent }.Concat(DesignTokens.AccentChoices.Select(Accents.FromArgb).Where(c => Distance(c, Accent) > 120)).Distinct().ToArray();
        return names.Select((n, i) => (n, n == "Other" ? Palette.Alpha(t.Faint, .7) : hues[i % hues.Length])).ToList();
    }

    private static double Distance(Color a, Color b) => Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(RenderSize));
        if (Weeks.Count == 0 || ActualWidth < 20) return;
        var t = Theme.Current; var legend = Legend().ToDictionary(x => x.Name, x => x.Color);
        const double bottom = 20; var plot = ActualHeight - bottom - 6; var slot = ActualWidth / Weeks.Count; var width = Math.Min(38, slot * .6);
        var max = Math.Max(1, Weeks.Max(w => w.Models.Sum(m => m.Total))); var grow = (double)GetValue(GrowProperty);
        for (var i = 0; i < Weeks.Count; i++)
        {
            var x = i * slot + (slot - width) / 2; var y = 6 + plot;
            foreach (var m in Weeks[i].Models)
            {
                var h = plot * m.Total / max * grow; if (h < .5) continue;
                y -= h;
                dc.DrawRoundedRectangle(Palette.Frozen(legend.GetValueOrDefault(m.Name, t.Faint)), null, new Rect(x, y, width, Math.Max(1, h - 1)), 3, 3);
            }
            var label = new FormattedText(Weeks[i].Start.ToString("MMM d", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(Ui.Body, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, Palette.Frozen(t.Faint), 1) { TextAlignment = TextAlignment.Center, MaxTextWidth = slot };
            dc.DrawText(label, new Point(i * slot, ActualHeight - 15));
        }
        ToolTip = string.Join("\n", Weeks.Select(w => $"Week of {w.Start:MMM d}: {WeeklyReport.Tokens(w.Models.Sum(m => m.Total))} tokens" + (w.Models.FirstOrDefault() is { } top ? $", mostly {top.Name}" : "")));
    }
}
