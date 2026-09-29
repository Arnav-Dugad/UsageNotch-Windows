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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (IsReviewSession) return;
        if (await UpdateService.ApplyFromArgumentsAsync(e.Args)) { Shutdown(); return; }
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

        StartSignalListener();
        _ = _coordinator.StartAsync();
        _ = Updates.RunAsync(settings);
        if (wantsSettings) _mainWindow.OpenSettings();
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
        _coordinator.Refreshed += () => phone.Update(_coordinator.Items.Select(item => item.Snapshot).ToList());
        if (!phone.ResumeOnLaunch) return;
        _ = Task.Run(async () =>
        {
            if (await phone.StartAsync() || !Services.Phone.PhoneLinkService.LegacyLinkRunning() || phone.TrayNoticeShown) return;
            phone.TrayNoticeShown = true;
            await Dispatcher.InvokeAsync(() => _tray?.ShowBalloonTip(8000, "Phone sharing is now built in",
                "UsageNotch Link is still running. Open Settings → Phone and choose Switch to built-in to use one app.", System.Windows.Forms.ToolTipIcon.Info));
        });
    }

    public void ApplySettings(AppSettings settings)
    {
        if (_tray is not null) _tray.Visible = settings.ShowTrayIcon;
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
