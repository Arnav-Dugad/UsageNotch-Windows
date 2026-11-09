using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageNotch.Controls;
using UsageNotch.Models;
using UsageNotch.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace UsageNotch;

/// <summary>
/// The glass workspace: a gliding tab bar for Overview, History and Settings, the live status capsule, the theme and the
/// editors whose changes save straight away (accounts, order, budgets, colours, storage).
/// </summary>
public partial class SettingsWindow
{
    private UsageCoordinator? _coordinator;
    private GlassTabBar? _topTabs;
    private int _lastSettingsPage = 2;
    private IDisposable? _systemTheme;
    private Border? _statusDot;
    private TextBlock? _statusText;
    private FrameworkElement? _refreshIcon;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Raised for edits that save at once, outside Save/Cancel. The owner applies the change to its saved copy too, so
    /// Cancel never undoes it.
    /// </summary>
    public event Action<Action<AppSettings>>? PersistRequested;

    private bool Reduced => Motion.IsReduced(_settings);

    private void InitWorkspace(UsageCoordinator? coordinator)
    {
        _coordinator = coordinator;
        Theme.Apply(Resources, _settings);
        _systemTheme = Theme.WatchSystem(() => Dispatcher.BeginInvoke(() => { if (_settings.AppTheme == "System") ApplyTheme(); }));
        Closed += (_, _) => { _systemTheme?.Dispose(); _statusTimer.Stop(); };
        Ambient.Animate = !Reduced;

        _topTabs = new GlassTabBar(["Overview", "History", "Settings"], 0, 40, 13, ["\uE80F", "\uE81C", "\uE713"]) { Reduced = Reduced };
        _topTabs.SelectionChanged += index => SettingsTabs.SelectedIndex = index switch { 0 => 0, 1 => 1, _ => _lastSettingsPage };
        TopTabsHost.Content = _topTabs;

        StatsView.Persist = Persist;
        if (coordinator is not null)
        {
            HistoryPageView.Connect(coordinator);
            coordinator.Refreshed += CoordinatorRefreshed;
            Closed += (_, _) => coordinator.Refreshed -= CoordinatorRefreshed;
            coordinator.PropertyChanged += CoordinatorChanged;
            Closed += (_, _) => coordinator.PropertyChanged -= CoordinatorChanged;
        }
        if (System.Windows.Application.Current is App app)
        {
            app.SharedSettingsChanged += SharedChanged;
            Closed += (_, _) => app.SharedSettingsChanged -= SharedChanged;
        }
        BuildStatus();
        _statusTimer.Tick += (_, _) => UpdateLiveStatus();
        _statusTimer.Start();
        BuildManualEditors();
        UpdateAmbient();
    }

