using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageNotch.Models;
using UsageNotch.Services;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace UsageNotch.Controls;

/// <summary>Rings and cards for a provider, shared by Overview and the full-screen provider view.</summary>
public static class ProviderVisuals
{
    /// <summary>A usage ring with the provider's logo in the centre: comet arc, weekly inner ring, time-left hairline and budget tick.</summary>
    public static Grid Ring(ProviderViewModel p, double size, bool reduced)
    {
        var s = p.Settings;
        var ring = new ProgressRing
        {
            Width = size, Height = size, Value = p.RingFraction ?? 0, SecondaryValue = p.SecondaryRingFraction, Remaining = p.ShowRemaining,
            SecondaryKnown = p.SecondaryKnown, Dual = p.WeeklyWindow is not null && p.WeeklyWindow != p.PrimaryWindow && size >= 56, ReducedMotion = reduced,
            IsKnown = p.UsedFraction is not null, Status = p.Status, TimeLeft = p.TimeLeftFraction, Comet = s.CometRings, Ink = Theme.Current.Ink,
            TrackBrush = Palette.Frozen(Theme.Current.Track),
        };
        ring.Budget = p.PrimaryWindow is { } w && Budgets.Of(s, p.Id, w.Id) is { } b ? (p.ShowRemaining ? 1 - b.Limit / 100d : b.Limit / 100d) : double.NaN;
        ring.SecondaryBudget = ring.Dual && p.WeeklyWindow is { } wk && Budgets.Of(s, p.Id, wk.Id) is { } wb ? (p.ShowRemaining ? 1 - wb.Limit / 100d : wb.Limit / 100d) : double.NaN;
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(ring);
        grid.Children.Add(Ui.Logo(p.Id, size * .3, s.ColoredLogos));
        grid.Tag = ring;
        return grid;
    }

    public static void UpdateRing(Grid host, ProviderViewModel p)
    {
        if (host.Tag is not ProgressRing ring) return;
        ring.Status = p.Status; ring.IsKnown = p.UsedFraction is not null; ring.Value = p.RingFraction ?? 0;
        ring.SecondaryValue = p.SecondaryRingFraction; ring.SecondaryKnown = p.SecondaryKnown; ring.TimeLeft = p.TimeLeftFraction;
    }

    public static Color StatusColor(ProviderViewModel p) => p.StatusPill switch
    {
        "Live" => Accents.FromArgb(DesignTokens.Live), "Preview" => Accents.FromArgb(DesignTokens.Preview), "Checking" => Palette.Loading,
        "Error" => Palette.Danger, _ => Palette.Warning
    };

    /// <summary>The headline in the ring's terms: "73%" or "27% left".</summary>
    public static string Headline(ProviderViewModel p) => p.Status == SnapshotStatus.Loading ? "···" : p.RingFraction is { } f ? $"{Math.Round(Math.Clamp(f, 0, 1) * 100):0}%" : "—";

    /// <summary>
    /// One limit as on Android and the dock card: its label and percentage, a usage bar with the budget tick, a live countdown,
    /// the local reset time, and the pace and budget lines.
    /// </summary>
    public sealed class LimitRow
    {
        public required LimitWindow Window { get; init; }
        public required UIElement View { get; init; }
        public required TextBlock Countdown { get; init; }
        public required TextBlock Pace { get; init; }
        public required Border BudgetSlot { get; init; }
    }

