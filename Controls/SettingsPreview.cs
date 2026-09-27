using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageNotch.Models;
using UsageNotch.Services;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;

namespace UsageNotch.Controls;

/// <summary>Local sample-only illustrations; shares display rules with the real dock.</summary>
public sealed class SettingsPreview : FrameworkElement
{
    public string Scene { get; set; } = "Text";
    private AppSettings _settings = new(), _previous = new();
    public static readonly DependencyProperty BlendProperty = DependencyProperty.Register(nameof(Blend), typeof(double), typeof(SettingsPreview), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Blend { get => (double)GetValue(BlendProperty); set => SetValue(BlendProperty, value); }
    public SettingsPreview() { Height = 186; ClipToBounds = true; IsHitTestVisible = false; }
    public void Update(AppSettings settings)
    {
        _previous = _settings; _settings = settings.Clone();
        if (Scene is "Text" or "Time") Height = Math.Max(186, 122 + (settings.ShowProviderNames ? 16 : 0) + (settings.ShowPercentages ? 20 : 0) + (settings.ShowWindowLabel ? 16 : 0) + (settings.DisplayMode == "Dual" && settings.ShowSecondaryPercentage ? 16 : 0) + (settings.ShowDockResetTimes ? 25 : 0) + (settings.ShowStatusBadge ? 14 : 0));
        BeginAnimation(BlendProperty, null); Blend = 1;
        if (IsLoaded && !Motion.IsReduced(settings))
            BeginAnimation(BlendProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        InvalidateVisual();
    }
    public void Replay() => Update(_settings);
    protected override void OnRender(DrawingContext dc)
    {
        var w = Math.Max(280, ActualWidth); var h = ActualHeight;
        var ink = new SolidColorBrush(Color.FromRgb(152, 165, 187));
        var line = new Pen(new SolidColorBrush(Color.FromRgb(50, 60, 76)), 1);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(14, 19, 27)), line, new Rect(0, 0, w, h), 12, 12);
        void Label(string value, double x, double y, double size = 11, Brush? brush = null) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush ?? ink, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        Label("SAMPLE PREVIEW", 14, 12, 9);
        var s = _settings; var at = new DateTimeOffset(2030, 1, 1, 13, 45, 30, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2030, 1, 1)));
        var snapshot = new ProviderSnapshot("claude", "Claude", "", Fidelity.Manual, SnapshotStatus.Stale,
            [new("five_hour", "5-hour window", .73, at), new("seven_day", "Weekly window", .34, at.AddDays(3))]);
        var vm = new ProviderViewModel(snapshot, s);
        double Mix(double a, double b) => a + (b - a) * Blend;
        Rect Bounds(AppSettings value)
        {
            if (Scene is "Text" or "Time") return new Rect(24, 37, 84, h - 51);
            var width = (value.CompactMode ? 36 : 48) * value.UiScale;
            var height = (value.CompactMode ? 70 : 95) * value.UiScale + value.ProviderSpacing * .5;
            if (value.Edge == "Top" && !value.FloatingDock) return new Rect(80 + value.HorizontalPosition * (w - 220), Scene == "Behavior" && value.AutoHide ? 32 - width + 7 : 32, height, width);
            var x = value.FloatingDock ? w * .42 : value.Edge == "Left" ? 18 : w - width - 18;
            if (Scene == "Behavior" && value.AutoHide) x += value.Edge == "Left" ? -width + 7 : width - 7;
            return new Rect(x, 34 + Math.Clamp(value.VerticalPosition, 0, 1) * Math.Max(0, h - height - 48), width, height);
        }
        var old = Bounds(_previous); var target = Bounds(s);
        var dock = new Rect(Mix(old.X, target.X), Mix(old.Y, target.Y), Mix(old.Width, target.Width), Mix(old.Height, target.Height));
        var surface = DockTheme.Parse(s.DockColor); var foreground = Palette.IsLight(surface) ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
        dc.PushOpacity(Math.Clamp(Mix(_previous.DockOpacity, s.DockOpacity), .3, 1));
        dc.DrawRoundedRectangle(new SolidColorBrush(surface), line, dock, 18, 18);
        var center = new Point(dock.X + dock.Width / 2, dock.Y + 29);
        if (Scene is not ("Text" or "Time") && s.Edge == "Top" && !s.FloatingDock) center = new Point(dock.X + 29, dock.Y + dock.Height / 2);
        var radius = Scene is "Text" or "Time" ? (s.CompactMode ? 15 : 19) : 12 * s.UiScale;
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(75, 87, 100)), 2.3), center, radius, radius);
        var fraction = Mix(new ProviderViewModel(snapshot, _previous).RingFraction ?? .73, vm.RingFraction ?? .73); var angle = Math.PI * 2 * Math.Clamp(fraction, .001, .999);
        var arc = new StreamGeometry(); using (var context = arc.Open())
        { context.BeginFigure(new Point(center.X, center.Y - radius), false, false); context.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)), new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, true, false); }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(126, 225, 189)), 2.5), arc);
        dc.PushTransform(new TranslateTransform(center.X - 7, center.Y - 7)); dc.PushTransform(new ScaleTransform(14d / 24, 14d / 24));
        dc.DrawGeometry(s.ColoredLogos ? new SolidColorBrush(Color.FromRgb(217, 119, 87)) : foreground, null, ProviderLogo.Shape("claude")); dc.Pop(); dc.Pop();
        if (vm.Dual) dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(178, 160, 237)), 1.4), center, radius - 5, radius - 5);
        if (Scene is "Text" or "Time")
        {
            dc.PushOpacity(.3 + .7 * Blend);
            var y = center.Y + radius + 8;
            void DockLabel(string value, double size = 10)
            {
                var text = new FormattedText(value, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = dock.Width - 8, TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(text, new Point(dock.X + 4, y)); y += text.Height + 3;
            }
            if (vm.ShowProviderNames) DockLabel(vm.DockName);
            if (vm.ShowPercentages) DockLabel(vm.PercentText, 12);
            if (vm.SecondaryLine.Length > 0) DockLabel(vm.SecondaryLine, 9);
            if (vm.DockWindowLabel.Length > 0) DockLabel(vm.DockWindowLabel, 8);
            if (vm.DockResetLines.Length > 0) DockLabel("5h " + TimeDisplay.Clock(at, s.Use24HourTime, s.ShowClockSeconds), 8);
            if (vm.ShowSavedBadge) DockLabel("saved", 8);
            dc.Pop();
        }
        dc.Pop();
        if (Scene is "Text" or "Time")
        {
            var panelX = 126d; var panelWidth = Math.Max(130, w - panelX - 16);
            dc.PushOpacity(.65 + .35 * Blend);
            dc.DrawRoundedRectangle(new SolidColorBrush(surface), line, new Rect(panelX, 42, panelWidth, 118), 12, 12);
            Label("EXPANDED CARD", panelX + 14, 55, 9, foreground); Label("5-hour window", panelX + 14, 78, 12, foreground);
            Label(s.ShowRemaining ? "27% left" : "73% used", panelX + 14, 99, 12, foreground);
            Label(s.ShowExpandedResetTimes ? "Resets at " + TimeDisplay.Clock(at, s.Use24HourTime, s.ShowClockSeconds) : "Reset details hidden", panelX + 14, 127, 11, foreground);
            dc.Pop();
        }
        else
        {
            var caption = Scene == "Behavior" ? s.AutoHide ? "A slim edge stays visible.\nHover to bring the dock back." : s.ClickThrough ? "Mouse clicks pass through.\nUse the tray to change settings." : "The dock stays available.\nHover opens its detail card."
                : s.FloatingDock ? "Free position\nDrag the dock anywhere." : $"{s.Edge} edge · {s.UiScale * 100:0}% scale\n{s.ProviderSpacing:0} px between providers";
            Label(caption, s.Edge == "Left" ? 115 : 24, 85, 12);
        }
    }
}
