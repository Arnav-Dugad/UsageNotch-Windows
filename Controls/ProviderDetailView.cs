using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageNotch.Models;
using UsageNotch.Services;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace UsageNotch.Controls;

/// <summary>
/// One provider, full screen, as on Android: a large ring that grows out of the card you clicked, the pace, the best time to
/// start, budgets for every limit, the usage explorer, insights from your history, local tokens and its accent colour.
/// </summary>
public sealed class ProviderDetailView : Grid
{
    private readonly UsageCoordinator _coordinator;
    private readonly ProviderViewModel _p;
    private readonly bool _reduced;
    private readonly Action<Action<AppSettings>> _persist;
    private readonly Grid _ringHost;
    private readonly WrapPanel _cards = new();
    private readonly List<ProviderVisuals.LimitRow> _rows = [];
    private readonly RollingNumber _headline;
    private readonly TextBlock _caption;
    private readonly TextBlock _reset;
    public ProviderStatsCard Explorer { get; }
    public event Action? Back;
    private int _cardIndex;

    public ProviderDetailView(UsageCoordinator coordinator, ProviderViewModel p, bool reduced, Action<Action<AppSettings>> persist)
    {
        _coordinator = coordinator; _p = p; _reduced = reduced; _persist = persist;
        var s = p.Settings;
        var back = Ui.Button("", () => Back?.Invoke(), false, "\uE72B", reduced, "Back to Overview");
        var title = Ui.Text(p.DisplayName, 22, FontWeights.SemiBold, "Ui.Text", false); title.Margin = new Thickness(10, 0, 10, 0); title.VerticalAlignment = VerticalAlignment.Center;
        var top = Ui.Row(12, back, Ui.Logo(p.Id, 24, s.ColoredLogos), title, Ui.Pill(s.ReviewSession ? "Preview" : p.StatusPill, ProviderVisuals.StatusColor(p)));
        if (p.Plan.Length > 0) top.Children.Insert(3, Ui.PlanBadge(p.Plan));
        if (p.HasManageUrl) top.Children.Add(Ui.Button(p.DashboardLabel, () => ProviderVisuals.OpenUrl(p.Snapshot.ManageUrl!), false, "\uE8A7", reduced));
        foreach (UIElement child in top.Children) if (child is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;

        _ringHost = ProviderVisuals.Ring(p, 236, reduced);
        _ringHost.HorizontalAlignment = HorizontalAlignment.Center;
        _ringHost.RenderTransformOrigin = new Point(.5, .5);
        _headline = new RollingNumber { Text = ProviderVisuals.Headline(p), FontSize = 52, FontWeight = FontWeights.Light, Reduced = reduced, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        _headline.SetResourceReference(RollingNumber.ForegroundProperty, "Ui.Text");
        var session = p.PrimaryWindow;
        var renewed = session?.ResetsAt is { } r0 && r0 <= DateTimeOffset.Now;
        _caption = Ui.Text(session is null ? p.HeadlineLabel : renewed ? $"{session.Label} renewed" : $"{session.Label} {(p.ShowRemaining ? "remaining" : "used")}", 13.5, FontWeights.Normal, "Ui.Muted", false);
        _caption.HorizontalAlignment = HorizontalAlignment.Center;
        _reset = Ui.Text("", 12.5, FontWeights.Normal, "Ui.Faint", false); _reset.HorizontalAlignment = HorizontalAlignment.Center; _reset.Margin = new Thickness(0, 4, 0, 0);
        var hero = Ui.Stack(_ringHost, _headline, _caption, _reset);
        if (p.Dual || p.WeeklyWindow is not null && p.WeeklyWindow != p.PrimaryWindow)
        {
            var secondary = Ui.Text($"{(ProviderKind.HasWeekly(p.Id) ? "7d" : "2nd")} {Ui.Percent(p.WeeklyWindow?.UsedFraction is { } wu ? (p.ShowRemaining ? 1 - wu : wu) : null)}{(p.ShowRemaining ? " left" : "")} · inner ring", 12, FontWeights.Normal, "Ui.Faint", false);
            secondary.HorizontalAlignment = HorizontalAlignment.Center; secondary.Margin = new Thickness(0, 2, 0, 0); hero.Children.Add(secondary);
        }
        hero.Margin = new Thickness(0, 26, 0, 24);

        Explorer = new ProviderStatsCard(coordinator, p.Id, embedded: true);
        var page = Ui.Stack(top, hero, _cards);
        page.MaxWidth = 1180; page.Margin = new Thickness(28, 8, 28, 40);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = page };
        Children.Add(scroll);
        _cards.SizeChanged += (_, _) => Layout();
        Build();
        Tick();
    }

    /// <summary>Grows the ring out of <paramref name="origin"/> (the card or ring you clicked) and lets the cards rise in.</summary>
    public void Enter(FrameworkElement? origin)
    {
        Motion.Rise(_headline, 1, _reduced, 10); Motion.Rise(_caption, 2, _reduced, 10); Motion.Rise(_reset, 2, _reduced, 10);
        if (_reduced || origin is null || PresentationSource.FromVisual(origin) is null) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (PresentationSource.FromVisual(_ringHost) is null || PresentationSource.FromVisual(origin) is null) return;
            var from = origin.TranslatePoint(new Point(origin.ActualWidth / 2, origin.ActualHeight / 2), this);
            var to = _ringHost.TranslatePoint(new Point(_ringHost.ActualWidth / 2, _ringHost.ActualHeight / 2), this);
            var scale = Math.Clamp(origin.ActualWidth / Math.Max(1, _ringHost.ActualWidth), .1, 1);
            var move = new TranslateTransform(from.X - to.X, from.Y - to.Y); var size = new ScaleTransform(scale, scale);
            _ringHost.RenderTransform = new TransformGroup { Children = { size, move } };
            Motion.Spring(move, TranslateTransform.XProperty, 0, false, .78, 760); Motion.Spring(move, TranslateTransform.YProperty, 0, false, .78, 760);
            Motion.Spring(size, ScaleTransform.ScaleXProperty, 1, false, .74, 760); Motion.Spring(size, ScaleTransform.ScaleYProperty, 1, false, .74, 760);
        });
    }

    private void Add(UIElement card, bool wide = false)
    {
        if (card is FrameworkElement fe) fe.Tag = wide ? "wide" : null;
        _cards.Children.Add(card);
        Motion.Rise(card, _cardIndex++ + 2, _reduced);
    }

    private void Layout()
    {
        const double gap = 14;
        var width = Math.Max(320, _cards.ActualWidth);
        var columns = width >= 900 ? 2 : 1;
        var half = Math.Floor((width - gap) / 2) - 1;
        var column = 0;
        foreach (FrameworkElement card in _cards.Children)
        {
            var wide = card.Tag as string == "wide" || columns == 1;
            if (wide) { card.Width = width - 1; card.Margin = new Thickness(0, 0, 0, gap); column = 0; continue; }
            card.Width = half;
            card.Margin = new Thickness(0, 0, column == 0 ? gap : 0, gap);
            column = 1 - column;
        }
    }

    private static TextBlock CardTitle(string glyph, string text, StackPanel into, string? trailing = null)
    {
        var title = Ui.Text(text, 14, FontWeights.SemiBold, "Ui.Text", false);
        var row = Ui.Columns((Ui.Icon(glyph, 15, "Ui.Muted", new Thickness(0, 0, 10, 0)), GridLength.Auto), (title, new GridLength(1, GridUnitType.Star)));
        if (trailing is not null) { var t = Ui.Text(trailing, 11.5, FontWeights.Normal, "Ui.Faint", false); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetColumn(t, 2); row.Children.Add(t); }
        row.Margin = new Thickness(0, 0, 0, 12);
        into.Children.Add(row);
        return title;
    }

    private void Build()
    {
        var p = _p; var s = p.Settings;
        foreach (var incident in _coordinator.Health.For(p.Id)?.Incidents ?? []) Add(ProviderVisuals.Incident(incident, s), true);

        // Pace
        var pace = new StackPanel(); CardTitle("\uEC4A", "Pace", pace);
        var paceSummary = Ui.Text("Reading your recent pace…", 20, FontWeights.Light);
        var paceDetail = Ui.Text("", 12, FontWeights.Normal, "Ui.Muted"); paceDetail.Margin = new Thickness(0, 6, 0, 0);
        pace.Children.Add(paceSummary); pace.Children.Add(paceDetail);
        Add(Ui.Card(pace, p.AccentColor));

        // Best time to start
        var start = new StackPanel(); CardTitle("\uE823", "Best time to start", start);
        var startText = Ui.Text("Needs about a week of history from this PC.", 20, FontWeights.Light);
        var startDetail = Ui.Text("A session started then renews in the middle of your usual busy hours, so you get a fresh limit when you need it most.", 12, FontWeights.Normal, "Ui.Muted"); startDetail.Margin = new Thickness(0, 6, 0, 0);
        start.Children.Add(startText); start.Children.Add(startDetail);
        Add(Ui.Card(start, p.AccentColor));

        // Usage explorer (the desktop's deep chart)
        var explore = new StackPanel(); CardTitle("\uE9D2", "Usage explorer", explore, "Drag to zoom · Ctrl+wheel");
        explore.Children.Add(Explorer);
        Add(Ui.Card(explore, p.AccentColor), true);

        // Limits
        var limits = new StackPanel(); CardTitle("\uE9F5", "Limits", limits);
        if (!string.IsNullOrWhiteSpace(p.StatusText)) { var st = Ui.Text(p.StatusText, 12, FontWeights.Normal, "Ui.Muted"); st.Margin = new Thickness(2, 0, 2, 8); limits.Children.Add(st); }
        var i = 0;
        foreach (var w in p.Windows)
        {
            var row = ProviderVisuals.Limit(p, w, i++, _reduced); ((FrameworkElement)row.View).Margin = new Thickness(0, 0, 0, 8);
            _rows.Add(row); limits.Children.Add(row.View);
        }
        if (p.Windows.Count == 0) limits.Children.Add(Ui.Text("No limits reported yet.", 12.5, FontWeights.Normal, "Ui.Muted"));
        Add(Ui.Card(limits, p.AccentColor));

        // Where this week's usage went, when the provider says (Claude: Claude Code, chat, …).
        if (p.Snapshot.Breakdown is { Count: > 0 } breakdown)
        {
            var shares = new StackPanel(); CardTitle("\uE9F9", "Where this week went", shares, p.WeeklyWindow?.Label);
            shares.Children.Add(ShareList.Build(breakdown.Select(b => (b.Name, b.Percent / 100, $"{b.Percent:0.#}%")), p.AccentColor, _reduced));
            var note = Ui.Text("Shares of this week's usage, as " + p.DisplayName + " reports them.", 11.5, FontWeights.Normal, "Ui.Faint"); note.Margin = new Thickness(0, 2, 0, 0);
            shares.Children.Add(note);
            Add(Ui.Card(shares, p.AccentColor));
        }

        // Budgets for every limit
        Add(BudgetsCard());

        // Insights
        var insights = new StackPanel(); CardTitle("\uE9D9", "From your history", insights);
        var insightBody = Ui.Stack(Ui.Text("Reading your history…", 12.5, FontWeights.Normal, "Ui.Muted"));
        insights.Children.Add(insightBody);
        Add(Ui.Card(insights, p.AccentColor));

        // Tokens
        var tokens = new StackPanel(); CardTitle("\uE8EF", "Tokens on this PC", tokens);
        var tokenBody = Ui.Stack(Ui.Text("Reading local transcripts…", 12.5, FontWeights.Normal, "Ui.Muted"));
        tokens.Children.Add(tokenBody);
        var tokenCard = Ui.Card(tokens, p.AccentColor);
        if (p.Kind is "claude" or "codex" && s.TrackLocalTokens) Add(tokenCard);

        Add(AccentCard());

        _ = LoadAsync(paceSummary, paceDetail, startText, startDetail, insightBody, tokenBody);
    }

    private async Task LoadAsync(TextBlock paceSummary, TextBlock paceDetail, TextBlock startText, TextBlock startDetail, StackPanel insightBody, StackPanel tokenBody)
    {
        Ui.EnsureContext(this);
        var p = _p; var s = p.Settings; var primary = p.PrimaryWindow;
        var paces = await Task.Run(() => _rows.Select(r => r.Window.UsedFraction is null ? null : _coordinator.PaceFor(p.Snapshot, r.Window)).ToArray());
        for (var i = 0; i < _rows.Count; i++) ProviderVisuals.ApplyPace(_rows[i], p, paces[i]);
        var f = primary is null ? null : paces.ElementAtOrDefault(_rows.FindIndex(r => r.Window.Id == primary.Id));
        if (f?.PercentPerHour is { } rate)
        {
            paceSummary.Text = ProviderVisuals.PaceLine(f, primary!, s)?.Replace("At this pace: ", "") is { } line ? char.ToUpperInvariant(line[0]) + line[1..] : f.Summary;
            paceDetail.Text = $"{rate:0.0} percentage points an hour over the last {Math.Round(f.Coverage * 180):0} minutes · {f.Confidence.ToLowerInvariant()}. An estimate, not a provider promise.";
        }
        else { paceSummary.Text = f?.Summary ?? "Not enough readings yet"; paceDetail.Text = f?.Explanation is { } ex && ex.Length < 220 ? ex : "UsageNotch learns your pace from a few readings in the same limit period."; }

        var hint = await Task.Run(() => _coordinator.BestStartFor(p));
        if (hint is not null)
        {
            startText.Text = hint.Now ? "Now" : (hint.Tomorrow ? "Tomorrow, " : "") + TimeDisplay.Clock(hint.StartAt, s.Use24HourTime);
            startDetail.Text = $"It renews around {TimeDisplay.Clock(hint.RenewAt, s.Use24HourTime)}, in the middle of your usual busy hours ({BestStart.Hours(hint.BusyFrom, hint.BusyTo, s.Use24HourTime)}).";
        }

        var summary = primary is null ? null : await Task.Run(() => _coordinator.SummaryFor(p.Snapshot, primary));
        insightBody.Children.Clear();
        if (summary is null) insightBody.Children.Add(Ui.Text("History appears after a few readings on this PC.", 12.5, FontWeights.Normal, "Ui.Muted"));
        else Insights(insightBody, summary, primary!, s);

        var reports = await Task.Run(() => _coordinator.TokenReports());
        tokenBody.Children.Clear();
        if (reports.FirstOrDefault(r => r.Source == p.Id) is { } report) tokenBody.Children.Add(TokenViews.Summary(report, p.AccentColor, _reduced, compact: true));
        else tokenBody.Children.Add(Ui.Text(_coordinator.Tokens.Status is { Length: > 0 } status ? status : "No local transcripts found for this account yet.", 12.5, FontWeights.Normal, "Ui.Muted"));
    }

    /// <summary>Unused capacity, limit hits, reset reliability and the streak for one window.</summary>
    internal static void Insights(StackPanel into, WindowSummary summary, LimitWindow w, AppSettings s)
    {
        var unused = HistoryInsights.AverageUnused(summary.Unused);
        into.Children.Add(Ui.Text(unused is { } u ? $"{Math.Round(u * 100):0}% left unused" : "No finished periods yet", 20, FontWeights.Light));
        into.Children.Add(Ui.Text(unused is null ? "Unused capacity appears after a limit period ends." : $"On average across your last {summary.Unused.Count} finished {w.Label.ToLowerInvariant()} periods.", 12, FontWeights.Normal, "Ui.Muted"));
        if (summary.Unused.Count > 0)
        {
            var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 46, Margin = new Thickness(0, 12, 0, 4) };
            foreach (var period in summary.Unused)
            {
                var b = new Border { Width = 16, Height = Math.Max(3, 44 * period.Peak), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 0),
                    Background = Palette.Frozen(Palette.Alpha(Palette.Usage(period.Peak), .85)), ToolTip = $"Peak {Math.Round(period.Peak * 100):0}% · ended {TimeDisplay.Stamp(period.End, s)}" };
                bars.Children.Add(b);
            }
            into.Children.Add(bars);
        }
        var hits = summary.Hits.Count;
        var hitText = hits == 0 ? "You didn't hit this limit in the last 30 days." : $"Hit the limit {(hits switch { 1 => "once", 2 => "twice", _ => $"{hits} times" })} in the last 30 days" + (summary.Hits[^1] is { Reset: { } reset } last ? $"; last time you waited {Duration(reset - last.At)} for the reset." : ".");
        var r = summary.Reliability;
        var reliability = r.Periods == 0 ? "Reset reliability appears after a few periods." : r.Moved == 0 ? $"The reported reset time held in all {r.Periods} recent periods." : $"The reset time held in {r.Fixed} of {r.Periods} periods; it moved {r.Moved}× by about {r.AverageShiftMinutes:0} min.";
        foreach (var (glyph, text) in new[] { ("\uE7BA", hitText), ("\uE916", reliability), ("\uE8D1", summary.Streak > 0 ? $"{summary.Streak}-day streak of using this limit." : "No current streak.") })
        {
            var row = Ui.Columns((Ui.Icon(glyph, 13, "Ui.Muted", new Thickness(0, 1, 10, 0)), GridLength.Auto), (Ui.Text(text, 12.5, FontWeights.Normal, "Ui.Text"), new GridLength(1, GridUnitType.Star)));
            row.Margin = new Thickness(0, 10, 0, 0); into.Children.Add(row);
        }
    }

    private static string Duration(TimeSpan t) => t.TotalMinutes < 60 ? $"{Math.Max(1, Math.Round(t.TotalMinutes)):0} min" : t.TotalHours < 48 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{(int)t.TotalDays} days";

    private Border BudgetsCard()
    {
        var body = new StackPanel(); CardTitle("\uE8C8", "Budgets", body);
        body.Children.Add(BudgetEditor(_p, _persist, _reduced));
        return Ui.Card(body, _p.AccentColor);
    }

    /// <summary>A budget switch, a percentage and an optional time of day for every limit of a provider. Edits save at once.</summary>
    internal static StackPanel BudgetEditor(ProviderViewModel p, Action<Action<AppSettings>> persist, bool reduced, bool intro = true)
    {
        var s = p.Settings;
        var body = new StackPanel();
        if (intro) body.Children.Add(Ui.Text("Keep a limit under a percentage, optionally by a time of day. A tick marks it on the ring and bar; alerts tell you when you're near it or on pace to pass it. Synced with your phone.", 12, FontWeights.Normal, "Ui.Muted"));
        foreach (var w in p.Windows.Where(w => w.UsedFraction is not null))
        {
            var budget = Budgets.Of(s, p.Id, w.Id);
            var editor = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = budget is null ? Visibility.Collapsed : Visibility.Visible };
            var limit = budget?.Limit ?? 60; int? by = budget?.ByMinute;
            var value = Ui.Text($"{limit}%", 13, FontWeights.SemiBold, "Ui.Text", false);
            var slider = new Slider { Minimum = 10, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Value = limit, Margin = new Thickness(0, 6, 0, 4) };
            System.Windows.Automation.AutomationProperties.SetName(slider, w.Label + " budget");
            var times = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            void Save() => persist(settings => Budgets.Set(settings, new BudgetSetting(p.Id, w.Id, limit, by), p.Id, w.Id));
            void Times()
            {
                times.Children.Clear();
                foreach (var (label, minute) in new (string, int?)[] { ("By the reset", null), (BudgetSetting.Clock(12 * 60, s.Use24HourTime), 720), (BudgetSetting.Clock(15 * 60, s.Use24HourTime), 900), (BudgetSetting.Clock(18 * 60, s.Use24HourTime), 1080), (BudgetSetting.Clock(21 * 60, s.Use24HourTime), 1260) })
                    times.Children.Add(Ui.Chip(label, by == minute, () => { by = minute; Times(); Save(); }, null, reduced));
            }
            Times();
            slider.ValueChanged += (_, e) => { limit = (int)e.NewValue; value.Text = $"{limit}%"; Save(); };
            editor.Children.Add(Ui.Columns((Ui.Text("Keep under", 12.5, FontWeights.Normal, "Ui.Muted", false), new GridLength(1, GridUnitType.Star)), (value, GridLength.Auto)));
            editor.Children.Add(slider); editor.Children.Add(times);
            var toggle = Ui.Switch(budget is not null, on =>
            {
                editor.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) Save(); else persist(settings => Budgets.Set(settings, null, p.Id, w.Id));
            }, reduced, w.Label + " budget");
            var block = Ui.Stack(Ui.SettingRow(w.Label, w.UsedFraction is { } used ? $"{Math.Round(used * 100):0}% used now" : null, toggle), editor);
            var inner = Ui.Inner(block, new Thickness(14, 4, 14, 10)); inner.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(inner);
        }
        if (!p.Windows.Any(w => w.UsedFraction is not null)) body.Children.Add(Ui.Text("Budgets appear once this provider reports a limit.", 12.5, FontWeights.Normal, "Ui.Muted"));
        return body;
    }

    private Border AccentCard()
    {
        var body = new StackPanel(); CardTitle("\uE790", "Colour", body);
        body.Children.Add(Ui.Text("Tints this provider's cards, charts and the ambient light. Rings keep the usage colours so they always mean how much is used.", 12, FontWeights.Normal, "Ui.Muted"));
        var swatches = Swatches(_p.Id, _p.Settings, _persist); swatches.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(swatches);
        return Ui.Card(body, _p.AccentColor);
    }

    /// <summary>The provider's brand colour and the shared accent choices; picking one saves at once.</summary>
    internal static WrapPanel Swatches(string id, AppSettings s, Action<Action<AppSettings>> persist)
    {
        var swatches = new WrapPanel();
        var current = Accents.Of(s, id);
        foreach (var argb in DesignTokens.AccentChoices.Prepend(DesignTokens.Brand.GetValueOrDefault(ProviderKind.BrandKey(id), DesignTokens.Brand["other"])).Distinct())
        {
            var color = Accents.FromArgb(argb); var selected = color == current;
            var dot = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Palette.Frozen(color), Margin = new Thickness(0, 0, 9, 9), Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(selected ? 3 : 0), ToolTip = Accents.Hex(color) + (color == Accents.Default(id) ? " · brand colour" : "") };
            dot.SetResourceReference(Border.BorderBrushProperty, "Ui.Ink");
            System.Windows.Automation.AutomationProperties.SetName(dot, "Accent " + Accents.Hex(color));
            dot.MouseLeftButtonUp += (_, _) =>
            {
                foreach (Border other in swatches.Children) other.BorderThickness = new Thickness(0);
                dot.BorderThickness = new Thickness(3);
                persist(settings => { if (color == Accents.Default(id)) settings.Accents.Remove(id); else settings.Accents[id] = Accents.Hex(color); });
            };
            swatches.Children.Add(dot);
        }
        return swatches;
    }

    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in _rows) row.Countdown.Text = ResetClock.Describe(row.Window.ResetsAt, now);
        if (_p.PrimaryWindow is { ResetsAt: { } reset } && reset > now) _reset.Text = TimeDisplay.Reset(reset, _p.Settings) + " · " + ResetClock.Describe(reset, now).Replace("Resets in ", "");
        if (_ringHost.Tag is ProgressRing ring) ring.TimeLeft = _p.TimeLeftFraction;
    }

    /// <summary>New readings: the ring and number move in place.</summary>
    public void Update()
    {
        ProviderVisuals.UpdateRing(_ringHost, _p);
        _headline.Text = ProviderVisuals.Headline(_p);
        Explorer.Refresh();
    }
}