    public static LimitRow Limit(ProviderViewModel p, LimitWindow w, int index, bool reduced)
    {
        var s = p.Settings; var used = w.UsedFraction;
        var shown = used is { } u ? (p.ShowRemaining ? 1 - Math.Clamp(u, 0, 1) : u) : (double?)null;
        var color = used is { } uu ? Palette.Readable(Palette.Usage(uu), Theme.Current.Card) : Theme.Current.Muted;
        var value = Ui.Text(shown is { } v ? $"{Math.Round(v * 100):0}%" + (p.ShowRemaining && s.ShowUsageSuffix ? " left" : "") : "—", 14, FontWeights.SemiBold, "Ui.Text", false);
        value.Foreground = Palette.Frozen(color);
        var head = Ui.Columns((Ui.Text(w.Label, 13, FontWeights.Normal, "Ui.Text", false), new GridLength(1, GridUnitType.Star)), (value, GridLength.Auto));
        var budget = Budgets.Of(s, p.Id, w.Id);
        var bar = new UsageBar { Value = shown ?? 0, Used = used ?? 0, Unknown = used is null, Reduced = reduced, Delay = 70 * index, Margin = new Thickness(0, 9, 0, 8),
            Budget = budget is { } b ? (p.ShowRemaining ? 1 - b.Limit / 100d : b.Limit / 100d) : double.NaN };
        var countdown = Ui.Text(ResetClock.Describe(w.ResetsAt, DateTimeOffset.UtcNow), 11.5, FontWeights.Normal, "Ui.Muted", false);
        var local = Ui.Text(w.ResetsAt is { } at && at <= DateTimeOffset.Now ? "Renewed · awaiting reading" : TimeDisplay.Reset(w.ResetsAt, s), 11.5, FontWeights.Normal, "Ui.Faint", false);
        var amount = new LimitAmountConverter().Convert(w, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture) as string;
        var foot = Ui.Columns((countdown, new GridLength(1, GridUnitType.Star)), (local, GridLength.Auto));
        var pace = Ui.Text("", 11.5, FontWeights.Normal, "Ui.Muted");
        pace.Visibility = Visibility.Collapsed; pace.Margin = new Thickness(0, 6, 0, 0);
        var budgetSlot = new Border { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        var stack = Ui.Stack(head, bar, foot);
        if (!string.IsNullOrWhiteSpace(amount)) { var a = Ui.Text(amount, 11, FontWeights.Normal, "Ui.Faint"); a.Margin = new Thickness(0, 4, 0, 0); stack.Children.Add(a); }
        stack.Children.Add(pace); stack.Children.Add(budgetSlot);
        return new LimitRow { Window = w, View = Ui.Inner(stack, new Thickness(14, 12, 14, 12)), Countdown = countdown, Pace = pace, BudgetSlot = budgetSlot };
    }

    /// <summary>Fills a row's pace and budget lines once its forecast is known.</summary>
    public static void ApplyPace(LimitRow row, ProviderViewModel p, PaceForecast? forecast)
    {
        var s = p.Settings; var w = row.Window;
        var line = PaceLine(forecast, w, s);
        row.Pace.Text = line ?? ""; row.Pace.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        if (Budgets.Of(s, p.Id, w.Id) is { } budget)
        {
            var status = Budgets.Evaluate(budget, w, forecast, DateTimeOffset.Now, s.Use24HourTime);
            var color = status.State switch { Budgets.State.Over => Palette.Danger, Budgets.State.Pace => Palette.Warning, Budgets.State.Near => Palette.Warning, _ => Accents.FromArgb(DesignTokens.Live) };
            row.BudgetSlot.Child = Ui.Pill("◎  " + status.Text, color, 10.5);
            row.BudgetSlot.Visibility = Visibility.Visible;
        }
        else row.BudgetSlot.Visibility = Visibility.Collapsed;
    }

    public static string? PaceLine(PaceForecast? f, LimitWindow w, AppSettings s)
    {
        if (f?.PercentPerHour is not { } rate || w.UsedFraction is not { } used || w.ResetsAt is not { } reset) return f?.Summary.StartsWith("Learning") == true ? "Learning your pace…" : null;
        if (rate <= .0001) return "At this pace: no increase since the last readings";
        var eta = DateTimeOffset.Now.AddHours((1 - used) * 100 / rate);
        return eta < reset ? $"At this pace: limit around {TimeDisplay.Clock(eta, s.Use24HourTime)}" : $"At this pace: about {Math.Min(100, f.ProjectedPercent ?? 0):0}% by the reset";
    }

    /// <summary>A banner for an open incident on the provider's status page.</summary>
    public static Border Incident(Services.Incident i, AppSettings s)
    {
        var color = i.Impact switch { "critical" or "major" => Palette.Danger, "maintenance" => Palette.Loading, _ => Palette.Warning };
        var title = Ui.Text(i.Title, 13, FontWeights.SemiBold);
        var meta = Ui.Text($"{StatusName(i.Provider)} status · {char.ToUpperInvariant(i.Status[0])}{i.Status[1..]} · updated {TimeDisplay.Clock(i.UpdatedAt, s.Use24HourTime)}", 11.5, FontWeights.Normal, "Ui.Muted");
        var body = Ui.Columns((Ui.Icon("", 18, "Ui.Warning", new Thickness(0, 0, 12, 0)), GridLength.Auto), (Ui.Stack(title, meta), new GridLength(1, GridUnitType.Star)));
        if (i.Url is { } url) { var open = Ui.Button("Status page", () => OpenUrl(url), false, ""); open.VerticalAlignment = VerticalAlignment.Center; open.Margin = new Thickness(12, 0, 0, 0); body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetColumn(open, 2); body.Children.Add(open); }
        ((TextBlock)((Grid)body).Children[0]).Foreground = Palette.Frozen(color);
        return Ui.Card(body, color, 20, new Thickness(16, 12, 16, 12));
    }

    public static string StatusName(string page) => page switch { "claude" => "Claude", "codex" => "OpenAI", "cursor" => "Cursor", _ => page };

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    public static string Updated(ProviderViewModel p) => p.UpdatedText + (p.AccountLabel is { Length: > 0 } account ? " · " + account : "");
}

/// <summary>
/// A provider on Overview, laid out like Android's provider card: header, every limit with its bar and countdown, the pace,
/// budgets, the best time to start and actions. Countdowns tick without rebuilding the card.
/// </summary>
public sealed class ProviderCardView : Border
{
    private readonly List<ProviderVisuals.LimitRow> _rows = [];
    private readonly TextBlock _updated;
    public ProviderViewModel Provider { get; }

