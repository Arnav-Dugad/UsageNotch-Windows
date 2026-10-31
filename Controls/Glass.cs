using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UsageNotch.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace UsageNotch.Controls;

/// <summary>
/// Soft coloured light that drifts behind the glass, tinted by your providers' accents, as on Android. It repaints at most
/// 15 times a second and stands still with reduced motion.
/// </summary>
public sealed class AmbientBackground : FrameworkElement
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IReadOnlyList<Color> _colors = [];
    private bool _animate = true;

    public AmbientBackground()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => Update();
        Theme.Changed += InvalidateVisual;
    }

    public IReadOnlyList<Color> Colors { get => _colors; set { _colors = value; InvalidateVisual(); } }
    public bool Animate { get => _animate; set { _animate = value; Update(); } }

    private void Update()
    {
        if (_animate && IsLoaded && IsVisible) _timer.Start(); else _timer.Stop();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var t = Theme.Current; var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(new LinearGradientBrush(t.AmbientTop, t.AmbientBottom, 90), null, new Rect(0, 0, w, h));
        var palette = _colors.Concat([t.Accent, Accents.FromArgb(DesignTokens.Ambient[0]), Accents.FromArgb(DesignTokens.Ambient[1])]).Distinct().Take(4).ToArray();
        var phase = _clock.Elapsed.TotalSeconds;
        for (var i = 0; i < palette.Length; i++)
        {
            var speed = .045 + i * .013;
            var cx = w * (.2 + .6 * (i * .37 % 1)) + w * .16 * Math.Sin(phase * speed * 6.283 + i * 1.7);
            var cy = h * (.12 + .22 * i) + h * .07 * Math.Cos(phase * speed * 5.1 + i * 2.3);
            var radius = Math.Max(w, h) * (.5 + .08 * ((i + 1) % 3));
            var alpha = t.AmbientAlpha;
            var brush = new RadialGradientBrush { GradientOrigin = new Point(.5, .5), Center = new Point(.5, .5), RadiusX = .5, RadiusY = .5 };
            brush.GradientStops.Add(new GradientStop(Palette.Alpha(palette[i], alpha), 0));
            brush.GradientStops.Add(new GradientStop(Palette.Alpha(palette[i], alpha * .35), .5));
            brush.GradientStops.Add(new GradientStop(Palette.Alpha(palette[i], 0), 1));
            brush.Freeze();
            dc.DrawEllipse(brush, null, new Point(cx, cy), radius, radius);
        }
    }
}

