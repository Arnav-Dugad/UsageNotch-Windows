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

/// <summary>
/// The usage ring, drawn exactly like Android's RingPainter: a quiet track, a comet arc from 12 o'clock that brightens from a
/// faint tail to its head in the usage colour, an optional inner ring for the weekly window, a hairline outside the ring for
/// the time left before the reset, a tick for your budget, a soft glow above 90% and a burst of particles when a limit renews
/// while you watch. Reduced motion keeps the drawing and drops the movement.
/// </summary>
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

    /// <summary>0..1 of the window's period still to run before its reset; NaN when unknown.</summary>
    public static readonly DependencyProperty TimeLeftProperty = DependencyProperty.Register(nameof(TimeLeft), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The budget's position on the outer arc (0..1, in the same terms as Value); NaN when there is none.</summary>
    public static readonly DependencyProperty BudgetProperty = DependencyProperty.Register(nameof(Budget), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SecondaryBudgetProperty = DependencyProperty.Register(nameof(SecondaryBudget), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The comet look (faint tail to bright head). Off draws a flat arc.</summary>
    public static readonly DependencyProperty CometProperty = DependencyProperty.Register(nameof(Comet), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Ink for the hairline and ticks; follows the surface so they read on light and dark.</summary>
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Color), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SecondaryDisplayProperty = DependencyProperty.Register(
        "SecondaryDisplay", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SpinAngleProperty = DependencyProperty.Register(
        "SpinAngle", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        "Pulse", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty HeadProperty = DependencyProperty.Register(
        "Head", typeof(double), typeof(ProgressRing),
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
    public double TimeLeft { get => (double)GetValue(TimeLeftProperty); set => SetValue(TimeLeftProperty, value); }
    public double Budget { get => (double)GetValue(BudgetProperty); set => SetValue(BudgetProperty, value); }
    public double SecondaryBudget { get => (double)GetValue(SecondaryBudgetProperty); set => SetValue(SecondaryBudgetProperty, value); }
    public bool Comet { get => (bool)GetValue(CometProperty); set => SetValue(CometProperty, value); }
    public Color Ink { get => (Color)GetValue(InkProperty); set => SetValue(InkProperty, value); }

    private sealed record Particle(double Angle, double Speed, double Size, Color Color);
    private Particle[] _particles = [];
    private TimeSpan? _burstStart;
    private TimeSpan _burstElapsed;
    private bool _pulsing;

    public ProgressRing()
    {
        Loaded += (_, _) => UpdateAnimations();
        IsVisibleChanged += (_, _) => UpdateAnimations();
        Unloaded += (_, _) => { BeginAnimation(SpinAngleProperty, null); BeginAnimation(PulseProperty, null); _pulsing = false; StopBurst(); };
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (ProgressRing)d;
        var before = (double)e.OldValue; var after = (double)e.NewValue;
        ring.AnimateTo(DisplayValueProperty, after);
        if (!ring.IsLoaded || ring.ReducedMotion || !ring.IsVisible || Math.Abs(before - after) < .003) { ring.UpdateAnimations(); return; }
        // A refresh you can feel: the head blooms briefly when the reading actually moves.
        ring.BeginAnimation(HeadProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(900)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        var usedBefore = ring.Remaining ? 1 - before : before; var usedAfter = ring.Remaining ? 1 - after : after;
        if (ring.Status == SnapshotStatus.Ok && ring.IsKnown && usedBefore > .25 && usedAfter < usedBefore - .2) ring.Burst();
        ring.UpdateAnimations();
    }
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
        // A spring settle, the same feel as Android's rings.
        BeginAnimation(property, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(720))
        {
            EasingFunction = new SpringEase(), FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private double UsedShown => Remaining ? 1 - Value : Value;

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
        // Above 90% the glow breathes slowly; otherwise it rests.
        var glow = IsKnown && Status == SnapshotStatus.Ok && UsedShown >= DesignTokens.GlowFraction;
        if (glow && IsLoaded && IsVisible && !ReducedMotion)
        {
            if (!_pulsing)
            {
                _pulsing = true;
                BeginAnimation(PulseProperty, new DoubleAnimation(.35, 1, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            }
        }
        else { _pulsing = false; BeginAnimation(PulseProperty, null); SetValue(PulseProperty, glow ? .7 : 0d); }
    }

    /// <summary>A short burst of particles in the usage colours, for a limit that renews while it is on screen.</summary>
    public void Burst()
    {
        if (ReducedMotion || !IsLoaded) return;
        var random = new Random();
        _particles = Enumerable.Range(0, 18).Select(i => new Particle(i * 20 + random.NextDouble() * 14, .55 + random.NextDouble() * .7,
            1.2 + random.NextDouble() * 1.6, Palette.Usage(random.NextDouble() * .5))).ToArray();
        _burstStart = null;
        CompositionTarget.Rendering -= Advance;
        CompositionTarget.Rendering += Advance;
    }

    private void Advance(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        _burstStart ??= now;
        _burstElapsed = now - _burstStart.Value;
        if (_burstElapsed > TimeSpan.FromMilliseconds(950)) StopBurst();
        InvalidateVisual();
    }

    private void StopBurst() { CompositionTarget.Rendering -= Advance; _particles = []; _burstStart = null; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var stroke = Math.Clamp(size * .052, 1.8, 10);
        var timeLeft = double.IsFinite(TimeLeft) && size >= 36 && Status == SnapshotStatus.Ok ? Math.Clamp(TimeLeft, 0, 1) : (double?)null;
        var radius = size / 2 - stroke / 2 - size * .045;
        if (timeLeft is not null) radius -= stroke * .9;
        var track = TrackBrush ?? Palette.Frozen(Palette.Alpha(Colors.White, .15));

        if (timeLeft is { } left)
        {
            var timeRadius = radius + stroke * 1.25;
            var width = Math.Max(1, stroke * .28);
            dc.DrawEllipse(null, new Pen(Palette.Frozen(Palette.Alpha(Ink, .12)), width), center, timeRadius, timeRadius);
            if (left > .004) DrawArc(dc, RoundPen(Palette.Frozen(Palette.Alpha(Ink, .55)), width), center, timeRadius, -90, left * 360);
        }

        dc.DrawEllipse(null, new Pen(track, stroke), center, radius, radius);
        if (Status == SnapshotStatus.Loading)
        {
            DrawArc(dc, RoundPen(Palette.Frozen(Palette.Loading), stroke), center, radius, (double)GetValue(SpinAngleProperty) - 90, 80);
            return;
        }
        var shown = (double)GetValue(DisplayValueProperty);
        if (IsKnown)
        {
            var used = Remaining ? 1 - Value : Value;
            var pulse = (double)GetValue(PulseProperty);
            if (pulse > 0 && shown > 0) Glow(dc, center, radius, stroke, shown, Palette.Usage(used), pulse);
            DrawUsage(dc, center, radius, stroke, shown, used);
            var head = (double)GetValue(HeadProperty);
            if (head > .01 && shown > .01)
            {
                var at = PointOnCircle(center, radius, -90 + Math.Clamp(shown, 0, 1) * 360);
                dc.DrawEllipse(Palette.Frozen(Palette.Alpha(Colors.White, .25 * head)), null, at, stroke * (1 + head), stroke * (1 + head));
                dc.DrawEllipse(Palette.Frozen(Palette.Alpha(Colors.White, .9)), null, at, stroke * .42, stroke * .42);
            }
            if (double.IsFinite(Budget)) Tick(dc, center, radius, stroke, Math.Clamp(Budget, 0, 1), stroke * .95, Math.Max(1.2, stroke * .34));
        }
        else if (Status is SnapshotStatus.Error or SnapshotStatus.NeedsAuth or SnapshotStatus.Unsupported)
        {
            // One restrained status mark, never an invented usage sweep.
            var color = Status == SnapshotStatus.Error ? Palette.Danger : Palette.Warning;
            dc.DrawEllipse(Palette.Frozen(color), null, PointOnCircle(center, radius, -45), 2.1, 2.1);
        }
        if (Dual)
        {
            var innerStroke = Math.Max(1.3, stroke * .62);
            var innerRadius = radius - stroke - size * .05;
            dc.DrawEllipse(null, new Pen(track, innerStroke), center, innerRadius, innerRadius);
            if (SecondaryKnown)
            {
                var secondary = (double)GetValue(SecondaryDisplayProperty);
                DrawUsage(dc, center, innerRadius, innerStroke, secondary, Remaining ? 1 - SecondaryValue : SecondaryValue);
                if (double.IsFinite(SecondaryBudget)) Tick(dc, center, innerRadius, innerStroke, Math.Clamp(SecondaryBudget, 0, 1), innerStroke, Math.Max(1, innerStroke * .4));
            }
        }
        if (_particles.Length > 0) DrawParticles(dc, center, radius);
    }

    private void Glow(DrawingContext dc, Point center, double radius, double stroke, double fraction, Color color, double strength)
    {
        // A soft halo from a few widening, fading strokes (WPF's blur effects are too costly for the dock).
        for (var i = 3; i >= 1; i--)
            DrawArc(dc, RoundPen(Palette.Frozen(Palette.Alpha(color, .07 * strength * (4 - i))), stroke * (1 + i * .9)), center, radius, -90, Math.Clamp(fraction, 0, 1) * 360);
    }

    private void Tick(DrawingContext dc, Point center, double radius, double stroke, double at, double reach, double width)
    {
        var degrees = -90 + at * 360;
        dc.DrawLine(RoundPen(Palette.Frozen(Ink), width), PointOnCircle(center, radius - reach, degrees), PointOnCircle(center, radius + reach, degrees));
    }

    private void DrawParticles(DrawingContext dc, Point center, double radius)
    {
        var t = Math.Clamp(_burstElapsed.TotalMilliseconds / 950, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        foreach (var p in _particles)
        {
            var distance = radius * (1 + eased * p.Speed * .55);
            var at = PointOnCircle(center, distance, p.Angle - 90);
            dc.DrawEllipse(Palette.Frozen(Palette.Alpha(p.Color, (1 - t) * .95)), null, at, p.Size * (1 - t * .4), p.Size * (1 - t * .4));
        }
    }

    private static Pen RoundPen(System.Windows.Media.Brush brush, double width) => new(brush, width)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    private void DrawUsage(DrawingContext dc, Point center, double radius, double stroke, double fraction, double used)
    {
        if (!double.IsFinite(fraction) || fraction <= 0) return;
        fraction = Math.Clamp(fraction, 0, 1);
        var color = Palette.Usage(used);
        if (!Comet || fraction >= .985) { DrawArc(dc, RoundPen(Palette.Frozen(color), stroke), center, radius, -90, fraction * 360); return; }
        // The comet: one arc painted with an angular gradient from a faint tail to the full colour at its round head.
        DrawArc(dc, RoundPen(CometBrush(center, radius, stroke, fraction, color), stroke), center, radius, -90, fraction * 360);
    }

    private static readonly Dictionary<(int Size, uint Color, int Fraction, int Stroke), ImageBrush> CometCache = new();

    /// <summary>
    /// An angular gradient as an image laid over the ring in absolute coordinates, so one stroke shows the whole comet without
    /// seams. Cached by size, colour and a quantised fraction.
    /// </summary>
    private ImageBrush CometBrush(Point center, double radius, double stroke, double fraction, Color color)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var extent = radius + stroke;
        var pixels = Math.Max(8, (int)Math.Ceiling(extent * 2 * scale));
        var q = (int)Math.Round(fraction * 400);
        var key = (pixels, (uint)(color.R << 16 | color.G << 8 | color.B), q, (int)Math.Round(stroke * 10));
        if (!CometCache.TryGetValue(key, out var brush))
        {
            if (CometCache.Count > 600) CometCache.Clear();
            var f = Math.Max(.0025, q / 400d);
            var cap = Math.Min(.2, stroke / 2 / Math.Max(1, radius) / (2 * Math.PI) * 1.6);
            var data = new byte[pixels * pixels * 4]; var mid = pixels / 2d;
            for (var y = 0; y < pixels; y++)
                for (var x = 0; x < pixels; x++)
                {
                    var turn = (Math.Atan2(x + .5 - mid, -(y + .5 - mid)) / (2 * Math.PI) + 1) % 1; // 0 at 12 o'clock, clockwise
                    double a;
                    if (turn <= f) a = .22 + .78 * Math.Pow(turn / f, 1.35);
                    else if (turn <= f + cap) a = 1;
                    else if (turn >= 1 - cap) a = .22;
                    else a = 0;
                    var i = (y * pixels + x) * 4;
                    data[i] = (byte)(color.B * a); data[i + 1] = (byte)(color.G * a); data[i + 2] = (byte)(color.R * a); data[i + 3] = (byte)(255 * a);
                }
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(pixels, pixels, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, data, pixels * 4);
            bitmap.Freeze();
            brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            CometCache[key] = brush;
        }
        var placed = brush.Clone();
        placed.Viewport = new Rect(center.X - extent, center.Y - extent, extent * 2, extent * 2);
        placed.Freeze();
        return placed;
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point center, double radius, double start, double sweep)
    {
        if (sweep <= 0) return;
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
