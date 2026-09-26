using System.Windows;
using System.Windows.Media.Animation;

namespace UsageNotch.Services;

/// <summary>Interruptible transitions start from the currently displayed value, not a hard-coded pose.</summary>
public static class Motion
{
    public static bool IsReduced(AppSettings settings) => settings.ReducedMotion || !SystemParameters.ClientAreaAnimation;
    private static DoubleAnimation Animation(double from, double to, int milliseconds) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };

    public static DoubleAnimation? To(Animatable target, DependencyProperty property, double value, int milliseconds, bool reduced)
    {
        var from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
        var animation = reduced || milliseconds <= 0 || Math.Abs(from - value) < .0001 ? null : Animation(from, value, milliseconds);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        return animation;
    }
    public static DoubleAnimation? To(UIElement target, DependencyProperty property, double value, int milliseconds, bool reduced)
    {
        var from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
        var animation = reduced || milliseconds <= 0 || Math.Abs(from - value) < .0001 ? null : Animation(from, value, milliseconds);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        return animation;
    }
}