    private string _editorSignature = "";
    /// <summary>New readings only rebuild the editors when the providers themselves change, so nothing resets while you edit.</summary>
    private void CoordinatorRefreshed()
    {
        UpdateLiveStatus(); UpdateAmbient();
        var signature = string.Join("|", _coordinator?.Items.Select(i => i.Id + ":" + string.Join(",", i.Windows.Select(w => w.Id))) ?? []);
        if (signature != _editorSignature) { _editorSignature = signature; BuildManualEditors(); }
    }
    private void CoordinatorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateLiveStatus);
    /// <summary>A phone changed shared settings: show them in the controls too, so the next live edit can't undo them.</summary>
    private void SharedChanged()
    {
        var previous = _suspend; _suspend = true;
        WarnSlider.Value = Math.Clamp(_settings.AlertWarnPercent, 30, 95);
        CriticalSlider.Value = Math.Clamp(_settings.AlertCriticalPercent, 50, 100);
        QuietBox.IsChecked = _settings.QuietHoursEnabled;
        QuietStartSlider.Value = _settings.QuietStartHour; QuietEndSlider.Value = _settings.QuietEndHour;
        ClaudeLabelField.Text = _settings.ClaudeDockLabel; CodexLabelField.Text = _settings.CodexDockLabel;
        GeminiLabelField.Text = _settings.GeminiDockLabel; CursorLabelField.Text = _settings.CursorDockLabel;
        _suspend = previous;
        BuildManualEditors(); UpdateAmbient(); StatsView.Refresh();
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        new Controls.WhatsNewWindow(_settings, false, ShowHistoryTab, null) { Owner = this }.ShowDialog();

    private void ApplyTheme()
    {
        Theme.Apply(Resources, _settings);
        Ambient.InvalidateVisual();
        StatsView.Refresh(); HistoryPageView.Refresh(); BuildManualEditors();
    }

    private void UpdateAmbient()
    {
        if (_coordinator is null) return;
        Ambient.Colors = _coordinator.Items.Take(3).Select(p => p.AccentColor).ToArray();
        Ambient.Animate = !Reduced;
    }

    /// <summary>Saves an edit straight away and lets the dock and phone see it.</summary>
    private void Persist(Action<AppSettings> mutate)
    {
        if (_settings.ReviewSession) { mutate(_settings); RefreshAfterPersist(); return; }
        if (PersistRequested is null) { mutate(_settings); SharedSettings.Capture(_settings, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); SettingsStore.Save(_settings); }
        else PersistRequested(mutate);
        RefreshAfterPersist();
    }

    private void RefreshAfterPersist()
    {
        foreach (var item in _coordinator?.Items ?? []) item.RefreshAppearance();
        UpdateAmbient();
        SettingsChanged?.Invoke();
    }

    // ---------------------------------------------------------------- navigation

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { CommandParameter: string page } && int.TryParse(page, out var index)) SettingsTabs.SelectedIndex = index;
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != SettingsTabs || _topTabs is null) return;
        var index = SettingsTabs.SelectedIndex;
        var settingsPage = index >= 2;
        if (settingsPage) _lastSettingsPage = index;
        _topTabs.Select(index switch { 0 => 0, 1 => 1, _ => 2 }, false);
        SettingsNavHost.Visibility = settingsPage ? Visibility.Visible : Visibility.Collapsed;
        FooterBar.Visibility = settingsPage ? Visibility.Visible : Visibility.Collapsed;
        foreach (var nav in SettingsNav.Children.OfType<System.Windows.Controls.RadioButton>()) nav.IsChecked = nav.CommandParameter as string == index.ToString();
        if (index == 1) HistoryPageView.Refresh();
        if (index == 0 && StatsView.DetailOpen && e.RemovedItems.Count > 0 && e.RemovedItems[0] is TabItem { Header: "Overview" }) StatsView.CloseDetail();
        // Pages slide in a little and fade, in the direction you moved.
        if (SettingsTabs.SelectedContent is FrameworkElement content && !Reduced && IsLoaded)
        {
            var from = e.RemovedItems.Count > 0 && e.RemovedItems[0] is TabItem old ? SettingsTabs.Items.IndexOf(old) : index;
            var move = new TranslateTransform(index >= from ? 26 : -26, 0);
            content.RenderTransform = move;
            Motion.Spring(move, TranslateTransform.XProperty, 0, false, .86, 520);
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    // ---------------------------------------------------------------- status capsule

    private void BuildStatus()
    {
        _statusDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        _statusText = Ui.Text("", 12.5, FontWeights.SemiBold, "Ui.Text", false);
        var capsule = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(13, 7, 13, 7), BorderThickness = new Thickness(1), Child = Ui.Row(0, _statusDot, _statusText), VerticalAlignment = VerticalAlignment.Center };
        capsule.SetResourceReference(Border.BackgroundProperty, "Ui.Glass");
        capsule.SetResourceReference(Border.BorderBrushProperty, "Ui.Rim");
        var refresh = Ui.Button("", () => { if (_coordinator is not null) _ = _coordinator.RefreshAllAsync(); }, false, "\uE72C", Reduced, "Refresh every provider");
        refresh.Margin = new Thickness(8, 0, 0, 0);
        _refreshIcon = Ui.Descendants<TextBlock>(refresh).FirstOrDefault();
        if (_refreshIcon is not null) { _refreshIcon.RenderTransformOrigin = new System.Windows.Point(.5, .5); _refreshIcon.RenderTransform = new RotateTransform(); }
        StatusHost.Content = Ui.Row(0, capsule, refresh);
        UpdateLiveStatus();
        if (!Reduced)
        {
            // The live dot breathes, like Android's status capsule.
            _statusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(1400)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
    }

    private void UpdateLiveStatus()
    {
        if (_statusDot is null || _statusText is null) return;
        var items = _coordinator?.Items.ToList() ?? [];
        var busy = _coordinator?.IsRefreshing == true;
        var live = items.Count(i => i.StatusPill == "Live");
        var color = _settings.ReviewSession ? Accents.FromArgb(DesignTokens.Preview) : busy ? Palette.Loading : live > 0 ? Accents.FromArgb(DesignTokens.Live) : items.Count == 0 ? Palette.Unknown : Palette.Warning;
        _statusDot.Background = Palette.Frozen(color);
        _statusText.Text = _settings.ReviewSession ? "Sample data" : busy ? "Checking…" : items.Count == 0 ? "No providers" :
            (live == items.Count ? "Live" : $"{live} of {items.Count} live") + (_coordinator?.LastRefresh is { } at ? " · " + TimeDisplay.Clock(at, _settings.Use24HourTime) : "");
        if (_refreshIcon?.RenderTransform is RotateTransform spin)
        {
            if (busy && !Reduced) spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(.9)) { RepeatBehavior = RepeatBehavior.Forever });
            else spin.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    // ---------------------------------------------------------------- editors that save at once

    private void BuildManualEditors()
    {
        if (_coordinator is null) { AccountsHost.Content = AccountsEditor(); return; }
        AccentsHost.Content = AccentsEditor();
        AccountsHost.Content = AccountsEditor();
        OrderHost.Content = OrderEditor();
        BudgetsHost.Content = BudgetsEditor();
        DataHost.Content = DataEditor();
    }

    private UIElement AccentsEditor()
    {
        var stack = new StackPanel();
        foreach (var p in _coordinator!.Items)
        {
            var head = Ui.Row(10, Ui.Logo(p.Id, 18, _settings.ColoredLogos), Ui.Text(p.DisplayName, 13, FontWeights.SemiBold, "Ui.Text", false));
            head.Margin = new Thickness(0, 10, 0, 8);
            stack.Children.Add(head);
            stack.Children.Add(ProviderDetailView.Swatches(p.Id, _settings, Persist));
        }
        if (stack.Children.Count == 0) stack.Children.Add(Ui.Text("Turn on a provider to choose its colour.", 12.5, FontWeights.Normal, "Ui.Muted"));
        return stack;
    }

    private UIElement BudgetsEditor()
    {
        var stack = new StackPanel();
        foreach (var p in _coordinator!.Items.Where(p => p.Windows.Any(w => w.UsedFraction is not null)))
        {
            var head = Ui.Row(10, Ui.LogoTile(p.Id, 30), Ui.Text(p.DisplayName, 15, FontWeights.SemiBold, "Ui.Text", false));
            foreach (UIElement c in head.Children) if (c is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            var card = Ui.Card(Ui.Stack(head, ProviderDetailView.BudgetEditor(p, Persist, Reduced, intro: false)), p.AccentColor);
            stack.Children.Add(card);
        }
        if (stack.Children.Count == 0) stack.Children.Add(Ui.Card(Ui.Text("Budgets appear once a provider reports its limits.", 12.5, FontWeights.Normal, "Ui.Muted")));
        return stack;
    }

    private UIElement OrderEditor()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text("The order of rings on the dock, Overview and your phone.", 12, FontWeights.Normal, "Ui.Muted"));
        var ids = _coordinator!.Items.Select(i => i.Id).ToList();
        for (var i = 0; i < ids.Count; i++)
        {
            var index = i; var p = _coordinator.Items[i];
            var up = Ui.Button("", () => Move(ids, index, -1), false, "\uE70E", Reduced, "Move up"); up.Opacity = i == 0 ? .35 : 1; up.IsEnabled = i > 0;
            var down = Ui.Button("", () => Move(ids, index, 1), false, "\uE70D", Reduced, "Move down"); down.Opacity = i == ids.Count - 1 ? .35 : 1; down.IsEnabled = i < ids.Count - 1; down.Margin = new Thickness(6, 0, 0, 0);
            var row = Ui.Columns((Ui.Logo(p.Id, 18, _settings.ColoredLogos), GridLength.Auto), (Ui.Text(p.DisplayName, 13.5, FontWeights.Normal, "Ui.Text", false), new GridLength(1, GridUnitType.Star)), (Ui.Row(0, up, down), GridLength.Auto));
            ((FrameworkElement)row.Children[1]).Margin = new Thickness(12, 0, 0, 0); ((FrameworkElement)row.Children[1]).VerticalAlignment = VerticalAlignment.Center;
            row.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(row);
        }
        return stack;
    }

    private void Move(List<string> ids, int index, int by)
    {
        var target = index + by;
        if (target < 0 || target >= ids.Count) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
        var order = ids.ToList();
        Persist(settings => settings.ProviderOrder = order);
        _coordinator?.RebuildProviders();
        BuildManualEditors(); StatsView.Refresh();
    }

    private UIElement AccountsEditor()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text("Watch another Claude Code or Codex sign-in alongside your main one. Each account keeps its own folder (Claude Code's CLAUDE_CONFIG_DIR, Codex's CODEX_HOME), so their sign-ins, readings and history never mix.", 12, FontWeights.Normal, "Ui.Muted"));
        foreach (var account in _settings.Accounts.ToList())
        {
            var signedIn = account.SignedIn;
            var title = Ui.Text(account.DisplayName, 13.5, FontWeights.SemiBold, "Ui.Text", false);
            var detail = Ui.Text((signedIn ? "Signed in · " : "Not signed in yet · ") + account.Folder, 11.5, FontWeights.Normal, "Ui.Faint", false);
            var on = Ui.Switch(account.Enabled, value => { Persist(s => { if (s.Accounts.FirstOrDefault(a => a.Slug == account.Slug) is { } a) a.Enabled = value; }); _coordinator?.RebuildProviders(); _ = _coordinator?.RefreshAllAsync(); }, Reduced, account.DisplayName);
            var signIn = Ui.Button(signedIn ? "Sign in again" : "Sign in", () => LaunchSignIn(account), !signedIn, "\uE8D7", Reduced);
            var remove = Ui.Button("", () =>
            {
                Persist(s => s.Accounts.RemoveAll(a => a.Slug == account.Slug));
                _coordinator?.RebuildProviders(); BuildManualEditors(); StatsView.Refresh();
            }, false, "\uE74D", Reduced, "Remove " + account.DisplayName);
            signIn.Margin = new Thickness(10, 0, 8, 0);
            var row = Ui.Columns((Ui.LogoTile(account.Kind, 32), GridLength.Auto), (Ui.Stack(title, detail), new GridLength(1, GridUnitType.Star)), (Ui.Row(0, on, signIn, remove), GridLength.Auto));
            ((FrameworkElement)row.Children[1]).Margin = new Thickness(12, 0, 8, 0); ((FrameworkElement)row.Children[1]).VerticalAlignment = VerticalAlignment.Center;
            foreach (UIElement c in ((StackPanel)row.Children[2]).Children) if (c is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            var inner = Ui.Inner(row, new Thickness(12, 10, 12, 10)); inner.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(inner);
        }
        var kind = "claude";
        var kinds = new WrapPanel();
        void Kinds()
        {
            kinds.Children.Clear();
            foreach (var (label, value) in new[] { ("Claude Code", "claude"), ("Codex", "codex") })
                kinds.Children.Add(Ui.Chip(label, kind == value, () => { kind = value; Kinds(); }, Ui.Logo(value, 14), Reduced));
        }
        Kinds();
        var name = new System.Windows.Controls.TextBox { MaxLength = 16, Width = 220, Margin = new Thickness(0, 0, 10, 0) };
        name.SetResourceReference(StyleProperty, "Field");
        System.Windows.Automation.AutomationProperties.SetName(name, "Account name");
        var status = Ui.Text("", 12, FontWeights.Normal, "Ui.Muted"); status.Margin = new Thickness(0, 8, 0, 0);
        void Add(string? folder)
        {
            var label = string.IsNullOrWhiteSpace(name.Text) ? (kind == "codex" ? "Work" : "Work") : name.Text.Trim();
            var slug = ExtraAccount.MakeSlug(label, _settings.Accounts.Select(a => a.Slug));
            var path = folder ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), (kind == "codex" ? ".codex-" : ".claude-") + slug);
            try { System.IO.Directory.CreateDirectory(path); }
            catch (Exception e) { status.Text = "Couldn't create the folder: " + e.Message; return; }
            var account = new ExtraAccount { Slug = slug, Kind = kind, Label = label, Folder = path };
            Persist(s => s.Accounts.Add(account));
            _coordinator?.RebuildProviders();
            if (!account.SignedIn) LaunchSignIn(account);
            BuildManualEditors(); StatsView.Refresh();
        }
        var create = Ui.Button("Add and sign in", () => Add(null), true, "\uE710", Reduced);
        var existing = Ui.Button("Use existing folder…", () =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = kind == "codex" ? "Choose a CODEX_HOME folder" : "Choose a Claude Code config folder (it holds .credentials.json)", UseDescriptionForTitle = true };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Add(dialog.SelectedPath);
        }, false, "\uE8B7", Reduced);
        existing.Margin = new Thickness(8, 0, 0, 0);
        var form = Ui.Stack(Ui.Text("Add an account", 13.5, FontWeights.SemiBold), kinds, Ui.Row(0, name, create, existing), status);
        ((FrameworkElement)form.Children[1]).Margin = new Thickness(0, 10, 0, 2);
        foreach (UIElement c in ((StackPanel)form.Children[2]).Children) if (c is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        var inner2 = Ui.Inner(form, new Thickness(14, 12, 14, 12)); inner2.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(inner2);
        return stack;
    }

    /// <summary>Opens a terminal signed in to that account's folder only; nothing else on the PC changes.</summary>
    private void LaunchSignIn(ExtraAccount account)
    {
        try
        {
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            if (account.Kind == "codex")
            {
                var exe = Providers.CodexSubscriptionProvider.FindCodex() ?? "codex";
                info.Environment["CODEX_HOME"] = account.Folder;
                info.Arguments = $"/k title Sign in to Codex · {account.DisplayName} && echo Signing in to a separate Codex account. When it finishes, close this window and press Refresh in UsageNotch. && \"{exe}\" login";
            }
            else
            {
                info.Environment["CLAUDE_CONFIG_DIR"] = account.Folder;
                info.Arguments = $"/k title Sign in to Claude Code · {account.DisplayName} && echo Signing in to a separate Claude Code account. Type /login if asked, then close this window and press Refresh in UsageNotch. && claude";
            }
            Process.Start(info);
        }
        catch (Exception e) { System.Windows.MessageBox.Show(this, e.Message, "Sign in", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private UIElement DataEditor()
    {
        var history = _coordinator!.History; var tokens = _coordinator.Tokens;
        var stack = new StackPanel();
        var historyLine = Ui.Text("Reading…", 12.5, FontWeights.Normal, "Ui.Muted");
        var tokenLine = Ui.Text("", 12.5, FontWeights.Normal, "Ui.Muted");
        stack.Children.Add(Ui.Text("Usage history", 13.5, FontWeights.SemiBold));
        stack.Children.Add(historyLine);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 14) };
        var message = Ui.Text("", 12, FontWeights.Normal, "Ui.Accent");
        actions.Children.Add(Ui.Button("Compact", async () => { message.Text = "Compacting…"; var ok = await Task.Run(() => { lock (history) return history.Compact(); }); message.Text = ok ? "History compacted." : "History is busy; try again in a moment."; Load(); }, false, "\uE74C", Reduced).WithGap());
        actions.Children.Add(Ui.Button("Export CSV…", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"usagenotch-history-{DateTime.Now:yyyy-MM-dd}.csv", Filter = "CSV|*.csv" };
            if (dialog.ShowDialog(this) != true) return;
            try { int rows; lock (history) rows = history.ExportCsv(dialog.FileName); message.Text = $"Exported {rows:N0} readings."; }
            catch (Exception e) { message.Text = "Export failed: " + e.Message; }
        }, false, "\uEDE1", Reduced).WithGap());
        actions.Children.Add(Ui.Button("Delete history…", async () =>
        {
            var (_, counts, _) = await Task.Run(() => { lock (history) return (0L, history.Counts(), 0L); });
            var since = counts.Since is { } first ? $" since {first.ToLocalTime():MMM d, yyyy}" : "";
            var answer = System.Windows.MessageBox.Show(this,
                $"Delete all {counts.Readings:N0} readings{since} from this PC?\n\nHistory, all-time totals and This week start again from the next reading. Live usage keeps working. Your phone keeps its own copy until you delete it there.\n\nThis can't be undone. To keep a copy, choose Export CSV first.",
                "Delete usage history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            message.Text = "Deleting…";
            var ok = await Task.Run(() => _coordinator!.DeleteHistory());
            message.Text = ok ? "History deleted. New readings start a fresh history." : "History is busy; try again in a moment.";
            Load(); HistoryPageView.Refresh();
        }, false, "\uE74D", Reduced).WithGap());
        stack.Children.Add(actions);
        stack.Children.Add(Ui.Text("Local token history", 13.5, FontWeights.SemiBold));
        stack.Children.Add(tokenLine);
        var tokenActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        tokenActions.Children.Add(Ui.Button("Read new transcripts", async () => { message.Text = "Reading…"; await tokens.ScanAsync(_settings); message.Text = "Up to date."; Load(); }, false, "\uE72C", Reduced).WithGap());
        tokenActions.Children.Add(Ui.Button("Start over", async () => { tokens.Reset(); message.Text = "Token history cleared; reading transcripts again…"; await tokens.ScanAsync(_settings); message.Text = "Up to date."; Load(); }, false, "\uE777", Reduced).WithGap());
        stack.Children.Add(tokenActions);
        stack.Children.Add(message);
        async void Load()
        {
            Ui.EnsureContext(this);
            var (bytes, counts, tokenBytes) = await Task.Run(() => { lock (history) return (history.Bytes(), history.Counts(), tokens.DatabaseBytes()); });
            historyLine.Text = $"{counts.Readings:N0} readings and {counts.Events:N0} events · {Size(bytes)}" + (counts.Since is { } since ? $" · since {since.ToLocalTime():MMM d, yyyy}" : "") + ". Kept on this PC without automatic expiry.";
            tokenLine.Text = $"{Size(tokenBytes)}" + (tokens.LastScan is { } scan ? $" · last read {TimeDisplay.Clock(scan, _settings.Use24HourTime)}" : "") + (tokens.Status is { Length: > 0 } st ? " · " + st : "") + ". Only counts, model names and folder names are kept.";
        }
        Load();
        return stack;
    }

    private static string Size(long bytes) => bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.0} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, bytes / 1024):0} KB";
}

internal static class WorkspaceExtensions
{
    public static Border WithGap(this Border b) { b.Margin = new Thickness(0, 0, 8, 8); return b; }
}
