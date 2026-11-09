using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UsageNotch.Models;
using UsageNotch.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace UsageNotch.Controls;

/// <summary>
/// Overview, as on Android: your rings at a glance, open incidents, then a glass card for every provider with each limit, its
/// countdown, pace, budget and the best time to start. Click a ring or card and it grows into the full-screen provider view.
/// </summary>
public partial class StatsDashboard : System.Windows.Controls.UserControl
{
    private UsageCoordinator? _coordinator;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly StackPanel _page = new() { MaxWidth = 1180, Margin = new Thickness(28, 8, 28, 40) };
    private readonly WrapPanel _rings = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _incidents = new();
    private readonly ContentControl _score = new();
    private string _scoreSignature = "";
    private readonly WrapPanel _cards = new();
    private readonly TextBlock _subtitle = Ui.Text("", 13, FontWeights.Normal, "Ui.Muted", false);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, (Grid Ring, RollingNumber Percent, TextBlock Secondary)> _ringViews = new();
    private ProviderDetailView? _detail;
    private bool _entered;
    private string _signature = "";

    /// <summary>Saves an edit made on these pages straight away (budgets, colours), so Cancel in Settings never undoes it.</summary>
    public Action<Action<AppSettings>>? Persist { get; set; }
    /// <summary>Raised with the provider id when the detail view opens or closes (null).</summary>
    public event Action<string?>? DetailChanged;

    public StatsDashboard()
    {
        InitializeComponent();
        var title = Ui.Text("Overview", 30, FontWeights.SemiBold, "Ui.Text", false);
        _subtitle.Margin = new Thickness(0, 4, 0, 18);
        var hero = Ui.Card(_rings, null, DesignTokens.Radius.Card, new Thickness(18, 22, 18, 16));
        _page.Children.Add(title); _page.Children.Add(_subtitle); _page.Children.Add(_incidents); _page.Children.Add(_score); _page.Children.Add(hero);
        _page.Children.Add(Ui.Label("Your providers"));
        _page.Children.Add(_cards);
        var note = Ui.Text("Private, local history · kept without automatic expiry. Missing readings never mean inactivity.", 11.5, FontWeights.Normal, "Ui.Faint");
        note.Margin = new Thickness(6, 6, 0, 0); _page.Children.Add(note);
        _scroll.Content = _page;
        Host.Children.Add(_scroll);
        _cards.SizeChanged += (_, _) => LayoutCards();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { if (_coordinator is not null) { _coordinator.Refreshed -= Refresh; _coordinator.Refreshed += Refresh; _coordinator.Health.Changed -= HealthChanged; _coordinator.Health.Changed += HealthChanged; } _timer.Start(); Refresh(); };
        Unloaded += (_, _) => { _timer.Stop(); if (_coordinator is not null) { _coordinator.Refreshed -= Refresh; _coordinator.Health.Changed -= HealthChanged; } };
    }

    private bool Reduced => _coordinator is null || Motion.IsReduced(_coordinator.Settings);

    public void Connect(UsageCoordinator coordinator) { _coordinator = coordinator; Refresh(); }

    private void HealthChanged() => Dispatcher.BeginInvoke(Refresh);

    /// <summary>Rebuilds after new readings or settings. Rings move in place; cards are rebuilt quietly.</summary>
    public void Refresh()
    {
        if (_coordinator is null) return;
        var items = _coordinator.Items.ToList(); var s = _coordinator.Settings;
        _subtitle.Text = items.Count == 0 ? "No providers are turned on. Choose them in Settings → Providers." :
            $"{items.Count} provider{(items.Count == 1 ? "" : "s")} · " + (_coordinator.LastRefresh is { } at ? $"checked {TimeDisplay.Clock(at, s.Use24HourTime)}" : "checking…");

        _incidents.Children.Clear();
        foreach (var incident in _coordinator.Health.Active.Where(i => items.Any(p => StatusPages.PageFor(p.Id) == i.Provider)).Take(3))
            _incidents.Children.Add(ProviderVisuals.Incident(incident, s));

        var signature = string.Join("|", items.Select(i => i.Id + i.Dual + i.ShowRemaining + i.Settings.ColoredLogos)) + Theme.Current.Dark;
        if (signature != _signature)
        {
            _signature = signature; _rings.Children.Clear(); _ringViews.Clear();
            var index = 0;
            foreach (var p in items)
            {
                var ring = ProviderVisuals.Ring(p, 92, Reduced);
                var percent = new RollingNumber { Text = ProviderVisuals.Headline(p), FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Reduced = Reduced, Margin = new Thickness(0, 10, 0, 0) };
                percent.SetResourceReference(RollingNumber.ForegroundProperty, "Ui.Text");
                var name = Ui.Text(p.DockName, 12, FontWeights.Normal, "Ui.Muted", false); name.HorizontalAlignment = HorizontalAlignment.Center;
                var secondary = Ui.Text("", 11, FontWeights.Normal, "Ui.Faint", false); secondary.HorizontalAlignment = HorizontalAlignment.Center;
                var cell = new Border { Padding = new Thickness(14, 6, 14, 6), CornerRadius = new CornerRadius(22), Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = Ui.Stack(ring, percent, name, secondary), ToolTip = p.LimitDescription };
                cell.MouseEnter += (_, _) => cell.SetResourceReference(Border.BackgroundProperty, "Ui.Inner");
                cell.MouseLeave += (_, _) => { cell.Background = System.Windows.Media.Brushes.Transparent; Motion.Press(cell, false, Reduced); };
                cell.MouseLeftButtonDown += (_, e) => { Motion.Press(cell, true, Reduced); e.Handled = true; };
                var provider = p;
                cell.MouseLeftButtonUp += (_, e) => { Motion.Press(cell, false, Reduced); Open(provider.Id, ring); e.Handled = true; };
                _rings.Children.Add(cell);
                _ringViews[p.Id] = (ring, percent, secondary);
                if (!_entered) Motion.Rise(cell, index++, Reduced, 12);
            }
        }
        foreach (var p in items)
        {
            if (!_ringViews.TryGetValue(p.Id, out var view)) continue;
            ProviderVisuals.UpdateRing(view.Ring, p);
            view.Percent.Text = ProviderVisuals.Headline(p);
            view.Secondary.Text = p.WeeklyWindow is { } w && w != p.PrimaryWindow ? $"{(ProviderKind.HasWeekly(p.Id) ? "7d" : "2nd")} {Ui.Percent(w.UsedFraction is { } u ? (p.ShowRemaining ? 1 - u : u) : null)}" : p.PrimaryWindow?.Label ?? p.HeadlineLabel;
        }

        _ = RefreshScore();

        _cards.Children.Clear();
        var i = 0;
        foreach (var p in items)
        {
            var card = new ProviderCardView(p, _coordinator, Reduced, (provider, origin) => Open(provider.Id, origin));
            _cards.Children.Add(card);
            if (!_entered) Motion.Rise(card, i++ + 2, Reduced);
        }
        _entered = true;
        LayoutCards();
        if (_detail is not null && items.FirstOrDefault(p => p.Id == _detailId) is not null) _detail.Update();
        else if (_detail is not null) CloseDetail();
    }