/// <summary>Text whose digits roll to their new value one by one, like Android's RollingText.</summary>
public sealed class RollingNumber : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(RollingNumber),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((RollingNumber)d).Roll((string?)e.OldValue ?? "")));
    public static readonly DependencyProperty FontSizeProperty = System.Windows.Documents.TextElement.FontSizeProperty.AddOwner(typeof(RollingNumber),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(typeof(RollingNumber),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontWeightProperty = System.Windows.Documents.TextElement.FontWeightProperty.AddOwner(typeof(RollingNumber),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontFamilyProperty = System.Windows.Documents.TextElement.FontFamilyProperty.AddOwner(typeof(RollingNumber),
        new FrameworkPropertyMetadata(new FontFamily("Segoe UI Variable Display, Segoe UI"), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReducedProperty = DependencyProperty.Register(nameof(Reduced), typeof(bool), typeof(RollingNumber), new PropertyMetadata(false));
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(RollingNumber),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public bool Reduced { get => (bool)GetValue(ReducedProperty); set => SetValue(ReducedProperty, value); }

    private string _previous = "";
    private bool _up = true;

    private void Roll(string old)
    {
        _previous = old;
        _up = !double.TryParse(new string(Text.Where(char.IsDigit).ToArray()), out var next) || !double.TryParse(new string(old.Where(char.IsDigit).ToArray()), out var before) || next >= before;
        if (Reduced || !IsLoaded || old.Length == 0) { BeginAnimation(ProgressProperty, null); SetValue(ProgressProperty, 1d); return; }
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new SpringEase { Damping = .86 } });
    }

    private FormattedText Format(string text) => new(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private double DigitWidth => Format("0").WidthIncludingTrailingWhitespace;

    private double Advance(char c) => char.IsDigit(c) ? DigitWidth : Format(c.ToString()).WidthIncludingTrailingWhitespace;

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Text ?? "";
        return new Size(text.Sum(Advance), Format("0%").Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = Text ?? ""; var old = _previous; var p = (double)GetValue(ProgressProperty);
        var height = Format("0%").Height;
        dc.PushClip(new RectangleGeometry(new Rect(0, -2, Math.Max(ActualWidth, 1), height + 4)));
        // Align both strings at the right, so digits keep their places as the number grows or shrinks.
        var x = 0d;
        var pad = old.Length > text.Length ? 0 : text.Length - old.Length;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i]; var w = Advance(c);
            var oi = i - pad; var o = oi >= 0 && oi < old.Length ? old[oi] : '\0';
            var glyph = Format(c.ToString());
            var gx = x + (w - glyph.WidthIncludingTrailingWhitespace) / 2;
            if (p >= 1 || o == c || !char.IsDigit(c))
                dc.DrawText(glyph, new Point(gx, 0));
            else
            {
                var dir = _up ? 1 : -1;
                var staggered = Math.Clamp(p * 1.15 - (text.Length - 1 - i) * .05, 0, 1);
                if (o != '\0') { dc.PushOpacity(1 - staggered); dc.DrawText(Format(o.ToString()), new Point(gx, -dir * height * staggered)); dc.Pop(); }
                dc.PushOpacity(staggered); dc.DrawText(glyph, new Point(gx, dir * height * (1 - staggered))); dc.Pop();
            }
            x += w;
        }
        dc.Pop();
    }
}

/// <summary>
/// A glass segmented control whose selection is a drop of glass that glides with a spring and stretches with its speed,
/// like Android's tab bar.
/// </summary>
public sealed class GlassTabBar : Grid
{
    private readonly Border _drop = new() { CornerRadius = new CornerRadius(999), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, IsHitTestVisible = false };
    private readonly TranslateTransform _move = new();
    private readonly ScaleTransform _stretch = new(1, 1);
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal };
    private readonly List<Border> _cells = [];
    private int _selected;
    public bool Reduced { get; set; }
    public event Action<int>? SelectionChanged;

    public GlassTabBar(IReadOnlyList<string> labels, int selected = 0, double height = 36, double fontSize = 13, IReadOnlyList<string>? glyphs = null)
    {
        _selected = selected;
        Height = height;
        var surface = new Border { CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1), Padding = new Thickness(3) };
        surface.SetResourceReference(Border.BackgroundProperty, "Ui.Glass");
        surface.SetResourceReference(Border.BorderBrushProperty, "Ui.Rim");
        Children.Add(surface);
        var inner = new Grid { Margin = new Thickness(4, 3, 4, 3) };
        _drop.SetResourceReference(Border.BackgroundProperty, "Ui.Selected");
        _drop.SetResourceReference(Border.BorderBrushProperty, "Ui.Rim");
        _drop.BorderThickness = new Thickness(1);
        _drop.RenderTransformOrigin = new Point(.5, .5);
        _drop.RenderTransform = new TransformGroup { Children = { _stretch, _move } };
        inner.Children.Add(_drop);
        inner.Children.Add(_items);
        Children.Add(inner);
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (glyphs?.ElementAtOrDefault(i) is { Length: > 0 } glyph) row.Children.Add(Ui.Icon(glyph, fontSize + 1, "Ui.Muted", new Thickness(0, 0, 7, 0)));
            row.Children.Add(Ui.Text(labels[i], fontSize, FontWeights.SemiBold, "Ui.Muted"));
            var cell = new Border { Padding = new Thickness(16, 0, 16, 0), Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = row };
            System.Windows.Automation.AutomationProperties.SetName(cell, labels[i]);
            cell.MouseLeftButtonDown += (_, e) => { Motion.Press(cell, true, Reduced); e.Handled = true; };
            cell.MouseLeftButtonUp += (_, e) => { Motion.Press(cell, false, Reduced); Select(index, true); e.Handled = true; };
            cell.MouseLeave += (_, _) => Motion.Press(cell, false, Reduced);
            _cells.Add(cell); _items.Children.Add(cell);
        }
        Loaded += (_, _) => { Paint(); Place(false); };
        _items.SizeChanged += (_, _) => Place(false);
        Theme.Changed += Paint;
    }

    public int Selected { get => _selected; set => Select(value, false); }

    public void Select(int index, bool user)
    {
        if (index < 0 || index >= _cells.Count) return;
        var changed = index != _selected;
        _selected = index;
        Paint(); Place(true);
        if (changed && user) SelectionChanged?.Invoke(index);
    }

    private void Paint()
    {
        for (var i = 0; i < _cells.Count; i++)
            foreach (var text in Ui.Descendants<FrameworkElement>(_cells[i]))
                if (text is TextBlock block) block.SetResourceReference(TextBlock.ForegroundProperty, i == _selected ? "Ui.Text" : "Ui.Muted");
    }

    private void Place(bool animate)
    {
        if (_cells.Count == 0) return;
        var cell = _cells[_selected];
        if (cell.ActualWidth <= 0 || !_items.IsAncestorOf(cell)) return;
        var x = cell.TranslatePoint(new Point(0, 0), _items).X;
        var width = cell.ActualWidth;
        if (width <= 0) return;
        var reduced = Reduced || !animate;
        var distance = Math.Abs(_move.X - x);
        _drop.Width = width;
        Motion.Spring(_move, TranslateTransform.XProperty, x, reduced, .72, 560);
        if (!reduced && distance > 4)
        {
            // Stretch with the speed of the glide, then settle.
            var stretch = 1 + Math.Min(.35, distance / 600);
            _stretch.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames = { new EasingDoubleKeyFrame(stretch, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new QuadraticEase()),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), new SpringEase { Damping = .5 }) },
                FillBehavior = FillBehavior.Stop
            });
            _stretch.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames = { new EasingDoubleKeyFrame(1 / Math.Sqrt(stretch), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new QuadraticEase()),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), new SpringEase { Damping = .5 }) },
                FillBehavior = FillBehavior.Stop
            });
        }
    }
}

