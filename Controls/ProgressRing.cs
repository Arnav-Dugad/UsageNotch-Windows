using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageNotch.Models;
using UsageNotch.Services;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace UsageNotch.Controls;

/// <summary>Quiet, precise usage arcs without glows or perpetual decorative motion.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty RemainingProperty = DependencyProperty.Register(nameof(Remaining), typeof(bool), typeof(ProgressRing), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Remaining { get => (bool)GetValue(RemainingProperty); set => SetValue(RemainingProperty, value); }
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty SecondaryValueProperty = DependencyProperty.Register(
        nameof(SecondaryValue), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnSecondaryChanged));

    public static readonly DependencyProperty SecondaryKnownProperty = DependencyProperty.Register(
        nameof(SecondaryKnown), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DualProperty = DependencyProperty.Register(
        nameof(Dual), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsKnownProperty = DependencyProperty.Register(
        nameof(IsKnown), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(SnapshotStatus), typeof(ProgressRing),
        new FrameworkPropertyMetadata(SnapshotStatus.Loading, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty HoveredProperty = DependencyProperty.Register(
        nameof(Hovered), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(ProgressRing),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DiscBrushProperty = DependencyProperty.Register(
        nameof(DiscBrush), typeof(System.Windows.Media.Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(System.Windows.Media.Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RimBrushProperty = DependencyProperty.Register(
        nameof(RimBrush), typeof(System.Windows.Media.Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SecondaryDisplayProperty = DependencyProperty.Register(
        "SecondaryDisplay", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SpinAngleProperty = DependencyProperty.Register(
        "SpinAngle", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double SecondaryValue { get => (double)GetValue(SecondaryValueProperty); set => SetValue(SecondaryValueProperty, value); }
    public bool SecondaryKnown { get => (bool)GetValue(SecondaryKnownProperty); set => SetValue(SecondaryKnownProperty, value); }
    public bool Dual { get => (bool)GetValue(DualProperty); set => SetValue(DualProperty, value); }
    public bool IsKnown { get => (bool)GetValue(IsKnownProperty); set => SetValue(IsKnownProperty, value); }
    public SnapshotStatus Status { get => (SnapshotStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public bool ReducedMotion { get => (bool)GetValue(ReducedMotionProperty); set => SetValue(ReducedMotionProperty, value); }
    public bool Hovered { get => (bool)GetValue(HoveredProperty); set => SetValue(HoveredProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public System.Windows.Media.Brush? DiscBrush { get => (System.Windows.Media.Brush?)GetValue(DiscBrushProperty); set => SetValue(DiscBrushProperty, value); }
    public System.Windows.Media.Brush? TrackBrush { get => (System.Windows.Media.Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public System.Windows.Media.Brush? RimBrush { get => (System.Windows.Media.Brush?)GetValue(RimBrushProperty); set => SetValue(RimBrushProperty, value); }


    public ProgressRing()
    {
        Loaded += (_, _) => UpdateAnimations();
        IsVisibleChanged += (_, _) => UpdateAnimations();
        Unloaded += (_, _) => BeginAnimation(SpinAngleProperty, null);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ProgressRing)d).AnimateTo(DisplayValueProperty, (double)e.NewValue);
    private static void OnSecondaryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ProgressRing)d).AnimateTo(SecondaryDisplayProperty, (double)e.NewValue);
    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (ProgressRing)d;
        ring.UpdateAnimations();
        if (ring.ReducedMotion)
        {
            ring.AnimateTo(DisplayValueProperty, ring.Value);
            ring.AnimateTo(SecondaryDisplayProperty, ring.SecondaryValue);
        }
    }

    private void AnimateTo(DependencyProperty property, double value)
    {
        var target = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        var current = (double)GetValue(property);
        SetValue(property, target);
        if (!IsLoaded || !IsVisible || ReducedMotion || Math.Abs(current - target) < .0005)
        {
            BeginAnimation(property, null);
            return;
        }
        BeginAnimation(property, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(460))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateAnimations()
    {
        if (IsLoaded && IsVisible && !ReducedMotion && Status == SnapshotStatus.Loading)
            BeginAnimation(SpinAngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4))
            { RepeatBehavior = RepeatBehavior.Forever }, HandoffBehavior.SnapshotAndReplace);
        else
        {
            BeginAnimation(SpinAngleProperty, null);
            SetValue(SpinAngleProperty, 0d);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var stroke = Math.Clamp(size * .047, 1.8, 3);
        var radius = size / 2 - stroke / 2 - 3;
        var track = TrackBrush ?? Palette.Frozen(Palette.Alpha(Colors.White, .15));
        dc.DrawEllipse(null, new Pen(track, stroke), center, radius, radius);
        if (Dual)
        {
            var innerRadius = radius - stroke - 3;
            var innerStroke = Math.Max(1.3, stroke * .62);
            dc.DrawEllipse(null, new Pen(track, innerStroke), center, innerRadius, innerRadius);
            if (SecondaryKnown) DrawUsage(dc, center, innerRadius, innerStroke, (double)GetValue(SecondaryDisplayProperty));
        }
        if (Status == SnapshotStatus.Loading)
        {
            DrawArc(dc, RoundPen(Palette.Frozen(Palette.Loading), stroke), center, radius, (double)GetValue(SpinAngleProperty) - 90, 80);
            return;
        }
        if (IsKnown) DrawUsage(dc, center, radius, stroke, (double)GetValue(DisplayValueProperty));
        else if (Status is SnapshotStatus.Error or SnapshotStatus.NeedsAuth or SnapshotStatus.Unsupported)
        {
            // One restrained status mark, never an invented usage sweep.
            var color = Status == SnapshotStatus.Error ? Palette.Danger : Palette.Warning;
            dc.DrawEllipse(Palette.Frozen(color), null, PointOnCircle(center, radius, -45), 2.1, 2.1);
        }
    }

    private static Pen RoundPen(System.Windows.Media.Brush brush, double width) => new(brush, width)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    private void DrawUsage(DrawingContext dc, Point center, double radius, double stroke, double fraction)
    {
        if (!double.IsFinite(fraction) || fraction <= 0) return;
        fraction = Math.Clamp(fraction, 0, 1);
        DrawArc(dc, RoundPen(Palette.Frozen(Palette.Usage(Remaining ? 1 - fraction : fraction)), stroke), center, radius, -90, fraction * 360);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point center, double radius, double start, double sweep)
    {
        if (sweep >= 359.99) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(PointOnCircle(center, radius, start), false, false);
            context.ArcTo(PointOnCircle(center, radius, start + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
