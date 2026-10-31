using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;

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

    /// <summary>Animates a property to a value with a spring, from wherever it is now.</summary>
    public static void Spring(Animatable target, DependencyProperty property, double value, bool reduced, double damping = .8, int milliseconds = 620)
    {
        var from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
        if (reduced || Math.Abs(from - value) < .0001) return;
        target.BeginAnimation(property, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new SpringEase { Damping = damping }, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Fades an element in while it rises a little on a spring, after <paramref name="index"/> stagger steps: how cards enter
    /// on Android. Reduced motion shows it at once.
    /// </summary>
    public static void Rise(UIElement element, int index, bool reduced, double distance = 16)
    {
        element.Opacity = 1;
        if (reduced) return;
        var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = translate;
        var delay = TimeSpan.FromMilliseconds(DesignTokens.Motion.StaggerMs * Math.Min(index, 12) + 40);
        translate.Y = 0;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)), new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay)),
                new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(DesignTokens.Motion.StandardMs)), new CubicEase { EasingMode = EasingMode.EaseOut }) },
            FillBehavior = FillBehavior.Stop
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new DiscreteDoubleKeyFrame(distance, KeyTime.FromTimeSpan(TimeSpan.Zero)), new DiscreteDoubleKeyFrame(distance, KeyTime.FromTimeSpan(delay)),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(680)), new SpringEase { Damping = .8 }) },
            FillBehavior = FillBehavior.Stop
        });
    }

    /// <summary>A quick press-down and spring back, for buttons and chips.</summary>
    public static void Press(FrameworkElement element, bool down, bool reduced)
    {
        if (element.RenderTransform is not ScaleTransform scale) { element.RenderTransformOrigin = new Point(.5, .5); element.RenderTransform = scale = new ScaleTransform(); }
        Spring(scale, ScaleTransform.ScaleXProperty, down ? .96 : 1, reduced, down ? 1 : .5, down ? 140 : 520);
        Spring(scale, ScaleTransform.ScaleYProperty, down ? .96 : 1, reduced, down ? 1 : .5, down ? 140 : 520);
    }
}

/// <summary>
/// A damped spring as an easing curve, so WPF animations settle like Compose springs. Damping 1 is critically damped
/// (no overshoot); lower values bounce a little.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingProperty = DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(.78));
    public static readonly DependencyProperty FrequencyProperty = DependencyProperty.Register(nameof(Frequency), typeof(double), typeof(SpringEase), new PropertyMetadata(1.35));
    public double Damping { get => (double)GetValue(DampingProperty); set => SetValue(DampingProperty, value); }
    public double Frequency { get => (double)GetValue(FrequencyProperty); set => SetValue(FrequencyProperty, value); }

    // The curve below is already an "out" curve; EaseIn makes WPF use it unmirrored.
    public SpringEase() => EasingMode = EasingMode.EaseIn;

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var zeta = Math.Clamp(Damping, .05, 1); var omega = 2 * Math.PI * Frequency * 2.2;
        double value;
        if (zeta >= .999) value = 1 - (1 + omega * t) * Math.Exp(-omega * t);
        else
        {
            var wd = omega * Math.Sqrt(1 - zeta * zeta);
            value = 1 - Math.Exp(-zeta * omega * t) * (Math.Cos(wd * t) + zeta * omega / wd * Math.Sin(wd * t));
        }
        // Blend to exactly 1 at the end so the animation lands on its target.
        return value + (1 - value) * Math.Pow(t, 8);
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
