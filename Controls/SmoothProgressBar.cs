using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageNotch.Services;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace UsageNotch.Controls;

/// <summary>A precise capsule meter with one flat colour and an eased value transition.</summary>
public sealed class SmoothProgressBar : FrameworkElement
{
    public static readonly DependencyProperty RemainingProperty = DependencyProperty.Register(nameof(Remaining), typeof(bool), typeof(SmoothProgressBar), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Remaining { get => (bool)GetValue(RemainingProperty); set => SetValue(RemainingProperty, value); }
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SmoothProgressBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion), typeof(bool), typeof(SmoothProgressBar), new FrameworkPropertyMetadata(false, (d, _) => ((SmoothProgressBar)d).Animate(((SmoothProgressBar)d).Value, false)));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(System.Windows.Media.Brush), typeof(SmoothProgressBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public System.Windows.Media.Brush? TrackBrush { get => (System.Windows.Media.Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public static readonly DependencyProperty DelayProperty = DependencyProperty.Register(
        nameof(Delay), typeof(double), typeof(SmoothProgressBar), new FrameworkPropertyMetadata(0d));

    public static readonly DependencyProperty UnknownProperty = DependencyProperty.Register(
        nameof(Unknown), typeof(bool), typeof(SmoothProgressBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(SmoothProgressBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool ReducedMotion { get => (bool)GetValue(ReducedMotionProperty); set => SetValue(ReducedMotionProperty, value); }
    public double Delay { get => (double)GetValue(DelayProperty); set => SetValue(DelayProperty, value); }
    public bool Unknown { get => (bool)GetValue(UnknownProperty); set => SetValue(UnknownProperty, value); }
    private double DisplayValue => (double)GetValue(DisplayValueProperty);

    public SmoothProgressBar() => Loaded += (_, _) => Animate(Value, true);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SmoothProgressBar)d).Animate((double)e.NewValue, false);

    private void Animate(double value, bool fromLoad)
    {
        var target = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        if (!IsLoaded || ReducedMotion)
        {
            BeginAnimation(DisplayValueProperty, null);
            SetValue(DisplayValueProperty, target);
            return;
        }
        var from = fromLoad ? 0 : DisplayValue;
        SetValue(DisplayValueProperty, target);
        BeginAnimation(DisplayValueProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(540))
        {
            BeginTime = fromLoad ? TimeSpan.FromMilliseconds(Delay) : TimeSpan.Zero,
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 2 || ActualHeight < 1) return;
        var radius = ActualHeight / 2;
        dc.DrawRoundedRectangle(TrackBrush ?? Palette.Frozen(Palette.Alpha(Colors.White, 0.10)), null,
            new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);

        if (Unknown)
        {
            var dash = Palette.Frozen(Palette.Alpha(Palette.Unknown, 0.6));
            for (double x = 0; x < ActualWidth - 3; x += 8)
                dc.DrawRoundedRectangle(dash, null, new Rect(x, 0, 4, ActualHeight), radius, radius);
            return;
        }

        var fraction = Math.Clamp(DisplayValue, 0, 1);
        var width = fraction * ActualWidth;
        if (width <= 1) return;
        dc.DrawRoundedRectangle(Palette.Frozen(Palette.Usage(Remaining ? 1 - fraction : fraction)), null,
            new Rect(0, 0, width, ActualHeight), Math.Min(radius, width / 2), radius);
    }
}