/// <summary>A capsule meter in the usage colour that springs to its value, with a tick for the budget.</summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((UsageBar)d).Animate()));
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    /// <summary>The used fraction that picks the colour (Value can be the remaining fraction).</summary>
    public double Used { get; set; }
    public bool Unknown { get; set; }
    public double Budget { get; set; } = double.NaN;
    public bool Reduced { get; set; }
    public int Delay { get; set; }

    public UsageBar() { Height = 7; Loaded += (_, _) => Animate(true); Theme.Changed += InvalidateVisual; }

    private void Animate(bool first = false)
    {
        var target = double.IsFinite(Value) ? Math.Clamp(Value, 0, 1) : 0;
        if (!IsLoaded || Reduced) { BeginAnimation(ShownProperty, null); SetValue(ShownProperty, target); return; }
        var from = first ? 0 : (double)GetValue(ShownProperty);
        SetValue(ShownProperty, target);
        BeginAnimation(ShownProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(760))
        { BeginTime = TimeSpan.FromMilliseconds(first ? Delay : 0), EasingFunction = new SpringEase { Damping = .82 }, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 2) return;
        var t = Theme.Current; var h = ActualHeight; var r = h / 2;
        dc.DrawRoundedRectangle(Palette.Frozen(t.Track), null, new Rect(0, 0, ActualWidth, h), r, r);
        if (Unknown)
        {
            for (double x = 0; x < ActualWidth - 3; x += 8) dc.DrawRoundedRectangle(Palette.Frozen(Palette.Alpha(Palette.Unknown, .55)), null, new Rect(x, 0, 4, h), r, r);
            return;
        }
        var shown = Math.Clamp((double)GetValue(ShownProperty), 0, 1.02);
        var width = Math.Min(ActualWidth, shown * ActualWidth);
        if (width > 1)
        {
            var color = Palette.Usage(Used);
            var fill = new LinearGradientBrush(Palette.Alpha(color, .55), color, 0);
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, width, h), Math.Min(r, width / 2), r);
            dc.DrawRoundedRectangle(Palette.Frozen(Palette.Alpha(Colors.White, .22)), null, new Rect(1, .5, Math.Max(0, width - 2), Math.Max(1, h * .35)), r * .6, r * .6);
        }
        if (double.IsFinite(Budget))
        {
            var x = Math.Clamp(Budget, 0, 1) * ActualWidth;
            dc.DrawRoundedRectangle(Palette.Frozen(t.Ink), null, new Rect(x - 1, -3, 2, h + 6), 1, 1);
        }
    }
}

