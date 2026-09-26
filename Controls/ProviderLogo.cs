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

    protected override void OnRender(DrawingContext context)
    {
        var key = Provider switch { "codex" or "openai_api" => "openai", "gemini" => "googlegemini", "anthropic_api" => "claude", _ => Provider };
        if (!Logos.TryGetValue(key, out var geometry)) return;
        Brush brush = Foreground;
        if (Colored)
        {
            brush = key switch
            {
                "claude" => new SolidColorBrush(Color.FromRgb(218, 119, 86)),
                "googlegemini" => new LinearGradientBrush(Color.FromRgb(79, 156, 255), Color.FromRgb(207, 109, 206), 45),
                _ => Foreground
            };
        }
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        context.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        context.DrawGeometry(brush, null, geometry);
        context.Pop();
        context.Pop();
    }
}
