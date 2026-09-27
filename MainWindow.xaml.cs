using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UsageNotch.Models;
using UsageNotch.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace UsageNotch;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;

    /// <summary>Pull strength at which releasing the dock commits to the edge.</summary>
    private const double SnapCommit = 0.45;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);

    private static readonly QuinticEase FluidEase = new() { EasingMode = EasingMode.EaseOut };
    private static readonly BackEase SettleEase = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };

    private readonly UsageCoordinator _coordinator;
    private readonly AppSettings _settings;
    private readonly HotkeyService _hotkeys = new();
    private readonly DispatcherTimer _closePopupTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _autoHideTimer = new();
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private bool ReduceMotion => Motion.IsReduced(_settings);
    private bool _layoutUpdateQueued;
    private bool _closed;
    private bool _insideCell;
    private bool _insidePopup;
    private bool _tucked;
    private bool _settingsOpen;
    private bool _menuOpen;
    private bool _dragEdgeTop;
    private double? _dropCentreX;
    private int _popupAnimationGeneration;
    private int? _magnifiedIndex;
    private Border? _pendingCell;
    private SettingsWindow? _settingsWindow;
    private string _providerSignature = "";

    private Point? _dragStart;
    private Point _dragWindowStart;
    private bool _dragging;
    private double _dragPull;
    private bool _dragEdgeLeft;
    private readonly DockIntent _dragIntent = new();
    private readonly Stopwatch _dragClock = new();
    private Point _dragFreePosition;
    private double _lastDragFrame;
    private TimeSpan? _lastRenderingTime;
    private DateTime _hiddenUntil = DateTime.MinValue;

    public MainWindow(UsageCoordinator coordinator, AppSettings settings)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _settings = settings;
        Title = App.VersionLabel;
        _providerSignature = ProviderSignature();
        DockTheme.Apply(Resources, settings.DockColor);
        DataContext = coordinator;
        Topmost = settings.AlwaysVisible;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        SourceInitialized += (_, _) => ApplyClickThrough();
        MouseEnter += Window_MouseEnter;
        MouseLeave += Window_MouseLeave;

        _coordinator.Items.CollectionChanged += Items_CollectionChanged;
        _coordinator.Refreshed += Coordinator_Refreshed;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;

        _closePopupTimer.Tick += ClosePopupTimer_Tick;
        _autoHideTimer.Tick += AutoHideTimer_Tick;
        _tickTimer.Tick += (_, _) => RefreshRelativeTimes();
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_insideCell && _pendingCell?.DataContext is ProviderViewModel viewModel)
                ShowDetail(_pendingCell, viewModel);
        };
        DetailPopup.Opened += (_, _) => CompositionTarget.Rendering += AlignPopupTail;
        DetailPopup.Closed += (_, _) => CompositionTarget.Rendering -= AlignPopupTail;
    }

    // ---------------------------------------------------------------- lifecycle

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Notch.Cursor = System.Windows.Input.Cursors.SizeAll;
        Dock(animate: false);
        _hotkeys.Attach(this);
        ApplyHotkeys();
        _tickTimer.Start();

        if (ReduceMotion)
        {
            Root.Opacity = 1;
        }
        else
        {
            if (IsTopDock) RootSlide.Y = -24;
            else RootSlide.X = IsLeftEdge ? -30 : 30;
            Root.Opacity = 1;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380))
            { EasingFunction = FluidEase, FillBehavior = FillBehavior.Stop });
            AnimateSlide(0, 620);
            PlayEntrance();
        }
        if (_settings.AutoHide) ScheduleTuck(immediate: false);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _coordinator.Items.CollectionChanged -= Items_CollectionChanged;
        _coordinator.Refreshed -= Coordinator_Refreshed;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _hoverTimer.Stop();
        _closePopupTimer.Stop();
        _autoHideTimer.Stop();
        _tickTimer.Stop();
        _hotkeys.Dispose();
        CompositionTarget.Rendering -= AlignPopupTail;
        CompositionTarget.Rendering -= RenderDrag;
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => { Dock(animate: true); PlayEntrance(); }, DispatcherPriority.Loaded);

    private void Coordinator_Refreshed()
    {
        RefreshRelativeTimes();
        if (DetailPopup.IsOpen && HoverInspector.IsExpanded) LoadInspector();
        // Saved/unknown states can change label height after the initial loading layout.
        Dock(animate: true);
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e) => Dock(animate: true);

    private void RefreshRelativeTimes()
    {
        foreach (var item in _coordinator.Items) item.RefreshClock();
    }

    private string ProviderSignature() =>
        $"{_settings.ClaudeEnabled}{_settings.CodexEnabled}{_settings.GeminiEnabled}{_settings.CursorEnabled}{_settings.OpenAiApiEnabled}{_settings.AnthropicApiEnabled}{_settings.GeminiUseCloudProject}";

    // ---------------------------------------------------------------- geometry helpers

    private bool IsTopDock => string.Equals(_settings.Edge, "Top", StringComparison.OrdinalIgnoreCase);
    private bool IsLeftEdge => !IsTopDock && (_settings.FloatingDock
        ? (_settings.DockLeft ?? (double.IsFinite(Left) ? Left : SystemParameters.WorkArea.Right - Width)) + Width / 2 < CurrentWorkArea().Left + CurrentWorkArea().Width / 2
        : string.Equals(_settings.Edge, "Left", StringComparison.OrdinalIgnoreCase));

    private Rect ScreenAreaFor(double left, double top)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
                (int)(left * dpi.DpiScaleX), (int)(top * dpi.DpiScaleY)));
            var area = screen.WorkingArea;
            return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
        }
        catch { return SystemParameters.WorkArea; }
    }

    private Rect CurrentWorkArea()
    {
        var x = _settings.DockLeft ?? (double.IsFinite(Left) ? Left : SystemParameters.WorkArea.Right - Width);
        var y = _settings.DockTop ?? (double.IsFinite(Top) ? Top : SystemParameters.WorkArea.Top);
        return ScreenAreaFor(x, y);
    }

    private static double Smoothstep(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    // ---------------------------------------------------------------- layout

    private void Dock(bool animate, int duration = 430)
    {
        if (_dragging) return;
        DockTheme.Apply(Resources, _settings.DockColor);

        var workArea = CurrentWorkArea();
        var compact = _settings.CompactMode;
        var scale = Math.Clamp(_settings.UiScale, 0.75, 1.5);
        var baseWidth = compact ? 68 : _settings.SleekMode ? 76 : 100;
        if (FindVisual<System.Windows.Controls.Primitives.UniformGrid>(ProviderList) is { } panel)
        {
            panel.Rows = IsTopDock ? 1 : 0;
            panel.Columns = IsTopDock ? 0 : 1;
        }
        // Measure the actual templates. Uniform rows keep the ring centres evenly spaced,
        // including mixed loading, unknown, weekly and saved-reading states.
        ProviderList.LayoutTransform = Transform.Identity;
        ProviderList.Measure(new System.Windows.Size(IsTopDock ? double.PositiveInfinity : baseWidth, double.PositiveInfinity));
        var shoulder = _settings.FloatingDock ? (compact ? 16 : _settings.SleekMode ? 20 : 26) : (compact ? 38 : _settings.SleekMode ? 48 : 58);
        var baseHeight = Math.Max(IsTopDock ? 64 : 120, ProviderList.DesiredSize.Height + (IsTopDock ? 24 : shoulder * 2));
        var baseLength = IsTopDock ? Math.Max(120, ProviderList.DesiredSize.Width + shoulder * 2) : baseWidth;
        scale *= Math.Min(1, Math.Min(workArea.Height / (baseHeight * scale), workArea.Width / (baseLength * scale)));
        var targetHeight = baseHeight * scale;
        var targetWidth = baseLength * scale;
        var targetLeft = IsLeftEdge ? workArea.Left : workArea.Right - targetWidth;
        var targetTop = workArea.Top + Math.Max(0, workArea.Height - targetHeight) * Math.Clamp(_settings.VerticalPosition, 0, 1);
        if (IsTopDock)
        {
            targetLeft = workArea.Left + Math.Max(0, workArea.Width - targetWidth) * Math.Clamp(_settings.HorizontalPosition, 0, 1);
            if (_dropCentreX is { } centre)
            {
                targetLeft = Math.Clamp(centre - targetWidth / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - targetWidth));
                _settings.HorizontalPosition = Math.Clamp((targetLeft - workArea.Left) / Math.Max(1, workArea.Width - targetWidth), 0, 1);
                _dropCentreX = null;
            }
            targetTop = workArea.Top;
        }

        if (_settings.FloatingDock)
        {
            targetLeft = Math.Clamp(_settings.DockLeft ?? targetLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - targetWidth));
            targetTop = Math.Clamp(_settings.DockTop ?? targetTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - targetHeight));
        }

        ProviderList.LayoutTransform = new ScaleTransform(scale, scale);
        DockShell.RenderTransformOrigin = IsTopDock ? new Point(.5, 0) : new Point(IsLeftEdge ? 0 : 1, 0.5);
        DockBackground.LeftEdge = IsLeftEdge;
        DockBackground.TopEdge = IsTopDock;
        DockBackground.ShapeScale = scale;
        DockBackground.Glass = _settings.GlassSurface;
        DockBackground.Opacity = Math.Clamp(_settings.DockOpacity, 0.3, 1);
        Notch.Padding = IsTopDock ? new Thickness(shoulder * scale, 12 * scale, shoulder * scale, 12 * scale) : new Thickness(0, shoulder * scale, 0, shoulder * scale);
        Width = targetWidth;
        CardScroll.MaxHeight = Math.Max(100, workArea.Height - (IsTopDock ? targetHeight + 60 : 60));

        PeekStrip.HorizontalAlignment = IsLeftEdge ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        if (IsTopDock) PeekStrip.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        PeekStrip.VerticalAlignment = IsTopDock ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        PeekStrip.Width = IsTopDock ? double.NaN : 7;
        PeekStrip.Height = IsTopDock ? 7 : double.NaN;
        PeekStrip.Visibility = _settings.AutoHide ? Visibility.Visible : Visibility.Collapsed;

        DetailPopup.Placement = PlacementMode.Custom;
        DetailPopup.HorizontalOffset = 0;
        DetailPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
        {
            if (IsTopDock)
                return [new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height - 4), PopupPrimaryAxis.Horizontal)];
            var anchorY = targetSize.Height / 2;
            if (DetailPopup.PlacementTarget is FrameworkElement cell && FindVisual<Controls.ProgressRing>(cell) is { } ring && cell.ActualHeight > 0)
                anchorY = ring.TransformToAncestor(cell).Transform(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2)).Y * targetSize.Height / cell.ActualHeight;
            return [new CustomPopupPlacement(new Point(IsLeftEdge ? targetSize.Width - 9 : -popupSize.Width + 9,
                anchorY - popupSize.Height / 2), PopupPrimaryAxis.Vertical)];
        };
        PopupBody.Margin = IsLeftEdge ? new Thickness(30, 14, 14, 14) : new Thickness(14, 14, 30, 14);
        PopupTail.HorizontalAlignment = IsLeftEdge ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        PopupTail.Margin = IsLeftEdge ? new Thickness(5, 0, 0, 0) : new Thickness(0, 0, 5, 0);
        PopupTail.RenderTransformOrigin = new Point(0.5, 0.5);
        PopupTail.RenderTransform = new ScaleTransform(IsLeftEdge ? -1 : 1, 1);
        PopupCard.RenderTransformOrigin = IsLeftEdge ? new Point(0.06, 0.5) : new Point(0.94, 0.5);
        PopupTail.Width = IsTopDock ? 46 : 26;
        PopupTail.Height = IsTopDock ? 22 : 46;
        PopupTail.Data = Geometry.Parse(IsTopDock ? "M0,22 C10,20 17,9 23,0 C29,9 36,20 46,22 Z" : "M0,0 C4,13 11,18 30,25 C11,32 4,37 0,50 Z");
        if (IsTopDock)
        {
            PopupBody.Margin = new Thickness(14, 30, 14, 14);
            PopupTail.RenderTransform = Transform.Identity;
            PopupTail.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            PopupCard.RenderTransformOrigin = new Point(.5, 0);
        }

        SetMorph(_settings.FloatingDock ? 0 : 1, animate && IsLoaded && !ReduceMotion);

        if (!animate || !IsLoaded || ReduceMotion)
        {
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Width = targetWidth;
            Height = targetHeight;
            Left = targetLeft;
            Top = targetTop;
        }
        else
        {
            AnimateWindowProperty(HeightProperty, targetHeight, duration, FluidEase);
            AnimateWindowProperty(LeftProperty, targetLeft, duration, SettleEase);
            AnimateWindowProperty(TopProperty, targetTop, duration, FluidEase);
        }

        if (_tucked) AnimateSlide(TuckOffset(), 0);
    }

    private void AnimateWindowProperty(DependencyProperty property, double target, int milliseconds, IEasingFunction ease)
    {
        var current = (double)GetValue(property);
        SetCurrentValue(property, target);
        if (Math.Abs(current - target) < 0.5) { BeginAnimation(property, null); return; }
        BeginAnimation(property, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetMorph(double target, bool animate)
    {
        if (!animate)
        {
            DockBackground.BeginAnimation(Controls.DockSurface.MorphProperty, null);
            DockBackground.Morph = target;
            return;
        }
        if (Math.Abs(DockBackground.Morph - target) < 0.002) return;
        var from = DockBackground.Morph;
        DockBackground.Morph = target;
        DockBackground.BeginAnimation(Controls.DockSurface.MorphProperty,
            new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(460))
            { EasingFunction = FluidEase, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private void PlayEntrance()
    {
        if (ReduceMotion || !IsLoaded) return;
        Dispatcher.BeginInvoke(() =>
        {
            for (var i = 0; i < ProviderList.Items.Count; i++)
            {
                if (ProviderList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
                var transforms = EnsureTransforms(container);
                var delay = TimeSpan.FromMilliseconds(55 * i);
                container.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340))
                { BeginTime = delay, EasingFunction = FluidEase, FillBehavior = FillBehavior.Stop });
                transforms.Translate.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(IsLeftEdge ? -20 : 20, 0, TimeSpan.FromMilliseconds(560))
                    { BeginTime = delay, EasingFunction = FluidEase, FillBehavior = FillBehavior.Stop });
            }
        }, DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- dock magnification

    private sealed record CellTransforms(ScaleTransform Scale, TranslateTransform Translate);

    private static CellTransforms EnsureTransforms(FrameworkElement container)
    {
        container.RenderTransformOrigin = new Point(0.5, 0.5);
        if (container.RenderTransform is TransformGroup existing
            && existing.Children.Count == 2
            && existing.Children[0] is ScaleTransform scale
            && existing.Children[1] is TranslateTransform translate)
            return new CellTransforms(scale, translate);

        var newScale = new ScaleTransform(1, 1);
        var newTranslate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(newScale);
        group.Children.Add(newTranslate);
        container.RenderTransform = group;
        return new CellTransforms(newScale, newTranslate);
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void AlignPopupTail(object? sender, EventArgs e)
    {
        if (!DetailPopup.IsOpen || DetailPopup.PlacementTarget is not FrameworkElement cell || PopupCard.ActualHeight < 90) return;
        var ring = FindVisual<Controls.ProgressRing>(cell);
        if (ring is null || PresentationSource.FromVisual(ring) is null || PresentationSource.FromVisual(PopupCard) is null) return;
        var centre = PopupCard.PointFromScreen(ring.PointToScreen(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2)));
        if (IsTopDock)
        {
            var x = Math.Clamp(centre.X - PopupTail.Width / 2, 32, Math.Max(32, PopupCard.ActualWidth - PopupTail.Width - 32));
            PopupTail.VerticalAlignment = VerticalAlignment.Top;
            PopupTail.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            PopupTail.Margin = new Thickness(x, 9, 0, 0);
            return;
        }
        var y = Math.Clamp(centre.Y - PopupTail.Height / 2, 32, Math.Max(32, PopupCard.ActualHeight - PopupTail.Height - 32));
        PopupTail.VerticalAlignment = VerticalAlignment.Top;
        var margin = IsLeftEdge ? new Thickness(5, y, 0, 0) : new Thickness(0, y, 5, 0);
        if (Math.Abs(PopupTail.Margin.Top - y) > .25) PopupTail.Margin = margin;
    }

    /// <summary>Neighbour-aware magnification, so the whole dock leans toward the pointer.</summary>
    private void ApplyMagnification(int? hovered, bool force = false)
    {
        if (!force && _magnifiedIndex == hovered) return;
        _magnifiedIndex = hovered;
        for (var i = 0; i < ProviderList.Items.Count; i++)
        {
            if (ProviderList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            var ring = FindVisual<Controls.ProgressRing>(container);
            if (ring?.Parent is not FrameworkElement ringHost) continue;
            var transforms = EnsureTransforms(ringHost);
            var distance = hovered is { } index ? Math.Abs(i - index) : int.MaxValue;
            var weight = distance switch { 0 => 1.0, 1 => 0.40, 2 => 0.13, _ => 0 };
            var scale = 1 + 0.10 * weight;
            var lean = _settings.FloatingDock || IsTopDock ? 0 : (IsLeftEdge ? 1 : -1) * 2 * weight;

            if (ReduceMotion)
            {
                transforms.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transforms.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                transforms.Translate.BeginAnimation(TranslateTransform.XProperty, null);
                transforms.Scale.ScaleX = transforms.Scale.ScaleY = 1;
                transforms.Translate.X = 0;
                continue;
            }

            var duration = TimeSpan.FromMilliseconds(weight > 0 ? 320 : 380);
            var ease = weight > 0 ? (IEasingFunction)SettleEase : FluidEase;
            transforms.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(scale, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
            transforms.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(scale, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
            transforms.Translate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(lean, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        }
    }

    // ---------------------------------------------------------------- auto-hide

    private double TuckOffset()
    {
        if (IsTopDock) return -Math.Max(0, Height - 8);
        var travel = Math.Max(0, Width - 8);
        return IsLeftEdge ? -travel : travel;
    }

    private void AnimateSlide(double target, int milliseconds)
    {
        var axis = IsTopDock ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var other = IsTopDock ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        RootSlide.BeginAnimation(other, null);
        RootSlide.SetValue(other, 0d);
        if (ReduceMotion || milliseconds <= 0)
        {
            RootSlide.BeginAnimation(axis, null);
            RootSlide.SetValue(axis, target);
            return;
        }
        var from = (double)RootSlide.GetValue(axis);
        RootSlide.SetValue(axis, target);
        RootSlide.BeginAnimation(axis,
            new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(milliseconds))
            { EasingFunction = FluidEase, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private void ScheduleTuck(bool immediate)
    {
        _autoHideTimer.Stop();
        if (!_settings.AutoHide || _dragging || _settingsOpen || _menuOpen) return;
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(immediate ? 1 : Math.Clamp(_settings.AutoHideDelayMs, 150, 8000));
        _autoHideTimer.Start();
    }

    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        if (!_settings.AutoHide || IsMouseOver || DetailPopup.IsOpen || _dragging || _settingsOpen || _menuOpen) return;
        _tucked = true;
        AnimateSlide(TuckOffset(), 460);
    }

    private void Untuck()
    {
        _autoHideTimer.Stop();
        if (!_tucked) return;
        _tucked = false;
        AnimateSlide(0, 380);
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) => Untuck();

    private void Window_MouseLeave(object sender, MouseEventArgs e) => ScheduleTuck(immediate: false);

    public void SetAutoHide(bool enabled)
    {
        _settings.AutoHide = enabled;
        SettingsStore.Save(_settings);
        PeekStrip.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled) ScheduleTuck(immediate: false);
        else Untuck();
    }

    // ---------------------------------------------------------------- click-through

    public void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, _settings.ClickThrough ? style | WsExTransparent : style & ~WsExTransparent);
    }

    public void SetClickThrough(bool enabled)
    {
        _settings.ClickThrough = enabled;
        SettingsStore.Save(_settings);
        if (enabled) HideDetail();
        ApplyClickThrough();
    }

    // ---------------------------------------------------------------- hotkeys

    public void ApplyHotkeys()
    {
        _hotkeys.Clear();
        if (!_settings.HotkeyEnabled) return;
        _hotkeys.Register(_settings.ToggleHotkey, () => Dispatcher.BeginInvoke(ToggleVisibility));
        _hotkeys.Register(_settings.RefreshHotkey, () => Dispatcher.BeginInvoke(() => _ = _coordinator.RefreshAllAsync()));
    }

    // ---------------------------------------------------------------- hover card

    private void Cell_MouseEnter(object sender, MouseEventArgs e)
    {
        Untuck();
        if (_dragging || _menuOpen) return;
        _insideCell = true;
        _closePopupTimer.Stop();
        if (sender is not Border border) return;
        _pendingCell = border;
        ApplyMagnification(IndexOfCell(border));
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.HoverDelayMs, 1, 500));
        _hoverTimer.Start();
    }

    private int? IndexOfCell(Border cell)
    {
        if (cell.DataContext is not ProviderViewModel viewModel) return null;
        var index = _coordinator.Items.IndexOf(viewModel);
        return index < 0 ? null : index;
    }

    private void Cell_MouseLeave(object sender, MouseEventArgs e)
    {
        _insideCell = false;
        _hoverTimer.Stop();
        SchedulePopupClose();
    }

    private void PopupCard_MouseEnter(object sender, MouseEventArgs e)
    {
        _insidePopup = true;
        _autoHideTimer.Stop();
        _closePopupTimer.Stop();
        // Re-entering during dismissal must cancel every exit animation, not leave a
        // shrunken card or let a stale completion close it underneath the pointer.
        ++_popupAnimationGeneration;
        RestorePopupPose(150);
    }

    private void PopupCard_MouseLeave(object sender, MouseEventArgs e)
    {
        _insidePopup = false;
        SchedulePopupClose();
    }

    private void SchedulePopupClose()
    {
        _closePopupTimer.Stop();
        _closePopupTimer.Start();
    }

    private void ClosePopupTimer_Tick(object? sender, EventArgs e)
    {
        _closePopupTimer.Stop();
        if (_insideCell || _insidePopup) return;
        HideDetail();
        ApplyMagnification(null);
        if (!IsMouseOver) ScheduleTuck(immediate: false);
    }

    private void ShowDetail(Border target, ProviderViewModel viewModel)
    {
        if (_settings.ClickThrough) return;
        var wasOpen = DetailPopup.IsOpen;
        var generation = ++_popupAnimationGeneration;
        viewModel.RefreshAppearance();
        DetailPopup.DataContext = viewModel;
        DetailPopup.PlacementTarget = target;
        DetailPopup.IsOpen = true;
        if (HoverInspector.IsExpanded) LoadInspector();

        Dispatcher.BeginInvoke(() =>
        {
            if (generation != _popupAnimationGeneration || !DetailPopup.IsOpen) return;
            if (wasOpen) { RestorePopupPose(160); return; }
            var direction = IsLeftEdge ? -1d : 1d;
            PopupCard.Opacity = 1;
            PopupScale.ScaleX = 1;
            PopupScale.ScaleY = 1;
            PopupSlide.X = 0;
            if (ReduceMotion || wasOpen)
            {
                PopupCard.BeginAnimation(OpacityProperty, null);
                PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                PopupSlide.BeginAnimation(TranslateTransform.XProperty, null);
                return;
            }

            PopupCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
            { EasingFunction = FluidEase, FillBehavior = FillBehavior.Stop });
            var pop = new CubicEase { EasingMode = EasingMode.EaseOut };
            PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(340)) { EasingFunction = pop, FillBehavior = FillBehavior.Stop });
            PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(340)) { EasingFunction = pop, FillBehavior = FillBehavior.Stop });
            PopupSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(16 * direction, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = FluidEase, FillBehavior = FillBehavior.Stop });
        }, DispatcherPriority.Render);
    }

    private void HideDetail()
    {
        if (!DetailPopup.IsOpen) return;
        if (ReduceMotion) { DetailPopup.IsOpen = false; return; }
        var generation = ++_popupAnimationGeneration;
        var direction = IsLeftEdge ? -1d : 1d;
        var fade = new DoubleAnimation(PopupCard.Opacity, 0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            if (generation == _popupAnimationGeneration && !_insideCell && !_insidePopup)
                DetailPopup.IsOpen = false;
        };
        PopupCard.BeginAnimation(OpacityProperty, fade);
        Motion.To(PopupScale, ScaleTransform.ScaleXProperty, .98, 130, ReduceMotion);
        Motion.To(PopupScale, ScaleTransform.ScaleYProperty, .98, 130, ReduceMotion);
        Motion.To(PopupSlide, TranslateTransform.XProperty, IsTopDock ? 0 : 5 * direction, 130, ReduceMotion);
    }

    private void RestorePopupPose(int milliseconds)
    {
        Motion.To(PopupCard, OpacityProperty, 1, milliseconds, ReduceMotion);
        Motion.To(PopupScale, ScaleTransform.ScaleXProperty, 1, milliseconds, ReduceMotion);
        Motion.To(PopupScale, ScaleTransform.ScaleYProperty, 1, milliseconds, ReduceMotion);
        Motion.To(PopupSlide, TranslateTransform.XProperty, 0, milliseconds, ReduceMotion);
    }

    // ---------------------------------------------------------------- commands

    private void Cell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || e.Handled) return;
        if (sender is Border { DataContext: ProviderViewModel { Id: "gemini", Status: SnapshotStatus.NeedsAuth } })
        {
            OpenSettings();
            return;
        }
        if (sender is Border { DataContext: ProviderViewModel viewModel }) OpenDashboard(viewModel);
    }

    private void Dashboard_Click(object sender, RoutedEventArgs e)
    {
        if (DetailPopup.DataContext is ProviderViewModel viewModel) OpenDashboard(viewModel);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _coordinator.RefreshAllAsync();

    private void GeminiHelp_Click(object sender, RoutedEventArgs e)
    {
        if (PopupCard.DataContext is ProviderViewModel { Id: "claude" })
            System.Windows.MessageBox.Show(this,
                "1. Open VS Code.\n2. Open the Claude Code extension.\n3. Sign in if prompted, or type /login in Claude Code.\n4. Return here and press Refresh.\n\nYour Claude desktop-app login is separate. No API key or payment is required to read subscription usage. A 429 means wait, not sign in again.",
                "Reconnect Claude", MessageBoxButton.OK, MessageBoxImage.Information);
        else OpenSettings();
    }

    private static void OpenDashboard(ProviderViewModel viewModel)
    {
        if (Uri.TryCreate(viewModel.Snapshot.ManageUrl, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private void Notch_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        CloseDetailImmediately();
        _menuOpen = true;
        _autoHideTimer.Stop();
        var menu = new ContextMenu();
        menu.Closed += (_, _) => { _menuOpen = false; if (!IsMouseOver) ScheduleTuck(immediate: false); };
        menu.Items.Add(Item("Stats & Settings…", (_, _) => OpenSettings()));
        menu.Items.Add(Item("Refresh now", async (_, _) => await _coordinator.RefreshAllAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Toggle("Compact dock", _settings.CompactMode, value =>
        {
            _settings.CompactMode = value;
            SettingsStore.Save(_settings);
            RelayoutFromTray();
        }));
        menu.Items.Add(Toggle("Hide until hover", _settings.AutoHide, SetAutoHide));
        menu.Items.Add(Toggle("Click through", _settings.ClickThrough, SetClickThrough));
        menu.Items.Add(Toggle("Keep above other windows", _settings.AlwaysVisible, value =>
        {
            _settings.AlwaysVisible = value;
            Topmost = value;
            SettingsStore.Save(_settings);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Snap to left edge", (_, _) => SnapToEdge("Left")));
        menu.Items.Add(Item("Snap to right edge", (_, _) => SnapToEdge("Right")));
        menu.Items.Add(Item("Snap to top edge", (_, _) => SnapToEdge("Top")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Hide for 1 hour", (_, _) => HideForHour()));
        menu.Items.Add(Item("Quit UsageNotch", (_, _) => System.Windows.Application.Current.Shutdown()));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, RoutedEventHandler action)
    {
        var item = new MenuItem { Header = header };
        item.Click += action;
        return item;
    }

    private static MenuItem Toggle(string header, bool value, Action<bool> action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = value, StaysOpenOnClick = false };
        item.Click += (_, _) => action(item.IsChecked);
        return item;
    }

    private void SnapToEdge(string edge)
    {
        _settings.FloatingDock = false;
        _settings.Edge = edge;
        foreach (var item in _coordinator.Items) item.RefreshAppearance();
        SettingsStore.Save(_settings);
        Dock(animate: true);
        PlayEntrance();
        _settingsWindow?.SyncFromDock();
    }

    // ---------------------------------------------------------------- dragging

    private static Point CursorScreenPoint()
    {
        var point = System.Windows.Forms.Cursor.Position;
        return new Point(point.X, point.Y);
    }

    private static bool IsOverCell(RoutedEventArgs e) =>
        e.OriginalSource is FrameworkElement { DataContext: ProviderViewModel };

    private void Dock_PointerDown(object sender, MouseButtonEventArgs e)
    {
        // Double-clicking bare dock surface is the quickest way into settings.
        if (e.ClickCount == 2 && !IsOverCell(e))
        {
            e.Handled = true;
            _dragStart = null;
            if (Mouse.Captured == Notch) Mouse.Capture(null);
            OpenSettings();
            return;
        }
        _dragStart = CursorScreenPoint();
        _dragWindowStart = new Point(Left, Top);
        Mouse.Capture(Notch, CaptureMode.SubTree);
    }

    private void Dock_PointerMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var cursor = CursorScreenPoint();
        var dpi = VisualTreeHelper.GetDpi(this);
        var dx = (cursor.X - start.X) / dpi.DpiScaleX;
        var dy = (cursor.Y - start.Y) / dpi.DpiScaleY;
        if (!_dragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!_dragging) BeginDrag();

        // Coalesce high-frequency mouse input into one shape/position update per display frame.
        _dragFreePosition = new Point(_dragWindowStart.X + dx, _dragWindowStart.Y + dy);
        e.Handled = true;
    }

    private void RenderDrag(object? sender, EventArgs e)
    {
        if (!_dragging || _closed) return;
        if (e is RenderingEventArgs frame)
        {
            if (_lastRenderingTime == frame.RenderingTime) return;
            _lastRenderingTime = frame.RenderingTime;
        }
        UpdateDragFrame();
    }

    private void UpdateDragFrame()
    {
        var now = _dragClock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastDragFrame, .001, .1);
        _lastDragFrame = now;
        var freeLeft = _dragFreePosition.X;
        var freeTop = _dragFreePosition.Y;
        var area = ScreenAreaFor(freeLeft + Width / 2, freeTop + Height / 2);
        var intent = _dragIntent.Update(new Rect(freeLeft, freeTop, Width, Height), area, dt,
            _settings.SnapToEdges, _settings.PredictiveDrag && !ReduceMotion);
        _dragEdgeLeft = intent.DropEdge == DockEdge.Left;
        _dragEdgeTop = intent.DropEdge == DockEdge.Top;
        var pull = intent.Pull;
        _dragPull = pull;

        var leftGap = freeLeft - area.Left;
        var rightGap = area.Right - (freeLeft + Width);
        var edgeLeft = leftGap <= rightGap ? area.Left : area.Right - Width;
        var sidePull = _settings.SnapToEdges ? DockIntent.Strength(Math.Min(leftGap, rightGap)) : 0;
        var topPull = _settings.SnapToEdges ? DockIntent.Strength(freeTop - area.Top) : 0;
        // Independent, continuous attraction avoids a lateral jump where top and side zones meet.
        Left = Math.Clamp(freeLeft + (edgeLeft - freeLeft) * sidePull, area.Left, Math.Max(area.Left, area.Right - Width));

        var lowest = area.Top;
        var highest = Math.Max(lowest, area.Bottom - Height);
        Top = Math.Clamp(freeTop + (area.Top - freeTop) * topPull, lowest, highest);

        var previewLeft = intent.PreviewEdge == DockEdge.Left;
        var sameAxis = (intent.PreviewEdge == DockEdge.Top) == IsTopDock;
        // Collapse before changing sides: mirrored notches must never flip at full extension.
        var switchingSide = !IsTopDock && previewLeft != DockBackground.LeftEdge;
        if (switchingSide && (DockBackground.Morph < .015 || ReduceMotion))
        {
            DockBackground.LeftEdge = previewLeft;
            switchingSide = false;
        }
        var target = sameAxis && !switchingSide ? intent.Preview : 0;
        var morph = ReduceMotion ? target : DockIntent.Settle(DockBackground.Morph, target, dt);
        var reach = _settings.PredictiveDrag && !ReduceMotion ? intent.Preview : 0;
        var reachX = intent.PreviewEdge == DockEdge.Top ? 0 : (previewLeft ? -reach : reach);
        var reachY = intent.PreviewEdge == DockEdge.Top ? -reach : 0;
        DockBackground.SetDragShape(morph, ReduceMotion ? default : new Vector(DockIntent.Settle(DockBackground.Reach.X, reachX, dt),
            DockIntent.Settle(DockBackground.Reach.Y, reachY, dt)));
    }

    private void BeginDrag()
    {
        _dragging = true;
        _dragFreePosition = _dragWindowStart;
        _dragIntent.Reset();
        _dragClock.Restart();
        _lastDragFrame = 0;
        _lastRenderingTime = null;
        var currentMorph = DockBackground.Morph;
        DockBackground.BeginAnimation(Controls.DockSurface.MorphProperty, null);
        DockBackground.Morph = currentMorph;
        var currentReach = DockBackground.Reach;
        DockBackground.BeginAnimation(Controls.DockSurface.ReachProperty, null);
        DockBackground.Reach = currentReach;
        CompositionTarget.Rendering += RenderDrag;
        _hoverTimer.Stop();
        _closePopupTimer.Stop();
        _autoHideTimer.Stop();
        _tucked = false;
        ++_popupAnimationGeneration;
        DetailPopup.IsOpen = false;
        ApplyMagnification(null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        RootSlide.BeginAnimation(TranslateTransform.XProperty, null);
        RootSlide.X = 0;
        RootSlide.BeginAnimation(TranslateTransform.YProperty, null);
        RootSlide.Y = 0;
        LiftDock(true);
    }

    /// <summary>A small lift while dragging so the dock reads as picked up rather than nudged.</summary>
    private void LiftDock(bool lifted)
    {
        if (ReduceMotion)
        {
            DockLift.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            DockLift.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            DockLift.ScaleX = DockLift.ScaleY = 1;
            return;
        }
        var target = lifted ? 1.012 : 1d;
        Motion.To(DockLift, ScaleTransform.ScaleXProperty, target, lifted ? 150 : 240, false);
        Motion.To(DockLift, ScaleTransform.ScaleYProperty, target, lifted ? 150 : 240, false);
    }

    private void Dock_PointerUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = _dragging;
        FinishDrag();
        if (Mouse.Captured == Notch) Mouse.Capture(null);
    }

    private void Dock_LostCapture(object sender, MouseEventArgs e) => FinishDrag();

    private void FinishDrag()
    {
        _dragStart = null;
        if (!_dragging) return;
        // Consume the last mouse sample even when release precedes the next render tick.
        UpdateDragFrame();
        CompositionTarget.Rendering -= RenderDrag;
        _dragClock.Stop();
        _dragging = false;
        LiftDock(false);
        var reach = DockBackground.Reach;
        DockBackground.Reach = default;
        if (!ReduceMotion)
            DockBackground.BeginAnimation(Controls.DockSurface.ReachProperty,
                new VectorAnimation(reach, default(Vector), TimeSpan.FromMilliseconds(240))
                { EasingFunction = FluidEase, FillBehavior = FillBehavior.Stop });

        var area = ScreenAreaFor(Left + Width / 2, Top + Height / 2);
        _settings.DockLeft = Left;
        _settings.DockTop = Top;

        if (_settings.SnapToEdges && _dragPull >= SnapCommit)
        {
            var travel = Math.Max(1, area.Height - Height);
            _settings.FloatingDock = false;
            _settings.Edge = _dragEdgeTop ? "Top" : _dragEdgeLeft ? "Left" : "Right";
            _settings.VerticalPosition = Math.Clamp((Top - area.Top) / travel, 0, 1);
            if (_dragEdgeTop) _dropCentreX = Left + Width / 2;
            foreach (var item in _coordinator.Items) item.RefreshAppearance();
            Dock(animate: true, duration: 520);
        }
        else
        {
            _settings.FloatingDock = true;
            Dock(animate: true, duration: 300);
        }
        SettingsStore.Save(_settings);
        _settingsWindow?.SyncFromDock();
        if (_settings.AutoHide) ScheduleTuck(immediate: false);
    }

    // ---------------------------------------------------------------- visibility

    private async void HideForHour()
    {
        HideDetail();
        Hide();
        var until = DateTime.UtcNow.AddHours(1);
        _hiddenUntil = until;
        await Task.Delay(TimeSpan.FromHours(1));
        if (_hiddenUntil != until) return;
        if (System.Windows.Application.Current is null || System.Windows.Application.Current.Dispatcher.HasShutdownStarted) return;
        Show();
        Dock(animate: false);
    }

    public void ToggleVisibility()
    {
        _hiddenUntil = DateTime.MinValue;
        if (IsVisible)
        {
            HideDetail();
            Hide();
            return;
        }
        Show();
        Dock(animate: false);
        Activate();
        if (!ReduceMotion)
        {
            RootSlide.BeginAnimation(TranslateTransform.XProperty, null);
            RootSlide.BeginAnimation(TranslateTransform.YProperty, null);
            RootSlide.X = IsTopDock ? 0 : IsLeftEdge ? -26 : 26;
            RootSlide.Y = IsTopDock ? -24 : 0;
            AnimateSlide(0, 420);
            PlayEntrance();
        }
        if (_settings.AutoHide) ScheduleTuck(immediate: false);
    }

    /// <summary>Another launch of UsageNotch means "show me the dock", not "do nothing".</summary>
    public void RevealFromAnotherInstance()
    {
        _hiddenUntil = DateTime.MinValue;
        if (!IsVisible)
        {
            Show();
            Dock(animate: false);
        }
        Untuck();
        Topmost = _settings.AlwaysVisible;
        Activate();
        PlayEntrance();
    }

    public void RelayoutFromTray()
    {
        foreach (var item in _coordinator.Items) item.RefreshAppearance();
        Dock(animate: true);
        PlayEntrance();
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Every control in the settings window edits the live dock; nothing waits for Save.</summary>
    private void Settings_Changed()
    {
        var signature = ProviderSignature();
        if (signature != _providerSignature)
        {
            _providerSignature = signature;
            _coordinator.RebuildProviders();
            _ = _coordinator.RefreshAllAsync();
        }
        foreach (var item in _coordinator.Items) item.RefreshAppearance();
        Topmost = _settings.AlwaysVisible;
        ApplyClickThrough();
        ApplyHotkeys();
        ((App)System.Windows.Application.Current).ApplySettings(_settings);
        ApplyMagnification(null, force: true);
        if (!_layoutUpdateQueued)
        {
            _layoutUpdateQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _layoutUpdateQueued = false;
                if (!_closed) Dock(animate: true, duration: 230);
            }, DispatcherPriority.Render);
        }
        if (!_settings.AutoHide) Untuck();
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        CloseDetailImmediately();
        Untuck();
        _settingsOpen = true;

        var snapshot = _settings.Clone();
        _settings.LiveEdit = true;
        var window = new SettingsWindow(_settings, _coordinator);
        _settingsWindow = window;
        window.SettingsChanged += Settings_Changed;
        window.Closed += (_, _) =>
        {
            window.SettingsChanged -= Settings_Changed;
            _settingsWindow = null;
            _settingsOpen = false;
            _settings.LiveEdit = false;
            if (window.Committed)
            {
                SettingsStore.Save(_settings);
                _coordinator.Alerts.Reprime();
            }
            else
            {
                // Alert deduplication may advance while Settings is open; Cancel must not rewind it.
                var alertState = _settings.AlertState;
                _settings.CopyFrom(snapshot);
                _settings.AlertState = alertState;
                SettingsStore.Save(_settings);
            }
            if (!_settings.ReviewSession) StartupService.Apply(_settings.StartWithWindows);
            Settings_Changed();
            PlayEntrance();
            if (_settings.AutoHide) ScheduleTuck(immediate: false);
        };
        window.Show();
    }

    private void Inspector_Expanded(object sender, RoutedEventArgs e) => LoadInspector();
    private async void LoadInspector()
    {
        if (DetailPopup.DataContext is not ProviderViewModel vm || vm.PrimaryWindow is not { } window) return;
        var snapshot = vm.Snapshot; var now = DateTimeOffset.UtcNow;
        var points = await Task.Run(() => { lock (_coordinator.History) return _coordinator.History.Read(vm.Id, _coordinator.History.AccountKey(snapshot), window.Id, now.AddHours(-3)); });
        await Dispatcher.InvokeAsync(() =>
        {
        if (DetailPopup.DataContext != vm || !DetailPopup.IsOpen) return;
        var forecast = UsageForecast.Calculate(points, vm.Status, now, _settings);
        HoverChart.TimeSettings = _settings;
        HoverChart.Points = points; HoverChart.Start = now.AddHours(-3); HoverChart.End = now; HoverChart.InvalidateVisual();
        HoverForecast.Text = window.Label + " · pace estimate\n" + forecast.Summary;
        HoverConfidence.Text = $"{forecast.Confidence} · {forecast.Samples} readings · {forecast.Coverage:P0} coverage";
        HoverConfidence.ToolTip = forecast.Explanation;
        });
    }
    private void ExploreHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DetailPopup.DataContext is not ProviderViewModel vm) return;
        var cell = DetailPopup.PlacementTarget as FrameworkElement;
        var origin = cell?.PointToScreen(new Point(cell.ActualWidth / 2, cell.ActualHeight / 2));
        OpenSettings(); _settingsWindow?.ShowProvider(vm.Id, origin);
    }

    private void CloseDetailImmediately()
    {
        ++_popupAnimationGeneration;
        _hoverTimer.Stop();
        _closePopupTimer.Stop();
        _insideCell = _insidePopup = false;
        DetailPopup.IsOpen = false;
        ApplyMagnification(null);
    }
}
