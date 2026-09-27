using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using UsageNotch.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using Slider = System.Windows.Controls.Slider;
using TextBlock = System.Windows.Controls.TextBlock;

namespace UsageNotch;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private string _dockColor = "#0B0B0E";
    private bool _suspend = true;

    /// <summary>Raised on every edit. The dock applies it immediately; nothing waits for Save.</summary>
    public event Action? SettingsChanged;

    /// <summary>True when the user pressed Save. Otherwise the caller restores its snapshot.</summary>
    public bool Committed { get; private set; }

    public SettingsWindow(AppSettings settings, UsageCoordinator? coordinator = null)
    {
        InitializeComponent();
        _settings = settings;
        if (coordinator is not null) StatsView.Connect(coordinator);
        AutoUpdateBox.IsChecked = settings.AutomaticUpdates;
        UpdateStatus.Text = (System.Windows.Application.Current as App)?.Updates.Status ?? "Update checks start with the running app.";
        if (System.Windows.Application.Current is App currentApp)
        {
            currentApp.Updates.Changed += UpdatesChanged;
            Closed += (_, _) => currentApp.Updates.Changed -= UpdatesChanged;
        }
        VersionLine.Text = "Your usage. Your way.  ·  " +
            (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } version ? $"{version.Major}.{version.Minor}" : "1.6");

        ClaudeBox.IsChecked = settings.ClaudeEnabled;
        CodexBox.IsChecked = settings.CodexEnabled;
        GeminiBox.IsChecked = settings.GeminiEnabled;
        CursorBox.IsChecked = settings.CursorEnabled;
        OpenAiBox.IsChecked = settings.OpenAiApiEnabled;
        AnthropicBox.IsChecked = settings.AnthropicApiEnabled;
        GeminiCloudBox.IsChecked = settings.GeminiUseCloudProject;

        TrayBox.IsChecked = settings.ShowTrayIcon;
        AlwaysVisibleBox.IsChecked = settings.AlwaysVisible;
        ClickThroughBox.IsChecked = settings.ClickThrough;
        StartupBox.IsChecked = settings.StartWithWindows || StartupService.IsEnabled();
        SnapBox.IsChecked = settings.SnapToEdges;
        PredictiveBox.IsChecked = settings.PredictiveDrag;
        AutoHideBox.IsChecked = settings.AutoHide;
        HotkeyBox.IsChecked = settings.HotkeyEnabled;
        ToggleHotkeyField.Text = settings.ToggleHotkey;
        RefreshHotkeyField.Text = settings.RefreshHotkey;

        AlertsBox.IsChecked = settings.AlertsEnabled;
        ResetAlertBox.IsChecked = settings.AlertOnReset;
        SoundBox.IsChecked = settings.AlertSound;
        AlertCardsBox.IsChecked = settings.AlertCards;
        QuietBox.IsChecked = settings.QuietHoursEnabled;
        QuietStartSlider.Value = settings.QuietStartHour;
        QuietEndSlider.Value = settings.QuietEndHour;
        if (System.Windows.Application.Current is App { Alerts: { } alerts }) AlertHistory.ItemsSource = alerts.History;
        UpdateSnoozeStatus();

        OpenAiBudget.Text = settings.OpenAiMonthlyBudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        AnthropicBudget.Text = settings.AnthropicMonthlyBudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        EdgeLeft.IsChecked = string.Equals(settings.Edge, "Left", StringComparison.OrdinalIgnoreCase);
        EdgeTop.IsChecked = settings.Edge == "Top";
        EdgeRight.IsChecked = EdgeLeft.IsChecked != true && EdgeTop.IsChecked != true;

        LoadAppearance(settings);
        WireSliders();
        AttachLiveEditing();
        _suspend = false;
        UpdatePreview();

        Height = Math.Min(880, SystemParameters.WorkArea.Height - 30);
        Width = Math.Min(1260, SystemParameters.WorkArea.Width - 30);
        if (!settings.ReviewSession) WindowPlacement.Restore(this);
        SourceInitialized += (_, _) =>
        {
            if (WindowMaterials.Apply(this, transient: false)) WindowSurface.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 16, 18, 24));
        };
        Closing += (_, _) => { if (!settings.ReviewSession && !_fullscreen) WindowPlacement.Save(this); };
        Loaded += SettingsWindow_Loaded;
        UpdateFooter.Text = "History stored locally · F11 full screen";
        UpdatesChanged();
    }

    // ---------------------------------------------------------------- live editing

    /// <summary>
    /// Attaches to every control in the logical tree, so a new control added to the XAML is
    /// live by construction and cannot be forgotten. Passwords are deliberately excluded:
    /// secrets are only re-encrypted on Save.
    /// </summary>
    private void AttachLiveEditing()
    {
        foreach (var node in LogicalDescendants(this))
        {
            switch (node)
            {
                case ToggleButton toggle:
                    toggle.Checked += OnLiveEdit;
                    toggle.Unchecked += OnLiveEdit;
                    break;
                case Slider slider:
                    slider.ValueChanged += OnLiveSlider;
                    break;
                case TextBox box:
                    box.TextChanged += OnLiveText;
                    break;
            }
        }
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            if (child is UsageNotch.Controls.StatsDashboard) continue;
            foreach (var nested in LogicalDescendants(child)) yield return nested;
        }
    }

    private void OnLiveEdit(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        if (sender == EdgeLeft || sender == EdgeRight || sender == EdgeTop)
        {
            if (((System.Windows.Controls.RadioButton)sender).IsChecked != true) return;
            _suspend = true;
            PositionSlider.Value = EdgeTop.IsChecked == true ? _settings.HorizontalPosition : _settings.VerticalPosition;
            FloatingBox.IsChecked = false;
            _suspend = false;
        }
        ApplyLive();
    }
    private void OnLiveSlider(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyLive();
    private void OnLiveText(object sender, TextChangedEventArgs e) => ApplyLive();

    private void ApplyLive()
    {
        if (_suspend) return;
        Collect();
        UpdatePreview();
        SettingsChanged?.Invoke();
    }

    /// <summary>Reads every control into the shared settings object. Used by live edits and by Save.</summary>
    private void Collect()
    {
        _settings.AutomaticUpdates = AutoUpdateBox.IsChecked == true;
        _settings.ClaudeEnabled = ClaudeBox.IsChecked == true;
        _settings.CodexEnabled = CodexBox.IsChecked == true;
        _settings.GeminiEnabled = GeminiBox.IsChecked == true;
        _settings.CursorEnabled = CursorBox.IsChecked == true;
        _settings.OpenAiApiEnabled = OpenAiBox.IsChecked == true;
        _settings.AnthropicApiEnabled = AnthropicBox.IsChecked == true;
        _settings.GeminiUseCloudProject = GeminiCloudBox.IsChecked == true;

        _settings.ShowTrayIcon = TrayBox.IsChecked == true;
        _settings.AlwaysVisible = AlwaysVisibleBox.IsChecked == true;
        _settings.ClickThrough = ClickThroughBox.IsChecked == true;
        _settings.StartWithWindows = StartupBox.IsChecked == true;
        _settings.SnapToEdges = SnapBox.IsChecked == true;
        _settings.PredictiveDrag = PredictiveBox.IsChecked == true;
        _settings.AutoHide = AutoHideBox.IsChecked == true;
        _settings.AutoHideDelayMs = (int)AutoHideSlider.Value;
        _settings.HotkeyEnabled = HotkeyBox.IsChecked == true;
        _settings.ToggleHotkey = ToggleHotkeyField.Text.Trim();
        _settings.RefreshHotkey = RefreshHotkeyField.Text.Trim();

        _settings.AlertsEnabled = AlertsBox.IsChecked == true;
        _settings.AlertOnReset = ResetAlertBox.IsChecked == true;
        _settings.AlertSound = SoundBox.IsChecked == true;
        _settings.AlertCards = AlertCardsBox.IsChecked == true;
        _settings.QuietHoursEnabled = QuietBox.IsChecked == true;
        _settings.QuietStartHour = (int)QuietStartSlider.Value;
        _settings.QuietEndHour = (int)QuietEndSlider.Value;
        _settings.AlertWarnPercent = (int)WarnSlider.Value;
        _settings.AlertCriticalPercent = (int)Math.Max(CriticalSlider.Value, WarnSlider.Value);

        _settings.Edge = EdgeTop.IsChecked == true ? "Top" : EdgeLeft.IsChecked == true ? "Left" : "Right";
        _settings.DisplayMode = ModeDual.IsChecked == true ? "Dual" : ModeWeekly.IsChecked == true ? "Weekly" : ModeHighest.IsChecked == true ? "Highest" : "Session";
        _settings.UiScale = ScaleSlider.Value;
        _settings.ProviderSpacing = SpacingSlider.Value;
        if (_settings.Edge == "Top") _settings.HorizontalPosition = PositionSlider.Value;
        else _settings.VerticalPosition = PositionSlider.Value;
        _settings.DockOpacity = OpacitySlider.Value;
        _settings.DockColor = _dockColor;
        _settings.FloatingDock = FloatingBox.IsChecked == true;
        _settings.GlassSurface = GlassBox.IsChecked == true;
        _settings.CompactMode = CompactBox.IsChecked == true;
        _settings.ShowPercentages = PercentBox.IsChecked == true;
        _settings.ShowRemaining = ValueRemaining.IsChecked == true;
        _settings.ShowAccountNames = AccountNamesBox.IsChecked == true;
        _settings.SleekMode = SleekBox.IsChecked == true;
        _settings.ShowProviderNames = NamesBox.IsChecked == true;
        _settings.ShowDockResetTimes = DockResetBox.IsChecked == true;
        _settings.ShowExpandedResetTimes = ExpandedResetBox.IsChecked == true;
        _settings.ShowSecondaryPercentage = SecondaryTextBox.IsChecked == true;
        _settings.ShowUsageSuffix = UsageSuffixBox.IsChecked == true;
        _settings.ShowWindowLabel = WindowLabelBox.IsChecked == true;
        _settings.ShowStatusBadge = SavedBadgeBox.IsChecked == true;
        _settings.Use24HourTime = Clock24.IsChecked == true;
        _settings.ShowClockSeconds = ClockSecondsBox.IsChecked == true;
        _settings.DetailedStats = DetailedStatsBox.IsChecked == true;
        _settings.ClaudeDockLabel = ClaudeLabelField.Text.Trim();
        _settings.CodexDockLabel = CodexLabelField.Text.Trim();
        _settings.GeminiDockLabel = GeminiLabelField.Text.Trim();
        _settings.CursorDockLabel = CursorLabelField.Text.Trim();
        _settings.ColoredLogos = LogoColorBox.IsChecked == true;
        _settings.ShowDashboardButton = DashboardBox.IsChecked == true;
        _settings.ReducedMotion = ReducedMotionBox.IsChecked == true;
        _settings.HoverDelayMs = (int)HoverSlider.Value;
        _settings.PollSeconds = (int)PollSlider.Value;
        _settings.OpenAiMonthlyBudgetUsd = ParseNullable(OpenAiBudget.Text);
        _settings.AnthropicMonthlyBudgetUsd = ParseNullable(AnthropicBudget.Text);
    }

    // ---------------------------------------------------------------- sliders

    private void WireSliders()
    {
        Bind(ScaleSlider, ScaleValue, value => $"{value * 100:0}%");
        Bind(SpacingSlider, SpacingValue, value => $"{value:0} px");
        Bind(PositionSlider, PositionValue, value => value is > 0.45 and < 0.55 ? "centre" : $"{value * 100:0}%");
        Bind(OpacitySlider, OpacityValue, value => $"{value * 100:0}%");
        Bind(HoverSlider, HoverValue, value => value < 1 ? "instant" : $"{value:0} ms");
        Bind(PollSlider, PollValue, value => value < 120 ? $"{value:0} s" : $"every {value / 60:0.#} min");
        Bind(AutoHideSlider, AutoHideValue, value => $"{value / 1000:0.##} s");
        Bind(WarnSlider, WarnValue, value => $"{value:0}%");
        Bind(CriticalSlider, CriticalValue, value => $"{value:0}%");
        Bind(QuietStartSlider, QuietStartValue, value => HourLabel((int)value));
        Bind(QuietEndSlider, QuietEndValue, value => HourLabel((int)value));
    }

    private static void Bind(Slider slider, TextBlock label, Func<double, string> format)
    {
        label.Text = format(slider.Value);
        slider.ValueChanged += (_, args) => label.Text = format(args.NewValue);
    }
    private string HourLabel(int hour) => _settings.Use24HourTime ? $"{hour:00}:00" : $"{(hour % 12 == 0 ? 12 : hour % 12)}:00 {(hour < 12 ? "AM" : "PM")}";

    /// <summary>Called by the dock when the vertical position changes by dragging.</summary>
    public void SyncFromDock()
    {
        var previous = _suspend;
        _suspend = true;
        PositionSlider.Value = Math.Clamp(_settings.Edge == "Top" ? _settings.HorizontalPosition : _settings.VerticalPosition, 0, 1);
        FloatingBox.IsChecked = _settings.FloatingDock;
        EdgeLeft.IsChecked = string.Equals(_settings.Edge, "Left", StringComparison.OrdinalIgnoreCase);
        EdgeTop.IsChecked = _settings.Edge == "Top";
        EdgeRight.IsChecked = EdgeLeft.IsChecked != true && EdgeTop.IsChecked != true;
        _suspend = previous;
    }

    // ---------------------------------------------------------------- preview

    private void Preview_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_settings is not null)
        {
            DockTextPreview.Update(_settings); AppearancePreview.Update(_settings);
            BehaviorPreview.Update(_settings); ClockPreview.Update(_settings);
            QuietStartValue.Text = HourLabel((int)QuietStartSlider.Value);
            QuietEndValue.Text = HourLabel((int)QuietEndSlider.Value);
        }
        if (_suspend) return;
        var dual = ModeDual.IsChecked == true;
        var used = ModeWeekly.IsChecked == true ? .34 : .73;
        var remaining = ValueRemaining.IsChecked == true;
        PreviewRing.Dual = dual;
        PreviewRing.Value = remaining ? 1 - used : used;
        PreviewRing.SecondaryValue = remaining ? .66 : .34;
        PreviewRing.Remaining = remaining;
        PreviewRing.ReducedMotion = ReducedMotionBox.IsChecked == true;
        PreviewLogo.Colored = LogoColorBox.IsChecked == true;
        PreviewHeadline.Text = $"{Math.Round((remaining ? 1 - used : used) * 100)}% {(remaining ? "left" : "used")}";
        PreviewCaption.Text = dual
            ? "Outer ring = session · inner ring = week"
            : ModeWeekly.IsChecked == true ? "The 7-day window only"
            : ModeHighest.IsChecked == true ? "Whichever limit is fullest right now"
            : "The current 5-hour session";
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SettingsRoot.Opacity = 1;
        SettingsScale.ScaleX = 1;
        SettingsScale.ScaleY = 1;
        if (Motion.IsReduced(_settings)) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SettingsRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        SettingsScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(330)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        SettingsScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(330)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    // ---------------------------------------------------------------- hotkeys

    private void Hotkey_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) box.Tag = box.Text;
    }

    private void Hotkey_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Text: "" } box && box.Tag is string previous) box.Text = previous;
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape) { Keyboard.ClearFocus(); return; }
        if (key is Key.Back or Key.Delete) { box.Text = ""; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) return;
        var chord = HotkeyService.Describe(modifiers, key);
        if (HotkeyService.TryParse(chord, out _, out _)) box.Text = chord;
    }

    // ---------------------------------------------------------------- commit

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Collect();
        if (!string.IsNullOrWhiteSpace(OpenAiKey.Password))
            _settings.OpenAiAdminKeyProtected = SecretStore.Protect(OpenAiKey.Password.Trim());
        if (!string.IsNullOrWhiteSpace(AnthropicKey.Password))
            _settings.AnthropicAdminKeyProtected = SecretStore.Protect(AnthropicKey.Password.Trim());
        Committed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Committed = false;
        Close();
    }

    private static double? ParseNullable(string text) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private void LoadAppearance(AppSettings settings)
    {
        ModeSession.IsChecked = settings.DisplayMode == "Session";
        ModeWeekly.IsChecked = settings.DisplayMode == "Weekly";
        ModeDual.IsChecked = settings.DisplayMode == "Dual";
        ModeHighest.IsChecked = settings.DisplayMode == "Highest";
        if (ModeSession.IsChecked != true && ModeWeekly.IsChecked != true && ModeDual.IsChecked != true && ModeHighest.IsChecked != true)
            ModeSession.IsChecked = true;

        ScaleSlider.Value = settings.UiScale;
        SpacingSlider.Value = settings.ProviderSpacing;
        PositionSlider.Value = Math.Clamp(settings.Edge == "Top" ? settings.HorizontalPosition : settings.VerticalPosition, 0, 1);
        OpacitySlider.Value = settings.DockOpacity;
        HoverSlider.Value = settings.HoverDelayMs;
        PollSlider.Value = settings.PollSeconds;
        AutoHideSlider.Value = Math.Clamp(settings.AutoHideDelayMs, 150, 4000);
        WarnSlider.Value = Math.Clamp(settings.AlertWarnPercent, 30, 95);
        CriticalSlider.Value = Math.Clamp(settings.AlertCriticalPercent, 50, 100);

        FloatingBox.IsChecked = settings.FloatingDock;
        GlassBox.IsChecked = settings.GlassSurface;
        CompactBox.IsChecked = settings.CompactMode;
        PercentBox.IsChecked = settings.ShowPercentages;
        ValueUsed.IsChecked = !settings.ShowRemaining;
        ValueRemaining.IsChecked = settings.ShowRemaining;
        AccountNamesBox.IsChecked = settings.ShowAccountNames;
        SleekBox.IsChecked = settings.SleekMode;
        NamesBox.IsChecked = settings.ShowProviderNames;
        DockResetBox.IsChecked = settings.ShowDockResetTimes;
        ExpandedResetBox.IsChecked = settings.ShowExpandedResetTimes;
        SecondaryTextBox.IsChecked = settings.ShowSecondaryPercentage;
        UsageSuffixBox.IsChecked = settings.ShowUsageSuffix;
        WindowLabelBox.IsChecked = settings.ShowWindowLabel;
        SavedBadgeBox.IsChecked = settings.ShowStatusBadge;
        Clock12.IsChecked = !settings.Use24HourTime;
        Clock24.IsChecked = settings.Use24HourTime;
        ClockSecondsBox.IsChecked = settings.ShowClockSeconds;
        DetailedStatsBox.IsChecked = settings.DetailedStats;
        ClaudeLabelField.Text = settings.ClaudeDockLabel;
        CodexLabelField.Text = settings.CodexDockLabel;
        GeminiLabelField.Text = settings.GeminiDockLabel;
        CursorLabelField.Text = settings.CursorDockLabel;
        LogoColorBox.IsChecked = settings.ColoredLogos;
        DashboardBox.IsChecked = settings.ShowDashboardButton;
        ReducedMotionBox.IsChecked = settings.ReducedMotion;
        SetDockColor(settings.DockColor);
    }

    private void RestoreAppearance_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings
        {
            DisplayMode = ModeDual.IsChecked == true ? "Dual" : ModeWeekly.IsChecked == true ? "Weekly" : ModeHighest.IsChecked == true ? "Highest" : "Session"
        };
        var previous = _suspend;
        _suspend = true;
        LoadAppearance(defaults);
        _suspend = previous;
        ApplyLive();
    }

    private void SetDockColor(string value)
    {
        var color = DockTheme.Parse(value);
        _dockColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ColorSwatch.Background = new System.Windows.Media.SolidColorBrush(color);
        ColorLabel.Text = _dockColor + (Palette.IsLight(color) ? " · dark text and icons" : " · light text and icons");
    }

    private void ColorPreset_Click(object sender, RoutedEventArgs e)
    {
        SetDockColor((string)((System.Windows.Controls.Button)sender).Tag);
        ApplyLive();
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        var color = DockTheme.Parse(_dockColor);
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B) };
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
            SetDockColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
            ApplyLive();
        }
        finally { owner.ReleaseHandle(); }
    }

    private void TestAlert_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App { Alerts: { } alerts }) alerts.Preview();
        else new AlertWindow(new AlertNotice("Claude · getting close", "Sample alert · 82% used · 18% left", false, "claude", .82, IsPreview: true), _settings) { Owner = this }.Show();
    }
    private void SnoozeAlerts_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlertsSnoozedUntil = DateTimeOffset.Now.AddHours(1);
        UpdateSnoozeStatus();
    }
    private void ResumeAlerts_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlertsSnoozedUntil = null;
        UpdateSnoozeStatus();
    }
    private void UpdateSnoozeStatus() => SnoozeStatus.Text = _settings.AlertsSnoozedUntil > DateTimeOffset.Now
        ? $"Snoozed until {TimeDisplay.Clock(_settings.AlertsSnoozedUntil.Value, _settings.Use24HourTime)}. Save changes to keep this setting." : "Not snoozed.";

    private void GeminiGuide_Click(object sender, RoutedEventArgs e) => System.Windows.MessageBox.Show(this,
        "1. Your Google sign-in worked. Google has disabled this older quota connection for your account.\n\n" +
        "2. Visit https://antigravity.google/download and install Google's Windows app.\n\n" +
        "3. Sign in with the same Google account that has Google AI Pro.\n\n" +
        "4. Open Antigravity Settings to see baseline quota usage. Set AI Credit Overages to Never if you do not want extra credits used.\n\n" +
        "5. These are Antigravity coding quotas, not your Gemini chat-app message allowance. UsageNotch does not yet read Antigravity quotas; you can hide Gemini under Providers.\n\n" +
        "Do not buy an API key or enable billing to repair this unsupported connection.",
        "Gemini: supported next steps", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ConnectGemini_Click(object sender, RoutedEventArgs e)
    {
        try { GeminiSignIn.Launch(); }
        catch (Exception exception) { System.Windows.MessageBox.Show(this, exception.Message, "Gemini sign-in", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Credits_Click(object sender, RoutedEventArgs e)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/UsageNotch;component/Assets/THIRD-PARTY-NOTICES.txt"))!.Stream;
        using var reader = new StreamReader(stream);
        System.Windows.MessageBox.Show(this, reader.ReadToEnd(), "Provider logo credits", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
        if (e.ClickCount == 2) { Maximize_Click(sender, e); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private static bool FindButton(DependencyObject source)
    {
        for (DependencyObject? node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }
    private bool _fullscreen;
    private Rect _beforeFullscreen;
    private WindowState _beforeState;
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) ToggleFullscreen();
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            if (!_settings.ReviewSession) WindowPlacement.Save(this);
            _beforeState = WindowState; _beforeFullscreen = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            WindowState = WindowState.Normal;
            var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).Bounds;
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var topLeft = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
            var bottomRight = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
            ResizeMode = ResizeMode.NoResize; Left = topLeft.X; Top = topLeft.Y; Width = bottomRight.X - topLeft.X; Height = bottomRight.Y - topLeft.Y;
            _fullscreen = true;
        }
        else
        {
            _fullscreen = false; ResizeMode = ResizeMode.CanResize;
            Left = _beforeFullscreen.Left; Top = _beforeFullscreen.Top; Width = _beforeFullscreen.Width; Height = _beforeFullscreen.Height; WindowState = _beforeState;
        }
    }
    public void ShowProvider(string id, System.Windows.Point? origin)
    {
        SettingsTabs.SelectedIndex = 0;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            var logo = StatsView.FocusProvider(id);
            if (logo is not null && origin is { } start && !Motion.IsReduced(_settings)) ConnectedTransition.Play(this, logo, id, start);
        }));
    }

    private void UpdatesChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (System.Windows.Application.Current is not App app) return;
        UpdateStatus.Text = app.Updates.Status;
        UpdateFooter.Text = app.Updates.Busy || app.Updates.Ready ? app.Updates.Status : "History stored locally · F11 full screen";
        UpdateProgress.Visibility = app.Updates.Busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.IsIndeterminate = app.Updates.Progress is null;
        UpdateProgress.Value = app.Updates.Progress ?? 0;
        RestartUpdateButton.IsEnabled = app.Updates.Ready;
    });
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app) await app.Updates.CheckAsync();
    }
    private void RestartUpdate_Click(object sender, RoutedEventArgs e)
    {
        // Commit visible edits before restarting; no settings are silently discarded.
        Save_Click(sender, e);
        if (Committed && System.Windows.Application.Current is App app) app.RestartForUpdate();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _fullscreen) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key != Key.Escape) return;
        if (ToggleHotkeyField.IsKeyboardFocused || RefreshHotkeyField.IsKeyboardFocused) return;
        Committed = false;
        Close();
    }
}
