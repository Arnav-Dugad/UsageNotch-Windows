using System.Windows;
using UsageNotch.Services;

namespace UsageNotch;

public partial class App : System.Windows.Application
{
    internal static string VersionLabel => "UsageNotch " + (typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "");
    public bool IsReviewSession { get; set; }
    private Mutex? _instanceMutex;
    private UsageCoordinator? _coordinator;
    private MainWindow? _mainWindow;
    private AppSettings? _settings;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private System.Drawing.Icon? _liveIcon;
    private string? _liveKey;
    private double? _lastTrayUsed;
    private System.Windows.Threading.DispatcherTimer? _pulse;
    private System.Windows.Forms.ToolStripMenuItem? _autoHideItem;
    private System.Windows.Forms.ToolStripMenuItem? _compactItem;
    private System.Windows.Forms.ToolStripMenuItem? _clickThroughItem;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _settingsSignal;
    private CancellationTokenSource? _signalStop;
    private AlertWindow? _alertWindow;
    private readonly Queue<AlertNotice> _alertQueue = new();
    private bool _exiting;
    public AlertService? Alerts => _coordinator?.Alerts;
    public UpdateService Updates { get; } = new();
    public Services.Phone.PhoneLinkService? Phone { get; private set; }
    /// <summary>Raised on the UI thread after a phone changed budgets, colours, names, order or alert settings.</summary>
    public event Action? SharedSettingsChanged;
    /// <summary>Raised on the UI thread after a phone added, changed or deleted a day note.</summary>
    public event Action? NotesChanged;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (IsReviewSession) return;
        if (await UpdateService.ApplyFromArgumentsAsync(e.Args)) { Shutdown(); return; }
        var firstRun = !SettingsStore.Exists();
        var settings = SettingsStore.Load();
        _settings = settings;