    public ProviderCardView(ProviderViewModel p, UsageCoordinator coordinator, bool reduced, Action<ProviderViewModel, FrameworkElement> open)
    {
        Provider = p;
        var s = p.Settings;
        var name = Ui.Text(p.DisplayName, 16, FontWeights.SemiBold, "Ui.Text", false);
        _updated = Ui.Text(ProviderVisuals.Updated(p), 11.5, FontWeights.Normal, "Ui.Faint", false);
        var pill = Ui.Pill(s.ReviewSession ? "Preview" : p.StatusPill, ProviderVisuals.StatusColor(p));
        var logo = Ui.LogoTile(p.Id, 36);
        var header = Ui.Columns((logo, GridLength.Auto), (Ui.Stack(name, _updated), new GridLength(1, GridUnitType.Star)), (pill, GridLength.Auto));
        ((StackPanel)header.Children[1]).Margin = new Thickness(12, 0, 8, 0);
        ((StackPanel)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;
        header.Cursor = System.Windows.Input.Cursors.Hand;
        header.MouseLeftButtonUp += (_, _) => open(p, logo);
        var body = Ui.Stack(header);
        if (!string.IsNullOrWhiteSpace(p.StatusText)) { var status = Ui.Text(p.StatusText, 12, FontWeights.Normal, "Ui.Muted"); status.Margin = new Thickness(2, 12, 2, 0); body.Children.Add(status); }
        var index = 0;
        foreach (var w in p.Windows)
        {
            var row = ProviderVisuals.Limit(p, w, index++, reduced);
            ((FrameworkElement)row.View).Margin = new Thickness(0, 10, 0, 0);
            _rows.Add(row); body.Children.Add(row.View);
        }
        var hint = Ui.Text("", 12, FontWeights.Normal, "Ui.Muted"); hint.Visibility = Visibility.Collapsed;
        var hintRow = Ui.Columns((Ui.Icon("", 14, "Ui.Accent", new Thickness(0, 0, 9, 0)), GridLength.Auto), (hint, new GridLength(1, GridUnitType.Star)));
        hintRow.Margin = new Thickness(2, 12, 0, 0); hintRow.Visibility = Visibility.Collapsed;
        body.Children.Add(hintRow);
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var details = Ui.Button("Details", () => open(p, logo), false, "", reduced); details.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(details);
        if (p.HasManageUrl) actions.Children.Add(Ui.Button(p.DashboardLabel, () => ProviderVisuals.OpenUrl(p.Snapshot.ManageUrl!), false, "", reduced));
        body.Children.Add(actions);
        var card = Ui.Card(body, p.AccentColor);
        Child = card; Margin = new Thickness(0, 0, 14, 14);
        _ = LoadAsync(coordinator, hint, hintRow);
    }

    private async Task LoadAsync(UsageCoordinator coordinator, TextBlock hint, FrameworkElement hintRow)
    {
        Ui.EnsureContext(this);
        var p = Provider; var s = p.Settings;
        var paces = await Task.Run(() => _rows.Select(r => r.Window.UsedFraction is null || p.Status != SnapshotStatus.Ok ? null : coordinator.PaceFor(p.Snapshot, r.Window)).ToArray());
        for (var i = 0; i < _rows.Count; i++) ProviderVisuals.ApplyPace(_rows[i], p, paces[i]);
        var start = p.Status == SnapshotStatus.Ok ? await Task.Run(() => coordinator.BestStartFor(p)) : null;
        if (start is null) return;
        hint.Text = (start.Now ? "Best time to start: now" : $"Best time to start: {(start.Tomorrow ? "tomorrow " : "")}{TimeDisplay.Clock(start.StartAt, s.Use24HourTime)}")
            + $" · renews {TimeDisplay.Clock(start.RenewAt, s.Use24HourTime)}, mid busy hours";
        hint.Visibility = Visibility.Visible; hintRow.Visibility = Visibility.Visible;
    }

    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in _rows) row.Countdown.Text = ResetClock.Describe(row.Window.ResetsAt, now);
        _updated.Text = ProviderVisuals.Updated(Provider);
    }
}