    /// <summary>The usage score card; rebuilt (and re-animated) only when its numbers change.</summary>
    private async Task RefreshScore()
    {
        if (_coordinator is null) return;
        Ui.EnsureContext(this);
        var coordinator = _coordinator;
        var score = await Task.Run(() => coordinator.Score());
        var signature = score is null ? "" : $"{score.Value}|{score.Previous}|{string.Join(",", score.Providers.Select(p => p.Id + Math.Round(p.Used * 100)))}|{Theme.Current.Dark}{Theme.Classic}";
        if (signature == _scoreSignature) return;
        _scoreSignature = signature;
        _score.Content = score is null || score.Providers.Count < 1 ? null : ScoreCard.Build(score, coordinator.Items.ToList(), Reduced);
        if (_score.Content is UIElement card && !Reduced) Motion.Rise(card, 0, Reduced);
    }

    private void LayoutCards()
    {
        const double gap = 14;
        var width = Math.Max(320, _cards.ActualWidth);
        var columns = width >= 1060 ? 3 : width >= 700 ? 2 : 1;
        var each = Math.Floor((width - (columns - 1) * gap) / columns) - 1;
        var i = 0;
        foreach (FrameworkElement card in _cards.Children)
        {
            card.Width = each;
            card.Margin = new Thickness(0, 0, i % columns == columns - 1 ? 0 : gap, gap);
            i++;
        }
    }

    private void Tick()
    {
        foreach (var card in _cards.Children.OfType<ProviderCardView>()) card.Tick();
        if (_coordinator is not null)
            foreach (var p in _coordinator.Items)
                if (_ringViews.TryGetValue(p.Id, out var view) && view.Ring.Tag is ProgressRing ring) ring.TimeLeft = p.TimeLeftFraction;
        _detail?.Tick();
    }

    private string? _detailId;

    /// <summary>Opens the full-screen view of a provider, growing out of <paramref name="origin"/>.</summary>
    public void Open(string id, FrameworkElement? origin)
    {
        if (_coordinator?.Items.FirstOrDefault(p => p.Id == id) is not { } p) return;
        if (_detail is not null) Host.Children.Remove(_detail);
        _detailId = id;
        _detail = new ProviderDetailView(_coordinator, p, Reduced, mutate => Persist?.Invoke(mutate));
        _detail.Back += CloseDetail;
        Host.Children.Add(_detail);
        Fade(_scroll, false); Fade(_detail, true);
        _detail.Enter(origin);
        DetailChanged?.Invoke(id);
    }

    public void CloseDetail()
    {
        if (_detail is not { } detail) return;
        _detail = null; _detailId = null;
        Fade(_scroll, true);
        if (Reduced) Host.Children.Remove(detail);
        else
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => Host.Children.Remove(detail);
            detail.BeginAnimation(OpacityProperty, fade);
        }
        DetailChanged?.Invoke(null);
        Refresh();
    }

    public bool DetailOpen => _detail is not null;

    private void Fade(FrameworkElement element, bool show)
    {
        element.IsHitTestVisible = show;
        var scale = element.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new System.Windows.Point(.5, .3); element.RenderTransform = scale;
        if (Reduced) { element.Opacity = show ? 1 : 0; element.Visibility = show ? Visibility.Visible : Visibility.Hidden; return; }
        element.Visibility = Visibility.Visible;
        Motion.To(element, OpacityProperty, show ? 1 : 0, show ? 320 : 180, false);
        Motion.Spring(scale, ScaleTransform.ScaleXProperty, show ? 1 : .97, false, .85, 520);
        Motion.Spring(scale, ScaleTransform.ScaleYProperty, show ? 1 : .97, false, .85, 520);
    }

    /// <summary>Opens a provider's full view (from the dock's Explore history) and returns its ring for the connected transition.</summary>
    public FrameworkElement? FocusProvider(string id)
    {
        var origin = _ringViews.TryGetValue(id, out var view) ? view.Ring : null;
        Open(id, origin);
        return _detail?.Explorer;
    }
}
