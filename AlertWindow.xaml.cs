using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UsageNotch.Services;

namespace UsageNotch;

public partial class AlertWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(9) };
    private readonly AppSettings _settings;
    private bool _dismissing;
    public event Action? Snoozed;
    public AlertWindow(AlertNotice notice, AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        DataContext = notice;
        DockTheme.Apply(Resources, settings.DockColor);
        Eyebrow.Text = notice.IsPreview ? "PREVIEW · SAMPLE DATA" : notice.Critical ? "LOW USAGE REMAINING" : "USAGE UPDATE";
        Meter.Visibility = notice.UsedFraction.HasValue ? Visibility.Visible : Visibility.Collapsed;
        Meter.ReducedMotion = Motion.IsReduced(settings);
        _timer.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - ActualWidth - 12);
            Top = Math.Max(area.Top, area.Bottom - ActualHeight - 12);
            if (!Motion.IsReduced(settings))
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            }
            _timer.Start();
        };
    }
    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismiss();
    private void Snooze_Click(object sender, RoutedEventArgs e) { Snoozed?.Invoke(); Dismiss(); }
    private void Dismiss()
    {
        if (_dismissing) return;
        _dismissing = true;
        _timer.Stop();
        if (Motion.IsReduced(_settings)) { Close(); return; }
        var fade = new DoubleAnimation(Card.Opacity, 0, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop };
        Card.Opacity = 0;
        fade.Completed += (_, _) => Close();
        Card.BeginAnimation(OpacityProperty, fade);
        Motion.To(Slide, System.Windows.Media.TranslateTransform.YProperty, 6, 160, false);
    }
}
