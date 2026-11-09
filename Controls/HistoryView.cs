using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageNotch.Models;
using UsageNotch.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace UsageNotch.Controls;

/// <summary>
/// History, as on Android: "This week" in plain language, then pick a provider and limit for 7, 30 or 90 days of daily usage
/// with bars that grow in (click one to open that day), totals, a 90-day calendar, all-time totals, your providers side by
/// side, your busiest hours, insights and local tokens.
/// Days and hours without readings are shown as no data, never as zero.
/// </summary>
public sealed class HistoryView : Grid
{
    private UsageCoordinator? _coordinator;
    private readonly StackPanel _page = new() { MaxWidth = 1180, Margin = new Thickness(28, 8, 28, 40) };
    private readonly WrapPanel _providerChips = new();
    private readonly WrapPanel _windowChips = new();
    private readonly StackPanel _body = new();
    private GlassTabBar? _range;
    private string? _provider, _window;
    private int _days = 7;
    private DateTime? _openDay;
    private int _generation;

    public HistoryView()
    {
        _page.Children.Add(Ui.Text("History", 30, FontWeights.SemiBold, "Ui.Text", false));
        var subtitle = Ui.Text("What you used, day by day, from the readings this PC keeps until you delete them.", 13, FontWeights.Normal, "Ui.Muted"); subtitle.Margin = new Thickness(0, 4, 0, 16);
        _page.Children.Add(subtitle);
        _page.Children.Add(_providerChips);
        _page.Children.Add(_windowChips);
        _page.Children.Add(_body);
        Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _page });
        Loaded += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        Theme.Changed += () => { if (IsLoaded) Refresh(); };
    }

    private bool Reduced => _coordinator is null || Motion.IsReduced(_coordinator.Settings);

    public void Connect(UsageCoordinator coordinator) => _coordinator = coordinator;

    public async void Refresh()
    {
        if (_coordinator is null) return;
        Ui.EnsureContext(this);
        var generation = ++_generation;
        var items = _coordinator.Items.Where(i => i.SessionWindow is not null || i.Windows.Count > 0).ToList(); var s = _coordinator.Settings;
        if (items.Count == 0) { _body.Children.Clear(); _body.Children.Add(Ui.Card(Ui.Text("History appears once a provider reports usage.", 13, FontWeights.Normal, "Ui.Muted"))); return; }
        var provider = items.FirstOrDefault(i => i.Id == _provider) ?? items[0]; _provider = provider.Id;
        var windows = new[] { provider.SessionWindow, provider.WeeklyWindow }.Where(w => w is { UsedFraction: not null }).Distinct().Select(w => w!).ToList();
        if (windows.Count == 0) windows = provider.Windows.Where(w => w.UsedFraction is not null).Take(2).ToList();
        var window = windows.FirstOrDefault(w => w.Id == _window) ?? windows.FirstOrDefault(); _window = window?.Id;

        _providerChips.Children.Clear();
        foreach (var p in items) { var id = p.Id; _providerChips.Children.Add(Ui.Chip(p.DisplayName, p.Id == provider.Id, () => { _provider = id; _window = null; _openDay = null; Refresh(); }, Ui.Logo(p.Id, 15, s.ColoredLogos), Reduced)); }
        _windowChips.Children.Clear();
        foreach (var w in windows) { var id = w.Id; _windowChips.Children.Add(Ui.Chip(w.Label, w.Id == window?.Id, () => { _window = id; _openDay = null; Refresh(); }, null, Reduced)); }
        _range ??= MakeRange();
        if (_range.Parent is null) _windowChips.Children.Add(_range);
        else if (_range.Parent != _windowChips) { ((System.Windows.Controls.Panel)_range.Parent).Children.Remove(_range); _windowChips.Children.Add(_range); }
        else { _windowChips.Children.Remove(_range); _windowChips.Children.Add(_range); }

        if (window is null) { _body.Children.Clear(); return; }
        var accent = provider.AccentColor;
        var data = await Task.Run(() =>
        {
            var summary = _coordinator.SummaryFor(provider.Snapshot, window);
            var others = items.Where(p => p.SessionWindow is not null).Select(p => (p, _coordinator.SummaryFor(p.Snapshot, p.SessionWindow!))).ToList();
            var otherWindows = provider.Windows.Where(w => w.UsedFraction is not null && w.Id != window.Id).Select(w => (w, _coordinator.SummaryFor(provider.Snapshot, w))).ToList();
            var tokens = _coordinator.TokenReports();
            var report = _coordinator.WeekReportFor();
            var allTime = _coordinator.AllTimeFor(provider.Snapshot, window);
            return (summary, others, otherWindows, tokens, report, allTime);
        });
        if (generation != _generation) return;
        _body.Children.Clear();
        var index = 0;
        void Add(UIElement e) { _body.Children.Add(e); Motion.Rise(e, index++, Reduced); }
        if (data.report is { } week) Add(ReportView.Card(week, items, s, Reduced, Share));
        if (data.summary is not { } sum)
        {
            Add(Ui.Card(Ui.Stack(Ui.Text("No history yet", 20, FontWeights.Light), Ui.Text("UsageNotch records readings while it runs. Daily totals appear after a few readings.", 12.5, FontWeights.Normal, "Ui.Muted"))));
        }
        else
        {
            var days = sum.Calendar.TakeLast(_days).ToList();
            // Plain language: one full limit is 100%; more than that is shown as a multiple (1.6× = one and a bit limits' worth).
            var tiles = new UniformGrid2(4);
            var total = sum.Total(_days); var recorded = sum.Recorded(_days); var busiest = sum.Busiest(_days);
            var limitName = WeeklyReport.Lower(window.Label);
            tiles.Add(Tile($"Used in {_days} days", Ui.Limits(total), total is >= Ui.MultipleFrom ? $"{total:0.#} full {limitName} limits' worth" : $"of one {limitName} limit"));
            tiles.Add(Tile("On an average day", total is { } t && recorded > 0 ? Ui.Limits(t / recorded) : "—", recorded > 0 ? $"of the limit, over {recorded} day{(recorded == 1 ? "" : "s")} with readings" : "no readings yet"));
            tiles.Add(Tile("Busiest day", busiest is { } b ? b.Date.ToString("ddd, MMM d") : "—", busiest is { Used: { } bu } ? $"{Ui.Limits(bu)} of the limit" : "no usage recorded"));
            tiles.Add(Tile("Streak", sum.Streak > 0 ? $"{sum.Streak} day{(sum.Streak == 1 ? "" : "s")}" : "—", "in a row with usage"));
            Add(tiles.Panel);

            var bars = new DailyBars { Accent = accent, Reduced = Reduced, Selected = _openDay };
            var dayCard = new Border();
            bars.DayClicked += day => { _openDay = _openDay == day.Date ? null : day.Date; bars.Selected = _openDay; bars.InvalidateVisual(); ShowDay(dayCard, day, sum, data.otherWindows, provider, window); };
            bars.Days = days;
            var coverage = sum.Coverage(_days);
            var trust = Ui.Text($"This PC observed {Math.Round(coverage * 100):0}% of these {_days} days. Usage while it was off or asleep is still counted, on the day it next read your usage, so totals stay complete; only the hour it happened is unknown.", 11.5, FontWeights.Normal, "Ui.Faint");
            trust.Margin = new Thickness(0, 6, 0, 0);
            Add(Ui.Card(Ui.Stack(Title("\uE9D2", "Daily usage", window.Label + " · 100% = one full limit"), bars, trust, dayCard), accent));
            if (_openDay is { } open && days.FirstOrDefault(d => d.Date == open) is { } openDay) ShowDay(dayCard, openDay, sum, data.otherWindows, provider, window);

            var calendar = new CalendarGrid { Accent = accent, Days = sum.Calendar };
            var calendarDay = new Border();
            calendar.DayClicked += day => ShowDay(calendarDay, day, sum, data.otherWindows, provider, window);
            Add(Ui.Card(Ui.Stack(Title("\uE787", "90 days", "outlined days had no readings"), calendar, calendarDay), accent));
            if (data.allTime is { } all) Add(ReportView.AllTime(all, window, accent));

            var series = data.others.Where(o => o.Item2 is not null).Select(o => new CompareChart.Series(o.p.DisplayName, o.p.AccentColor, o.Item2!.Calendar.TakeLast(_days).ToArray())).ToList();
            if (series.Count > 1)
            {
                var chart = new CompareChart { Reduced = Reduced, Data = series };
                var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var x in series) legend.Children.Add(Ui.Row(7, new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Palette.Frozen(x.Color), VerticalAlignment = VerticalAlignment.Center }, Ui.Text(x.Name, 12, FontWeights.Normal, "Ui.Muted", false)).WithMargin(new Thickness(0, 0, 16, 0)));
                Add(Ui.Card(Ui.Stack(Title("\uE9F9", "Side by side", "each provider's main limit"), chart, legend)));
            }

            var heat = new HeatGrid { Accent = accent, Use24 = s.Use24HourTime }; heat.Set(sum.Heat, sum.Observed);
            var busy = sum.BusiestHours();
            var busyText = Ui.Text(busy.Count == 0 ? "Your busiest hours appear after a few days of readings." : "Busiest: " + string.Join(", ", busy.Select(b => $"{(DayOfWeek)b.Weekday} {BudgetSetting.Clock(b.Hour * 60, s.Use24HourTime)}")), 12.5, FontWeights.Normal, "Ui.Muted");
            busyText.Margin = new Thickness(0, 0, 0, 10);
            Add(Ui.Card(Ui.Stack(Title("\uE916", "Busy hours", "last 30 days"), busyText, heat), accent));

            var insights = new StackPanel();
            ProviderDetailView.Insights(insights, sum, window, s);
            Add(Ui.Card(Ui.Stack(Title("\uE9D9", "Insights", window.Label), insights), accent));
        }
        foreach (var report in data.tokens)
            Add(Ui.Card(Ui.Stack(Title("\uE8EF", report.Label + " tokens", "this PC"), TokenViews.Summary(report, _coordinator.Items.FirstOrDefault(p => p.Id == report.Source)?.AccentColor ?? accent, Reduced)), _coordinator.Items.FirstOrDefault(p => p.Id == report.Source)?.AccentColor));
        if (data.tokens.Count == 0 && s.TrackLocalTokens && _coordinator.Tokens.Status is { Length: > 0 } status)
            Add(Ui.Card(Ui.Stack(Title("\uE8EF", "Tokens", null), Ui.Text(status, 12.5, FontWeights.Normal, "Ui.Muted"))));
    }

    private GlassTabBar MakeRange()
    {
        var bar = new GlassTabBar(["7 days", "30 days", "90 days"], _days switch { 7 => 0, 30 => 1, _ => 2 }, 32, 12) { Reduced = Reduced, Margin = new Thickness(4, 0, 0, 8), VerticalAlignment = VerticalAlignment.Top };
        bar.SelectionChanged += i => { _days = i switch { 0 => 7, 1 => 30, _ => 90 }; _openDay = null; Refresh(); };
        return bar;
    }

    /// <summary>Renders "My week in AI", saves it to Pictures and copies it, then says where it went.</summary>
    private async void Share(TextBlock status)
    {
        if (_coordinator is null) return;
        status.Text = "Making your image…";
        var coordinator = _coordinator; var items = coordinator.ItemsSnapshot;
        var (summaries, report, tokens) = await Task.Run(() =>
        {
            var map = new Dictionary<(string, string), WindowSummary?>();
            foreach (var p in items)
                foreach (var w in new[] { p.SessionWindow, p.WeeklyWindow }.Where(w => w is not null)) map[(p.Id, w!.Id)] = coordinator.SummaryFor(p.Snapshot, w!);
            return (map, coordinator.WeekReportFor(), coordinator.TokenReports());
        });
        try
        {
            var image = ShareWeekCard.Render(items, (p, w) => summaries.GetValueOrDefault((p.Id, w.Id)), report, tokens, coordinator.Settings, Theme.Current.Dark, coordinator.Settings.ClassicStyle);
            var (file, message) = ShareWeekCard.SaveAndCopy(image);
            status.Text = message;
            if (file is not null)
            {
                status.Cursor = System.Windows.Input.Cursors.Hand; status.TextDecorations = TextDecorations.Underline; status.ToolTip = "Show in folder";
                status.MouseLeftButtonUp += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { } };
            }
        }
        catch (Exception e) { status.Text = "Couldn't make the image: " + e.Message; }
    }

    private static Grid Tile(string label, string value, string detail)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 12, 12) };
        grid.Children.Add(Ui.Card(Ui.Stack(Ui.Text(label, 11.5, FontWeights.SemiBold, "Ui.Faint", false), Ui.Text(value, 26, FontWeights.Light, "Ui.Text", false), Ui.Text(detail, 11.5, FontWeights.Normal, "Ui.Muted", false)), null, 22, new Thickness(16, 12, 16, 14)));
        ((Border)grid.Children[0]).Margin = new Thickness(0);
        return grid;
    }

    private static Grid Title(string glyph, string text, string? trailing)
    {
        var row = Ui.Columns((Ui.Icon(glyph, 15, "Ui.Muted", new Thickness(0, 0, 10, 0)), GridLength.Auto), (Ui.Text(text, 14, FontWeights.SemiBold, "Ui.Text", false), new GridLength(1, GridUnitType.Star)));
        if (trailing is not null) { var t = Ui.Text(trailing, 11.5, FontWeights.Normal, "Ui.Faint", false); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetColumn(t, 2); row.Children.Add(t); }
        row.Margin = new Thickness(0, 0, 0, 12);
        return row;
    }

    /// <summary>The day card: that day's usage against your average and rank, and what your other limits used.</summary>
    private void ShowDay(Border host, DayUsage day, WindowSummary sum, IReadOnlyList<(LimitWindow w, WindowSummary? Summary)> others, ProviderViewModel p, LimitWindow window)
    {
        if (host.Child is FrameworkElement shown && shown.Tag is DateTime date && date == day.Date) { host.Child = null; return; }
        var recorded = sum.Calendar.Where(d => d.Used is not null).ToList();
        var average = recorded.Count > 0 ? recorded.Average(d => d.Used!.Value) : (double?)null;
        var rank = day.Used is { } u ? recorded.Count(d => d.Used > u) + 1 : (int?)null;
        var facts = new UniformGrid2(3);
        facts.Add(Fact("Used", Ui.Limits(day.Used), $"of the {window.Label.ToLowerInvariant()} limit · {Math.Round(day.Coverage * 100):0}% of the day observed"));
        facts.Add(Fact("Compared with usual", average is { } a && day.Used is { } du && a > .0001 ? (du >= a ? $"{du / a:0.0}× more" : $"{Math.Round((1 - du / a) * 100):0}% less") : "—", average is { } av ? $"than your {Ui.Limits(av)} average" : "no average yet"));
        facts.Add(Fact("Rank", rank is { } r ? $"#{r}" : "—", $"of {recorded.Count} recorded days"));
        var stack = Ui.Stack(Ui.Text(day.Date.ToString("dddd, MMMM d"), 18, FontWeights.SemiBold), facts.Panel);
        foreach (var (w, summary) in others)
        {
            var other = summary?.Calendar.FirstOrDefault(d => d.Date == day.Date);
            var row = Ui.Columns((Ui.Text(w.Label, 12.5, FontWeights.Normal, "Ui.Muted", false), new GridLength(1, GridUnitType.Star)), (Ui.Text(other?.Used is { } ou ? Ui.Percent(ou) : "no readings", 12.5, FontWeights.SemiBold, "Ui.Text", false), GridLength.Auto));
            row.Margin = new Thickness(0, 6, 0, 0); stack.Children.Add(row);
        }
        var card = Ui.Inner(stack, new Thickness(16, 14, 16, 14)); card.Margin = new Thickness(0, 14, 0, 0); card.Tag = day.Date;
        host.Child = card;
        // The day card grows out of the chart.
        if (!Reduced)
        {
            var scale = new ScaleTransform(.94, .6); card.RenderTransformOrigin = new System.Windows.Point(.5, 0); card.RenderTransform = scale; card.Opacity = 0;
            Motion.Spring(scale, ScaleTransform.ScaleXProperty, 1, false, .72, 560); Motion.Spring(scale, ScaleTransform.ScaleYProperty, 1, false, .72, 560);
            Motion.To(card, OpacityProperty, 1, 220, false);
        }
    }

    private static Grid Fact(string label, string value, string note)
    {
        var g = new Grid { Margin = new Thickness(0, 10, 12, 0) };
        g.Children.Add(Ui.Stack(Ui.Text(label, 11, FontWeights.SemiBold, "Ui.Faint", false), Ui.Text(value, 22, FontWeights.Light, "Ui.Text", false), Ui.Text(note, 11, FontWeights.Normal, "Ui.Muted", false)));
        return g;
    }
}

internal static class ElementExtensions
{
    public static T WithMargin<T>(this T element, Thickness margin) where T : FrameworkElement { element.Margin = margin; return element; }
}
