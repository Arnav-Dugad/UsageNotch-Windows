using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace UsageNotch.Controls;

public sealed class ProviderLogo : FrameworkElement
{
    private static readonly Dictionary<string, Geometry> Logos = LoadLogos();
    internal static Geometry? Shape(string key) => Logos.GetValueOrDefault(key);
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(string), typeof(ProviderLogo), new FrameworkPropertyMetadata("codex", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColoredProperty = DependencyProperty.Register(
        nameof(Colored), typeof(bool), typeof(ProviderLogo), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Provider { get => (string)GetValue(ProviderProperty); set => SetValue(ProviderProperty, value); }
    public bool Colored { get => (bool)GetValue(ColoredProperty); set => SetValue(ColoredProperty, value); }
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(ProviderLogo),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static Dictionary<string, Geometry> LoadLogos()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/UsageNotch;component/Assets/provider-logos.json"))!.Stream;
        var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        return paths.ToDictionary(pair => pair.Key, pair =>
        {
            var geometry = Geometry.Parse(pair.Value);
            geometry.Freeze();
            return geometry;
        });
    }

    /// <summary>Gemini's 2025 spark: a blue base with green, red and yellow light falling off from three sides (Lobe Icons).</summary>
    private static readonly Brush[] GeminiLayers = CreateGeminiLayers();
    private static readonly Brush ClaudeCoral = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));

    private static Brush[] CreateGeminiLayers()
    {
        Brush Fade(Color c, Point from, Point to, double end = 1)
        {
            var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = from, EndPoint = to };
            brush.GradientStops.Add(new GradientStop(c, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), end));
            return Frozen(brush);
        }
        return
        [
            Frozen(new SolidColorBrush(Color.FromRgb(0x31, 0x86, 0xFF))),
            Fade(Color.FromRgb(0x08, 0xB9, 0x62), new Point(7, 15.5), new Point(11, 12)),
            Fade(Color.FromRgb(0xF9, 0x45, 0x43), new Point(8, 5.5), new Point(11.5, 11)),
            Fade(Color.FromRgb(0xFA, 0xBC, 0x12), new Point(3.5, 13.5), new Point(17.5, 12), .46)
        ];
    }

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    /// <summary>The artwork key for a provider id: Codex has its own mark; the OpenAI API keeps OpenAI's.</summary>
    internal static string KeyFor(string provider) => UsageNotch.Services.ProviderKind.Of(provider ?? "") switch
    {
        "openai_api" => "openai", "gemini" => "googlegemini", "anthropic_api" => "claude", var kind => kind
    };

    protected override void OnRender(DrawingContext context) => Draw(context, Provider, new Rect(0, 0, ActualWidth, ActualHeight), Foreground, Colored);

    /// <summary>Draws a provider's mark centred in a square inside <paramref name="bounds"/>; used by the dock and by images.</summary>
    public static void Draw(DrawingContext context, string provider, Rect bounds, Brush ink, bool colored)
    {
        var key = KeyFor(provider);
        if (!Logos.TryGetValue(key, out var geometry)) return;
        var scale = Math.Min(bounds.Width, bounds.Height) / 24;
        context.PushTransform(new TranslateTransform(bounds.X + (bounds.Width - 24 * scale) / 2, bounds.Y + (bounds.Height - 24 * scale) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        if (colored && key == "googlegemini")
            foreach (var layer in GeminiLayers) context.DrawGeometry(layer, null, geometry);
        else
            context.DrawGeometry(colored && key == "claude" ? ClaudeCoral : ink, null, geometry);
        context.Pop();
        context.Pop();
    }
}