        var wantsSettings = e.Args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase));

        _instanceMutex = new Mutex(true, @"Local\UsageNotch.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Do not silently reveal an older copy when the user launches the new release.
            try
            {
                var current = Environment.ProcessPath;
                var other = System.Diagnostics.Process.GetProcessesByName("UsageNotch")
                    .FirstOrDefault(process => process.Id != Environment.ProcessId && !process.HasExited);
                if (other?.MainModule?.FileName is { } running && !string.Equals(running, current, StringComparison.OrdinalIgnoreCase))
                    System.Windows.MessageBox.Show("Another copy of UsageNotch is already running.\n\nRight-click its dock or tray icon and choose Quit UsageNotch, then open this copy again.\n\nThis copy: " + VersionLabel + "\nRunning from: " + running,
                        "Finish switching to " + VersionLabel, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
            // Launching UsageNotch again should bring the running dock back, not silently do nothing.
            Signal(wantsSettings ? SettingsSignalName : ShowSignalName);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        if (settings.AutomaticUpdates && Updates.TryInstallOnRestart()) { Shutdown(); return; }

        // Values set before 2.6 count as old, so a phone's newer choices win the first sync.
        if (SharedSettings.Capture(settings, 1)) SettingsStore.Save(settings);
        _coordinator = new UsageCoordinator(settings);
        if (settings.StartWithWindows || StartupService.IsEnabled()) StartupService.Apply(true);
        _coordinator.Alerts.Raised += ShowAlert;
        StartPhoneLink(settings);
        _mainWindow = new MainWindow(_coordinator, settings);
        _mainWindow.Show();

        _trayIcon = LoadAppIcon();
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = VersionLabel + " · AI usage at a glance",
            Icon = _trayIcon,
            Visible = settings.ShowTrayIcon
        };
        _tray.DoubleClick += (_, _) => _mainWindow.ToggleVisibility();
        _tray.ContextMenuStrip = BuildTrayMenu(settings);
        _coordinator.Refreshed += UpdateTray;

        StartSignalListener();
        _ = _coordinator.StartAsync();
        _ = Updates.RunAsync(settings);
        if (wantsSettings) _mainWindow.OpenSettings();
        else ShowWelcomeOrWhatsNew(settings, firstRun);
    }

    /// <summary>A welcome on the first launch, or "What's new" once after an update. Never both, never twice.</summary>
    private void ShowWelcomeOrWhatsNew(AppSettings settings, bool firstRun)
    {
        var version = typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}" : "";
        if (settings.LastSeenVersion == version || version.Length == 0) return;
        settings.LastSeenVersion = version;
        SettingsStore.Save(settings);
        var main = _mainWindow;
        // After the dock has appeared, so the sheet sits in front of it.
        _ = Dispatcher.BeginInvoke(() =>
        {
            try { new Controls.WhatsNewWindow(settings, firstRun, () => main?.OpenSettings(history: true), () => main?.OpenSettings()).Show(); } catch { }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private const string ShowSignalName = @"Local\UsageNotch.Show";
    private const string SettingsSignalName = @"Local\UsageNotch.Settings";

    private static void Signal(string name)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                handle.Set();
                handle.Dispose();
            }
        }
        catch { }
    }

    private void StartSignalListener()
    {
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsSignalName);
        }
        catch { return; }

        _signalStop = new CancellationTokenSource();
        var stop = _signalStop;
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showSignal, _settingsSignal, stop.Token.WaitHandle };
            while (!stop.IsCancellationRequested)
            {
                int index;
                try { index = WaitHandle.WaitAny(handles); }
                catch { return; }
                if (index == 2) return;
                var openSettings = index == 1;
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _mainWindow?.RevealFromAnotherInstance();
                        if (openSettings) _mainWindow?.OpenSettings();
                    });
                }
                catch { return; }
            }
        })
        { IsBackground = true, Name = "UsageNotch signal listener" };
        thread.Start();
    }

    private System.Windows.Forms.ContextMenuStrip BuildTrayMenu(AppSettings settings)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(VersionLabel) { Enabled = false });
        menu.Items.Add("Show / hide dock", null, (_, _) => _mainWindow?.ToggleVisibility());
        menu.Items.Add("Refresh now", null, async (_, _) =>
        {
            if (_coordinator is not null) await _coordinator.RefreshAllAsync();
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var compact = new System.Windows.Forms.ToolStripMenuItem("Compact dock") { CheckOnClick = true, Checked = settings.CompactMode };
        compact.Click += (_, _) =>
        {
            settings.CompactMode = compact.Checked;
            SettingsStore.Save(settings);
            if (_coordinator is not null)
                foreach (var item in _coordinator.Items) item.RefreshAppearance();
            _mainWindow?.RelayoutFromTray();
        };
        _compactItem = compact;
        menu.Items.Add(compact);

        var autoHide = new System.Windows.Forms.ToolStripMenuItem("Hide until hover") { CheckOnClick = true, Checked = settings.AutoHide };
        autoHide.Click += (_, _) => _mainWindow?.SetAutoHide(autoHide.Checked);
        _autoHideItem = autoHide;
        menu.Items.Add(autoHide);

        var clickThrough = new System.Windows.Forms.ToolStripMenuItem("Click through") { CheckOnClick = true, Checked = settings.ClickThrough };
        clickThrough.Click += (_, _) => _mainWindow?.SetClickThrough(clickThrough.Checked);
        _clickThroughItem = clickThrough;
        menu.Items.Add(clickThrough);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Stats & Settings…", null, (_, _) => _mainWindow?.OpenSettings());
        menu.Items.Add("Pair a phone…", null, (_, _) => _mainWindow?.OpenSettings(phone: true));
        menu.Items.Add("Quit UsageNotch", null, (_, _) => Shutdown());
        return menu;
    }

    private void ShowAlert(AlertNotice notice)
    {
        if (_settings is null || (!notice.IsPreview && !_settings.AlertsEnabled)) return;
        if (_settings.AlertCards || notice.IsPreview)
        {
            if (_alertQueue.Count >= 4) _alertQueue.Dequeue();
            _alertQueue.Enqueue(notice);
            ShowNextAlert();
            return;
        }
        if (_tray is null) return;
        if (!_tray.Visible)
        {
            // Balloon tips need a visible icon; show it briefly so the alert is not lost.
            _tray.Visible = true;
            _ = RestoreTrayVisibility();
        }
        _tray.BalloonTipTitle = notice.Title;
        _tray.BalloonTipText = notice.Message;
        _tray.BalloonTipIcon = notice.Critical
            ? System.Windows.Forms.ToolTipIcon.Warning
            : System.Windows.Forms.ToolTipIcon.Info;
        _tray.ShowBalloonTip(notice.Critical ? 12000 : 7000);
        if (_settings.AlertSound) System.Media.SystemSounds.Exclamation.Play();
    }

    private void ShowNextAlert()
    {
        if (_exiting || _alertWindow is not null || _settings is null) return;
        while (_alertQueue.TryDequeue(out var notice))
        {
            if (!notice.IsPreview && Alerts?.IsQuiet(DateTimeOffset.Now) == true) continue;
            _alertWindow = new AlertWindow(notice, _settings);
            _alertWindow.Snoozed += () => { Alerts?.Snooze(TimeSpan.FromHours(1)); _alertQueue.Clear(); };
            _alertWindow.Closed += (_, _) => { _alertWindow = null; Dispatcher.BeginInvoke(ShowNextAlert); };
            _alertWindow.Show();
            if (_settings.AlertSound) System.Media.SystemSounds.Exclamation.Play();
            break;
        }
    }

    private async Task RestoreTrayVisibility()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (_tray is not null && _settings is not null) _tray.Visible = _settings.ShowTrayIcon;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        Updates.Dispose();
        _alertQueue.Clear();
        _alertWindow?.Close();
        _signalStop?.Cancel();
        _signalStop?.Dispose();
        _showSignal?.Dispose();
        _settingsSignal?.Dispose();
        if (_coordinator is not null) _coordinator.Alerts.Raised -= ShowAlert;
        _coordinator?.Dispose();
        // Stop listening before exit so the port is free for the next launch or an update restart.
        if (Phone is not null) { try { Task.Run(() => Phone.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3)); } catch { } }
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
        _liveIcon?.Dispose();
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>
    /// The phone link is built in. It resumes when it was on last time, including in the standalone
    /// UsageNotch Link it replaces (same identity and preferences), so paired phones keep working.
    /// </summary>
    private void StartPhoneLink(AppSettings settings)
    {
        if (_coordinator is null) return;
        try { Phone = new Services.Phone.PhoneLinkService(settings, _coordinator.History); }
        catch { return; }
        var phone = Phone;
        var coordinator = _coordinator;
        _coordinator.Refreshed += () => phone.Update(_coordinator.Items.Select(item => item.Snapshot).ToList());
        phone.RefreshRequested = coordinator.RefreshForPhoneAsync;
        phone.ExtrasProvider = () => new Services.Phone.PhoneSnapshot.Extras(false, coordinator.TokenReports(), coordinator.Health.Active, settings.ShareProjectsWithPhone,
            Report: coordinator.WeekReportFor(settings.ShareProjectsWithPhone), AllTime: coordinator.AllTimeFor,
            Score: coordinator.Score(), Facts: coordinator.FactsFor, ProjectBudgets: settings.ProjectBudgets);
        phone.NotesChanged += () => { coordinator.NotesChanged(); Dispatcher.BeginInvoke(() => NotesChanged?.Invoke()); };
        phone.SettingsReceived = shared => Dispatcher.Invoke(() =>
        {
            var now = DateTimeOffset.UtcNow;
            SharedSettings.Capture(settings, now.ToUnixTimeMilliseconds());
            var order = string.Join(",", settings.ProviderOrder);
            if (SharedSettings.Merge(settings, shared, now))
            {
                SharedSettings.Apply(settings);
                if (!settings.ReviewSession) SettingsStore.Save(settings);
                if (order != string.Join(",", settings.ProviderOrder)) coordinator.RebuildProviders();
                foreach (var item in coordinator.Items) item.RefreshAppearance();
                SharedSettingsChanged?.Invoke();
            }
            return SharedSettings.ToJson(settings);
        });
        if (!phone.ResumeOnLaunch) return;
        _ = Task.Run(async () =>
        {
            if (await phone.StartAsync() || !Services.Phone.PhoneLinkService.LegacyLinkRunning() || phone.TrayNoticeShown) return;
            phone.TrayNoticeShown = true;
            await Dispatcher.InvokeAsync(() => _tray?.ShowBalloonTip(8000, "Phone sharing is now built in",
                "UsageNotch Link is still running. Open Settings → Phone and choose Switch to built-in to use one app.", System.Windows.Forms.ToolTipIcon.Info));
        });
    }

    /// <summary>Redraws the tray ring and tooltip after each refresh; only when the number or colours change.</summary>
    private void UpdateTray()
    {
        if (_tray is null || _coordinator is null || _settings is null) return;
        try
        {
            _tray.Text = Services.TrayRing.Tooltip(_coordinator.Items, VersionLabel);
            var primary = _settings.LiveTrayIcon ? Services.TrayRing.Primary(_coordinator.Items) : null;
            var dark = Services.TrayRing.TaskbarDark();
            var key = primary is null ? null : $"{Math.Round(primary.UsedFraction!.Value * 100)}|{_settings.ShowRemaining}|{dark}";
            // Crossing your warning level: the ring glows three times, without taking focus or making a sound.
            var warn = Math.Clamp(_settings.AlertWarnPercent, 1, 100) / 100d;
            if (primary?.UsedFraction is { } now && _lastTrayUsed is { } before && before < warn && now >= warn) Pulse(now, dark);
            _lastTrayUsed = primary?.UsedFraction;
            if (key == _liveKey) return;
            var old = _liveIcon;
            _liveIcon = primary is null ? null : Services.TrayRing.Draw(primary.UsedFraction!.Value, _settings.ShowRemaining, dark);
            _tray.Icon = _liveIcon ?? _trayIcon;
            _liveKey = key;
            old?.Dispose();
        }
        catch { }
    }

    private void Pulse(double used, bool dark)
    {
        if (_tray is null || _settings is null) return;
        _pulse?.Stop();
        var frame = 0; const int frames = 36; // three pulses of 12 frames, 50 ms apart
        _pulse = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _pulse.Tick += (_, _) =>
        {
            if (_tray is null || _settings is null) { _pulse?.Stop(); return; }
            var old = _liveIcon;
            if (++frame > frames)
            {
                _pulse!.Stop();
                _liveIcon = Services.TrayRing.Draw(used, _settings.ShowRemaining, dark);
            }
            else _liveIcon = Services.TrayRing.Draw(used, _settings.ShowRemaining, dark, frame % 12 / 12d);
            _tray.Icon = _liveIcon;
            old?.Dispose();
        };
        _pulse.Start();
    }

    public void ApplySettings(AppSettings settings)
    {
        if (_tray is not null) _tray.Visible = settings.ShowTrayIcon;
        UpdateTray();
        if (_mainWindow is not null) _mainWindow.Topmost = settings.AlwaysVisible;
        if (_autoHideItem is not null) _autoHideItem.Checked = settings.AutoHide;
        if (_compactItem is not null) _compactItem.Checked = settings.CompactMode;
        if (_clickThroughItem is not null) _clickThroughItem.Checked = settings.ClickThrough;
    }

    public void RestartForUpdate()
    {
        if (Updates.TryInstallOnRestart()) Shutdown();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/UsageNotch.ico"));
            if (resource is not null)
            {
                using var icon = new System.Drawing.Icon(resource.Stream);
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch { }
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }
}
