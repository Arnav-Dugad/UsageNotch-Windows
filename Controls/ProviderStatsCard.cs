using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UsageNotch.Models;
using UsageNotch.Services;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace UsageNotch.Controls;

public sealed class ProviderStatsCard : Border
{
    private readonly UsageCoordinator _coordinator;
    public string ProviderId { get; }
    public ProviderLogo Logo { get; }
    private ProviderViewModel? Provider => _coordinator.Items.FirstOrDefault(p => p.Id == ProviderId);
    private string? _windowId;
    private LimitWindow? Window => Provider?.Windows.FirstOrDefault(w => w.Id == _windowId);
    private readonly StackPanel _body = new();
    private readonly WrapPanel _windows = new();
    private readonly TextBlock _status = Text("", 11), _usage = Text("", 28), _clock = Text("", 13), _date = Text("", 11), _forecast = Text("", 15), _assumptions = Text("", 11), _coverage = Text("", 11), _notice = Text("", 11), _detail = Text("", 11);
    private readonly Expander _confidence = new() { Margin = new Thickness(0, 8, 0, 8), Foreground = Ink };
    private readonly UsageChart _chart = new() { Height = 225, Margin = new Thickness(0, 12, 0, 0) };
    private readonly UsageChart _overview = new() { Height = 80, Overview = true };
    private readonly StackPanel _insights = new(), _sessions = new(), _patterns = new(), _events = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<UsagePoint> _recent = [], _allOverview = [];
    private int _generation;
    private int _days = 1;
    private DateTimeOffset? _selectedStart, _selectedEnd;
    private string? _account;
    private string? _loadedWindow;
    private static Brush Ink => new SolidColorBrush(Color.FromRgb(229, 234, 244));
    private static TextBlock Text(string text, double size = 12) => new() { Text = text, FontSize = size, Foreground = Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    private Button Action(string label, Action clicked)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 3, 5, 3), Padding = new Thickness(9, 6, 9, 6), FontSize = 11, Foreground = Ink, Background = new SolidColorBrush(Color.FromRgb(35, 42, 56)), BorderBrush = new SolidColorBrush(Color.FromRgb(58, 67, 86)), Cursor = System.Windows.Input.Cursors.Hand };
        button.SetResourceReference(StyleProperty, "SecondaryButton");
        button.Click += (_, _) => clicked(); return button;
    }
    private void Section(string title, UIElement content)
    {
        _body.Children.Add(new Expander { Header = title, Content = content, Foreground = Ink, Margin = new Thickness(0, 9, 0, 2), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch });
    }
    public ProviderStatsCard(UsageCoordinator coordinator, string id)
    {
        _coordinator = coordinator; ProviderId = id;
        _chart.Start = _overview.Start = DateTimeOffset.UtcNow.AddDays(-1);
        _chart.End = _overview.End = DateTimeOffset.UtcNow;
        Background = new SolidColorBrush(Color.FromRgb(21, 26, 35)); BorderBrush = new SolidColorBrush(Color.FromRgb(43, 51, 67));
        BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(14); Padding = new Thickness(18); Margin = new Thickness(0, 0, 10, 12); Child = _body;
        Logo = new ProviderLogo { Provider = id, Colored = true, Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal }; heading.Children.Add(Logo);
        heading.Children.Add(Text(Provider?.DisplayName ?? (id == "claude" ? "Claude" : "Codex"), 20)); _body.Children.Add(heading);
        _body.Children.Add(_status); _body.Children.Add(_windows); _body.Children.Add(_usage); _body.Children.Add(_clock); _body.Children.Add(_date);
        var ranges = new WrapPanel();
        foreach (var (label, days) in new[] { ("24h", 1), ("7d", 7), ("30d", 30), ("All history", 0) })
            ranges.Children.Add(Action(label, () => { _days = days; _selectedStart = _selectedEnd = null; Refresh(); }));
        ranges.Children.Add(Action("Reset period", () =>
        {
            if (_recent.LastOrDefault() is not { } last) return;
            var period = _recent.Where(p => p.Period == last.Period).ToArray();
            Select(period[0].At, last.Reset ?? DateTimeOffset.UtcNow);
        }));
        _body.Children.Add(ranges); _body.Children.Add(_chart);
        _body.Children.Add(Text("Used % · local time   |   Mint: observed   ·   Dashed: estimate\nShading: pace scenarios, not probability. Hover readings; drag to zoom; Ctrl+wheel adjusts zoom.", 10));
        _body.Children.Add(_overview); _body.Children.Add(Text("Overview · drag to choose an interval. All-history view uses sampled extrema; raw readings are retained.", 10));
        _body.Children.Add(_forecast); _confidence.Content = _assumptions; _body.Children.Add(_confidence); _body.Children.Add(_coverage);
        _body.Children.Add(_detail); _body.Children.Add(_insights);
        Section("Observed work sessions", _sessions); Section("Personal patterns · last 28 days", _patterns); Section("Events in this interval", _events);
        _body.Children.Add(_notice);
        _chart.IntervalSelected += Select; _overview.IntervalSelected += Select;
        _chart.EventClicked += e => _detail.Text = $"{e.At.ToLocalTime():MMM d HH:mm:ss} · {e.Kind}\n{e.Explanation}";
        _timer.Tick += (_, _) => Clock();
        SizeChanged += (_, _) => _chart.Height = Math.Clamp(ActualWidth * .44, 180, 300);
        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Unloaded += (_, _) => { _timer.Stop(); _generation++; };
    }
    private void Select(DateTimeOffset start, DateTimeOffset end)
    {
        var earliest = _allOverview.FirstOrDefault()?.At ?? start;
        var latest = Window?.ResetsAt is { } reset && reset > DateTimeOffset.UtcNow ? reset : DateTimeOffset.UtcNow;
        start = start < earliest ? earliest : start; end = end > latest ? latest : end;
        if (end - start < TimeSpan.FromMinutes(1)) return;
        _selectedStart = start; _selectedEnd = end; Refresh();
    }
    public async void Refresh()
    {
        var generation = ++_generation;
        var provider = Provider; var snapshot = provider?.Snapshot;
        _status.Text = provider is null ? "Disabled · enable in Settings → Providers" : provider.StatusPill;
        if (snapshot is null || snapshot.Windows.Count == 0)
        {
            _windows.Children.Clear(); _recent = []; _allOverview = [];
            _chart.Points = []; _chart.Events = []; _chart.Forecast = null; _chart.InvalidateVisual();
            _overview.Points = []; _overview.InvalidateVisual();
            _insights.Children.Clear(); _sessions.Children.Clear(); _patterns.Children.Clear(); _events.Children.Clear();
            _usage.Text = snapshot is null ? "Provider disabled" : "Waiting for provider limits";
            _notice.Text = "No usage is inferred from unavailable readings."; Clock(); return;
        }
        _windows.Children.Clear();
        if (!snapshot.Windows.Any(w => w.Id == _windowId)) _windowId = snapshot.Windows.FirstOrDefault()?.Id;
        foreach (var w in snapshot.Windows)
        {
            var button = Action($"{w.Label}  {(w.UsedFraction is { } u ? $"{u * 100:0.#}%" : "—")}", () => { _windowId = w.Id; _selectedStart = _selectedEnd = null; Refresh(); });
            if (w.Id == _windowId) button.BorderBrush = new SolidColorBrush(Color.FromRgb(117, 224, 191));
            _windows.Children.Add(button);
        }
        var window = Window; if (window is null) { _usage.Text = "Waiting for provider limits"; return; }
        _usage.Text = window.UsedFraction is { } used ? $"{used * 100:0.0}% used · {window.Label}" : window.Label + " · no reading";
        if (_loadedWindow != window.Id) { _loadedWindow = window.Id; _recent = []; _chart.Forecast = null; }
        var now = DateTimeOffset.UtcNow; var days = _days;
        var start = _selectedStart ?? now.AddDays(-(days == 0 ? 30 : days)); var end = _selectedEnd ?? now;
        var selected = _selectedStart is not null; _notice.Text = "Loading local history…";
        var result = await Task.Run(() =>
        {
            lock (_coordinator.History)
            {
                var h = _coordinator.History; var account = h.AccountKey(snapshot);
                var overview = h.ReadOverview(ProviderId, account, window.Id);
                var recent = h.Read(ProviderId, account, window.Id, now.AddDays(-28));
                // Detailed ranges are bounded; broad ranges use SQL extrema instead of loading unlimited rows.
                var broad = !selected && days == 0 || end - start > TimeSpan.FromDays(32);
                var points = broad ? overview : h.Read(ProviderId, account, window.Id, start, end);
                var eventStart = broad && overview.Count > 0 ? overview[0].At : start < now.AddDays(-28) ? start : now.AddDays(-28);
                var events = h.ReadEvents(ProviderId, account, window.Id, eventStart);
                return (account, overview, recent, points, events, error: h.Error, broad);
            }
        });
        if (Dispatcher.HasShutdownStarted) return;
        await Dispatcher.InvokeAsync(() =>
        {
        if (generation != _generation) return;
        _account = result.account; _recent = result.recent; _allOverview = result.overview;
        _chart.Points = result.points;
        _chart.Start = result.broad && !selected ? result.overview.FirstOrDefault()?.At ?? start : start;
        _chart.End = end;
        _chart.Events = result.events.Where(e => e.At >= _chart.Start && e.At <= end).ToArray();
        _overview.Points = result.overview; _overview.Start = result.overview.FirstOrDefault()?.At ?? start; _overview.End = now;
        _overview.SelectionStart = _chart.Start; _overview.SelectionEnd = end; _overview.InvalidateVisual();
        _insights.Children.Clear();
        foreach (var insight in UsageAnalytics.Insights(_recent, now))
        {
            var b = Action("", () => Select(insight.Start, insight.End)); b.Content = Text(insight.Text + "  → Inspect", 11); _insights.Children.Add(b);
        }
        _sessions.Children.Clear();
        _sessions.Children.Add(Text("Observation sessions are separated by gaps over 20 minutes, not detected keyboard activity. Consumption is the sum of observed increases within continuous periods; resets and missing intervals are excluded. Peak is the highest observed interval rate (provider rounding can amplify it).", 11));
        foreach (var s in UsageAnalytics.Sessions(_recent, result.events))
        {
            var b = Action("", () => Select(s.Start, s.End));
            b.Content = Text($"{s.Start.ToLocalTime():MMM d HH:mm}–{s.End.ToLocalTime():HH:mm}\n{ s.Consumption:0.00} percentage points observed · peak {s.PeakPace:0.0} pp/h\n{s.Resets} confirmed resets · {s.Missing} excluded boundaries/gaps", 11); _sessions.Children.Add(b);
        }
        if (_recent.Count < 2) _sessions.Children.Add(Text("Waiting for consecutive observations.", 11));
        _patterns.Children.Clear();
        _patterns.Children.Add(Text("Local hours/days · observed percentage points per covered hour. ≥7 observed days required; each hour needs ≥3 dates and ≥1h coverage, each weekday ≥2 dates and ≥1h. — means insufficient coverage, never zero usage. Between readings, consumption is allocated proportionally across hour boundaries.", 11));
        foreach (var group in new[] { false, true })
        {
            var cells = UsageAnalytics.Patterns(_recent, group);
            var busy = cells.Where(c => c.Pace is not null).OrderByDescending(c => c.Pace).Take(3).ToArray();
            if (busy.Length > 0) _patterns.Children.Add(Text("Busiest observed " + (group ? "days: " : "hours: ") + string.Join(", ", busy.Select(c => c.Label)), 12));
            var panel = new WrapPanel();
            foreach (var c in cells)
            {
                var label = Text($"{c.Label}  {(c.Pace is { } p ? p.ToString("0.0") + " pp/h" : "—")}\n{c.CoveredHours:0.0}h · {c.Days} date(s)", 10);
                label.Width = 107; label.Margin = new Thickness(0, 4, 5, 4); panel.Children.Add(label);
            }
            _patterns.Children.Add(panel);
        }
        _events.Children.Clear();
        foreach (var evt in _chart.Events.TakeLast(100).Reverse())
        { var b = Action("", () => { _detail.Text = evt.Explanation; Select(evt.At.AddMinutes(-30), evt.At.AddMinutes(30)); }); b.Content = Text($"{evt.At.ToLocalTime():MMM d HH:mm} · {evt.Kind}", 11); _events.Children.Add(b); }
        if (_chart.Events.Count == 0) _events.Children.Add(Text("No recorded events in this interval.", 11));
        _notice.Text = result.error ?? (result.broad ? "Overview sampled for display. Select ≤32 days to inspect exact readings. Raw history has no automatic expiry." : $"{result.points.Count:N0} exact readings in view · no automatic history expiry.");
        Clock();
        });
    }
    private void Clock()
    {
        var window = Window; var now = DateTimeOffset.UtcNow;
        _clock.Text = ResetClock.Describe(window?.ResetsAt, now);
        _date.Text = window?.ResetsAt is { } reset ? $"Resets {reset.ToLocalTime():ddd, MMM d yyyy · HH:mm:ss zzz} (local)" : "Exact reset not reported";
        var forecast = UsageForecast.Calculate(_recent, Provider?.Status ?? SnapshotStatus.Error, now);
        _forecast.Text = "Current pace estimate · " + forecast.Summary;
        _confidence.Header = forecast.Confidence + " · assumptions"; _assumptions.Text = forecast.Explanation;
        _coverage.Text = $"{forecast.Samples} forecast readings · {forecast.Coverage:P0} of the 3-hour lookback covered";
        _chart.Forecast = _chart.Points.LastOrDefault()?.At == _recent.LastOrDefault()?.At ? forecast : null;
        if (_selectedStart is null && _days != 0 && forecast.PercentPerHour is not null && window?.ResetsAt is { } at)
            _chart.End = at < now.AddHours(_days == 1 ? 6 : 24) ? at : now.AddHours(_days == 1 ? 6 : 24);
        _chart.InvalidateVisual();
    }
}
