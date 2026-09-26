using System.Windows;
using System.Windows.Media;
using UsageNotch.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace UsageNotch.Controls;

/// <summary>
/// The dock silhouette. A single parametric outline morphs continuously between a free
/// floating capsule (Morph = 0) and a teardrop clinging to the screen edge (Morph = 1):
/// both shapes are described by the same five curve segments, so every value in between
/// is a real shape rather than a cross-fade.
/// </summary>
public sealed class DockSurface : FrameworkElement
{
    private bool _changingShape;
    private Color? _paintColor;
    private System.Windows.Media.Brush? _bodyPaint;
    private System.Windows.Media.Brush? _sheenPaint;
    private Pen? _rimPaint;

    public void SetDragShape(double morph, Vector reach)
    {
        if (Math.Abs(Morph - morph) < .0001 && (Reach - reach).LengthSquared < .00000001) return;
        _changingShape = true;
        try { Morph = morph; Reach = reach; }
        finally { _changingShape = false; }
        RefreshOutline();
    }
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(System.Windows.Media.Brush), typeof(DockSurface),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LeftEdgeProperty = DependencyProperty.Register(
        nameof(LeftEdge), typeof(bool), typeof(DockSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnOutlineChanged));

    public static readonly DependencyProperty TopEdgeProperty = DependencyProperty.Register(nameof(TopEdge), typeof(bool), typeof(DockSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnOutlineChanged));
    public bool TopEdge { get => (bool)GetValue(TopEdgeProperty); set => SetValue(TopEdgeProperty, value); }
    private static readonly DependencyPropertyKey OutlinePropertyKey = DependencyProperty.RegisterReadOnly(nameof(Outline), typeof(Geometry), typeof(DockSurface), new PropertyMetadata(null));
    public static readonly DependencyProperty OutlineProperty = OutlinePropertyKey.DependencyProperty;
    public Geometry? Outline => (Geometry?)GetValue(OutlineProperty);
    private static void OnOutlineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DockSurface { _changingShape: false } surface) surface.RefreshOutline();
    }
    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        RefreshOutline(finalSize.Width, finalSize.Height);
        return base.ArrangeOverride(finalSize);
    }
    private void RefreshOutline() => RefreshOutline(ActualWidth, ActualHeight);
    private void RefreshOutline(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        var reach = TopEdge ? new Vector(-Reach.Y, Reach.X) : new Vector(LeftEdge ? -Reach.X : Reach.X, Reach.Y);
        var geometry = BuildOutline(TopEdge ? height : width, TopEdge ? width : height, Math.Clamp(Morph, 0, 1), reach, ShapeScale)?.Clone();
        if (geometry is null) return;
        if (TopEdge) geometry.Transform = new MatrixTransform(0, -1, 1, 0, 0, height);
        else if (LeftEdge) geometry.Transform = new MatrixTransform(-1, 0, 0, 1, width, 0);
        geometry.Freeze();
        SetValue(OutlinePropertyKey, geometry);
    }

    public static readonly DependencyProperty MorphProperty = DependencyProperty.Register(
        nameof(Morph), typeof(double), typeof(DockSurface),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, OnOutlineChanged));

    public static readonly DependencyProperty ReachProperty = DependencyProperty.Register(
        nameof(Reach), typeof(Vector), typeof(DockSurface),
        new FrameworkPropertyMetadata(default(Vector), FrameworkPropertyMetadataOptions.AffectsRender, OnOutlineChanged));
    public Vector Reach { get => (Vector)GetValue(ReachProperty); set => SetValue(ReachProperty, value); }
    public static readonly DependencyProperty ShapeScaleProperty = DependencyProperty.Register(
        nameof(ShapeScale), typeof(double), typeof(DockSurface),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, OnOutlineChanged));
    public double ShapeScale { get => (double)GetValue(ShapeScaleProperty); set => SetValue(ShapeScaleProperty, value); }

    public static readonly DependencyProperty GlassProperty = DependencyProperty.Register(
        nameof(Glass), typeof(bool), typeof(DockSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Windows.Media.Brush Fill { get => (System.Windows.Media.Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool LeftEdge { get => (bool)GetValue(LeftEdgeProperty); set => SetValue(LeftEdgeProperty, value); }
    public double Morph { get => (double)GetValue(MorphProperty); set => SetValue(MorphProperty, value); }
    public bool Glass { get => (bool)GetValue(GlassProperty); set => SetValue(GlassProperty, value); }

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (height < 48 || width < 10) return;

        var geometry = Outline;
        if (geometry is null) return;

        var baseColor = (Fill as SolidColorBrush)?.Color ?? Colors.Black;
        if (_paintColor != baseColor)
        {
            var light = Palette.IsLight(baseColor);
            _bodyPaint = BodyBrush(baseColor, light);
            _rimPaint = new Pen(Palette.Frozen(Palette.Alpha(light ? Colors.Black : Colors.White, light ? 0.08 : 0.10)), .8);
            _rimPaint.Freeze();
            var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            sheen.GradientStops.Add(new GradientStop(Palette.Alpha(Colors.White, light ? 0.28 : 0.055), 0));
            sheen.GradientStops.Add(new GradientStop(Palette.Alpha(Colors.White, 0), 0.34));
            sheen.GradientStops.Add(new GradientStop(Palette.Alpha(Colors.White, 0), 0.72));
            sheen.GradientStops.Add(new GradientStop(Palette.Alpha(light ? Colors.Black : Colors.White, light ? 0.05 : 0.025), 1));
            sheen.Freeze();
            _sheenPaint = sheen;
            _paintColor = baseColor;
        }
        context.DrawGeometry(_bodyPaint, _rimPaint, geometry);
        if (Glass)
        {
            context.PushClip(geometry);
            context.DrawRectangle(_sheenPaint, null, new Rect(0, 0, width, height));
            context.Pop();
        }

    }

    private static System.Windows.Media.Brush BodyBrush(Color baseColor, bool light)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0.1, 0), EndPoint = new Point(0.9, 1) };
        brush.GradientStops.Add(new GradientStop(light ? Palette.Lighten(baseColor, 0.14) : Palette.Lighten(baseColor, 0.06), 0));
        brush.GradientStops.Add(new GradientStop(baseColor, 0.48));
        brush.GradientStops.Add(new GradientStop(light ? Palette.Darken(baseColor, 0.07) : Palette.Darken(baseColor, 0.35), 1));
        brush.Freeze();
        return brush;
    }

    private static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>
    /// Capsule and teardrop share one topology — start point, two cubics, a line, two cubics,
    /// close — so the outline can be interpolated point by point.
    /// </summary>
    private static Geometry? Build(double w, double h, double t) => BuildOutline(w, h, t, default);

    private static Geometry? BuildOutline(double w, double h, double t, Vector reach, double shapeScale = 1)
    {
        var radius = Math.Min(w / 2, h / 2);
        if (radius <= 1) return null;
        var handle = radius * 0.5523;
        var half = w / 2;

        var unit = Math.Clamp(shapeScale, .1, 3);
        var shoulder = Math.Min(54 * unit, w * 0.62);
        var waist = Math.Clamp(shoulder + 46 * unit, shoulder + 6 * unit, Math.Max(shoulder + 6 * unit, h / 2 - 1));
        if (waist >= h / 2) waist = Math.Max(1, h / 2 - 1);

        // Capsule (free floating).
        Point cStart = new(w, radius);
        Point c1a = new(w, radius - handle), c1b = new(half + handle, 0), c1e = new(half, 0);
        Point c2a = new(half - handle, 0), c2b = new(0, radius - handle), c2e = new(0, radius);
        Point c3e = new(0, h - radius);
        Point c4a = new(0, h - radius + handle), c4b = new(half - handle, h), c4e = new(half, h);
        Point c5a = new(half + handle, h), c5b = new(w, h - radius + handle), c5e = new(w, h - radius);

        // Teardrop (clinging to the edge at x = w).
        Point tStart = new(w, 0);
        Point t1a = new(w, shoulder * 0.58), t1b = new(w * 0.99, shoulder * 0.95), t1e = new(w * 0.52, shoulder);
        Point t2a = new(w * 0.13, shoulder + unit), t2b = new(0, shoulder + 22 * unit), t2e = new(0, waist);
        Point t3e = new(0, h - waist);
        Point t4a = new(0, h - shoulder - 22 * unit), t4b = new(w * 0.13, h - shoulder - unit), t4e = new(w * 0.52, h - shoulder);
        Point t5a = new(w * 0.99, h - shoulder * 0.95), t5b = new(w, h - shoulder * 0.58), t5e = new(w, h);

        var geometry = new StreamGeometry();
        // A bounded directional reach gives cross-axis intent without rotating or stretching
        // the icons. Endpoints stay within the window, so no transparent hit box leaks out.
        Point Blend(Point a, Point b)
        {
            var p = Lerp(a, b, t);
            var x = Math.Clamp(p.X / w, 0, 1);
            var y = Math.Clamp(p.Y / h, 0, 1);
            return new Point(p.X + Math.Clamp(reach.X, -1, 1) * .32 * w * x * (1 - x),
                p.Y + Math.Clamp(reach.Y, -1, 1) * .32 * Math.Min(h, w * 2) * y * (1 - y));
        }
        using (var path = geometry.Open())
        {
            path.BeginFigure(Blend(cStart, tStart), true, true);
            path.BezierTo(Blend(c1a, t1a), Blend(c1b, t1b), Blend(c1e, t1e), true, true);
            path.BezierTo(Blend(c2a, t2a), Blend(c2b, t2b), Blend(c2e, t2e), true, true);
            path.LineTo(Blend(c3e, t3e), true, true);
            path.BezierTo(Blend(c4a, t4a), Blend(c4b, t4b), Blend(c4e, t4e), true, true);
            path.BezierTo(Blend(c5a, t5a), Blend(c5b, t5b), Blend(c5e, t5e), true, true);
        }
        geometry.Freeze();
        return geometry;
    }
}