/// <summary>Building blocks for the code-built pages, all bound to the theme brushes.</summary>
public static class Ui
{
    public static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string brush = "Ui.Text", bool wrap = true)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontFamily = size >= 20 ? Display : Body, FontWeight = weight ?? FontWeights.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    public static TextBlock Icon(string glyph, double size = 14, string brush = "Ui.Text", Thickness? margin = null)
    {
        var block = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    public static TextBlock Label(string text) { var t = Text(text.ToUpperInvariant(), 11, FontWeights.SemiBold, "Ui.Muted", false); t.Margin = new Thickness(6, 18, 0, 8); return t; }

    /// <summary>A glass card: translucent body, specular rim and a top sheen, optionally tinted with a provider's accent.</summary>
    public static Border Card(UIElement child, Color? tint = null, double radius = DesignTokens.Radius.Card, Thickness? padding = null, bool strong = false)
    {
        var layers = new Grid();
        if (tint is { } c) layers.Children.Add(new Border { CornerRadius = new CornerRadius(radius - 1), Background = Palette.Frozen(Palette.Alpha(c, Theme.Current.Dark ? .10 : .07)), IsHitTestVisible = false });
        var sheen = new Border { CornerRadius = new CornerRadius(radius - 1), IsHitTestVisible = false };
        sheen.SetResourceReference(Border.BackgroundProperty, "Ui.Sheen");
        layers.Children.Add(sheen);
        var content = new Border { Padding = padding ?? new Thickness(20, 18, 20, 18), Child = child };
        layers.Children.Add(content);
        var card = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), Child = layers, Margin = new Thickness(0, 0, 0, 12), SnapsToDevicePixels = true };
        card.SetResourceReference(Border.BackgroundProperty, strong ? "Ui.GlassStrong" : "Ui.Glass");
        card.SetResourceReference(Border.BorderBrushProperty, "Ui.Rim");
        return card;
    }

    /// <summary>A quieter surface inside a card.</summary>
    public static Border Inner(UIElement child, Thickness? padding = null, double radius = DesignTokens.Radius.Inner)
    {
        var b = new Border { CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(14, 12, 14, 12), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, "Ui.Inner");
        return b;
    }

    public static Border Pill(string text, Color color, double size = 11)
    {
        var t = Theme.Current;
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, FontFamily = Body, Foreground = Palette.Frozen(Palette.Readable(color, Palette.Lerp(t.Background, color, .16))) };
        return new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 3, 9, 3), Background = Palette.Frozen(Palette.Alpha(color, .16)), Child = label, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>A glass capsule button that springs down under the pointer.</summary>
    public static Border Button(object content, Action click, bool prominent = false, string? glyph = null, bool reduced = false, string? tooltip = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null) row.Children.Add(Icon(glyph, 13, prominent ? "Ui.OnAccent" : "Ui.Text", new Thickness(0, 0, content is string { Length: > 0 } ? 8 : 0, 0)));
        if (content is string label) { if (label.Length > 0) row.Children.Add(Text(label, 12.5, FontWeights.SemiBold, prominent ? "Ui.OnAccent" : "Ui.Text", false)); }
        else if (content is UIElement element) row.Children.Add(element);
        var button = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(glyph is not null && content is "" ? 10 : 16, 8, glyph is not null && content is "" ? 10 : 16, 8), BorderThickness = new Thickness(1), Child = row, Cursor = System.Windows.Input.Cursors.Hand, Focusable = true, ToolTip = tooltip };
        button.SetResourceReference(Border.BackgroundProperty, prominent ? "Ui.Accent" : "Ui.Inner");
        button.SetResourceReference(Border.BorderBrushProperty, "Ui.Rim");
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip ?? content as string ?? glyph ?? "Button");
        button.MouseEnter += (_, _) => { if (!prominent) button.SetResourceReference(Border.BackgroundProperty, "Ui.InnerHover"); else button.Opacity = .92; };
        button.MouseLeave += (_, _) => { if (!prominent) button.SetResourceReference(Border.BackgroundProperty, "Ui.Inner"); button.Opacity = 1; Motion.Press(button, false, reduced); };
        button.MouseLeftButtonDown += (_, e) => { Motion.Press(button, true, reduced); e.Handled = true; };
        button.MouseLeftButtonUp += (_, e) => { Motion.Press(button, false, reduced); click(); e.Handled = true; };
        button.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { click(); e.Handled = true; } };
        return button;
    }

    public static Border Chip(string text, bool selected, Action click, UIElement? leading = null, bool reduced = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (leading is not null) { leading.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 7, 0)); row.Children.Add(leading); }
        row.Children.Add(Text(text, 12.5, selected ? FontWeights.SemiBold : FontWeights.Normal, selected ? "Ui.Text" : "Ui.Muted", false));
        var chip = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(13, 6, 13, 6), BorderThickness = new Thickness(1), Child = row, Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand };
        chip.SetResourceReference(Border.BackgroundProperty, selected ? "Ui.Selected" : "Ui.Inner");
        chip.SetResourceReference(Border.BorderBrushProperty, selected ? "Ui.Rim" : "Ui.Hairline");
        chip.MouseLeftButtonDown += (_, e) => { Motion.Press(chip, true, reduced); e.Handled = true; };
        chip.MouseLeftButtonUp += (_, e) => { Motion.Press(chip, false, reduced); click(); e.Handled = true; };
        chip.MouseLeave += (_, _) => Motion.Press(chip, false, reduced);
        return chip;
    }

    /// <summary>An iOS-style switch whose thumb springs across.</summary>
    public static Border Switch(bool value, Action<bool> changed, bool reduced = false, string name = "Switch")
    {
        var thumb = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = .35 } };
        var move = new TranslateTransform(value ? 18 : 0, 0); thumb.RenderTransform = move;
        var track = new Border { Width = 42, Height = 24, CornerRadius = new CornerRadius(12), Child = thumb, Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Focusable = true };
        System.Windows.Automation.AutomationProperties.SetName(track, name);
        void Paint(bool on) { if (on) track.Background = Palette.Frozen(Accents.FromArgb(DesignTokens.Live)); else track.SetResourceReference(Border.BackgroundProperty, "Ui.Track"); }
        Paint(value);
        var state = value;
        void Flip() { state = !state; Paint(state); Motion.Spring(move, TranslateTransform.XProperty, state ? 18 : 0, reduced, .62, 420); changed(state); }
        track.MouseLeftButtonUp += (_, e) => { Flip(); e.Handled = true; };
        track.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { Flip(); e.Handled = true; } };
        return track;
    }

    /// <summary>A setting row: title and detail on the left, a control on the right.</summary>
    public static Grid SettingRow(string title, string? detail, UIElement control)
    {
        var text = Stack(Text(title, 13.5, FontWeights.Normal));
        if (detail is not null) { var d = Text(detail, 11.5, FontWeights.Normal, "Ui.Muted"); d.Margin = new Thickness(0, 2, 0, 0); text.Children.Add(d); }
        text.VerticalAlignment = VerticalAlignment.Center;
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(14, 0, 0, 0); }
        var row = Columns((text, new GridLength(1, GridUnitType.Star)), (control, GridLength.Auto));
        row.Margin = new Thickness(0, 8, 0, 8);
        return row;
    }

    public static ProviderLogo Logo(string id, double size = 18, bool colored = true)
    {
        var logo = new ProviderLogo { Provider = id, Colored = colored, Width = size, Height = size, IsHitTestVisible = false };
        logo.SetResourceReference(ProviderLogo.ForegroundProperty, "Ui.Text");
        return logo;
    }

    /// <summary>A logo on a small glass tile, as in the desktop card header.</summary>
    public static Border LogoTile(string id, double size = 34)
    {
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * .32), Child = Logo(id, size * .52) };
        tile.SetResourceReference(Border.BackgroundProperty, "Ui.Inner");
        return tile;
    }

    public static Grid Columns(params (UIElement Element, GridLength Width)[] cells)
    {
        var grid = new Grid();
        for (var i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = cells[i].Width });
            Grid.SetColumn(cells[i].Element, i);
            grid.Children.Add(cells[i].Element);
        }
        return grid;
    }

    public static StackPanel Stack(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var c in children) stack.Children.Add(c);
        return stack;
    }

    public static StackPanel Row(double gap, params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < children.Length; i++) { if (i > 0 && children[i] is FrameworkElement fe) fe.Margin = new Thickness(gap, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom); row.Children.Add(children[i]); }
        return row;
    }

    /// <summary>Logical descendants, so it works on code-built trees before they are shown.</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>Makes awaits in a view's async loader resume on its own UI thread, even where no context was installed yet.</summary>
    public static void EnsureContext(System.Windows.Threading.DispatcherObject owner)
    {
        if (System.Threading.SynchronizationContext.Current is not System.Windows.Threading.DispatcherSynchronizationContext)
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(owner.Dispatcher));
    }

    public static string Percent(double? fraction) => fraction is { } f ? $"{Math.Round(Math.Clamp(f, 0, 9.99) * 100):0}%" : "—";
    public static string Tokens(long n) => n switch { >= 1_000_000_000 => $"{n / 1e9:0.##}B", >= 1_000_000 => $"{n / 1e6:0.#}M", >= 10_000 => $"{n / 1e3:0}K", >= 1000 => $"{n / 1e3:0.#}K", _ => n.ToString("N0") };
    public static string Money(double? usd) => usd is { } v ? v >= 100 ? $"${v:N0}" : $"${v:0.00}" : "—";
}
