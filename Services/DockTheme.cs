using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace UsageNotch.Services;

/// <summary>Derives the whole dock/card palette from the single user-chosen surface colour.</summary>
public static class DockTheme
{
    public static Color Parse(string? value)
    {
        try
        {
            var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(value ?? "#0B0B0E");
            return Color.FromRgb(color.R, color.G, color.B);
        }
        catch { return Color.FromRgb(0x0B, 0x0B, 0x0E); }
    }

    public static void Apply(ResourceDictionary resources, string colorText)
    {
        var color = Parse(colorText);
        if (resources["DockFill"] is SolidColorBrush existing && existing.Color == color) return;
        var light = Palette.IsLight(color);
        var ink = light ? Colors.Black : Colors.White;

        resources["DockFill"] = Palette.Frozen(color);
        resources["DockText"] = Palette.Frozen(light ? Color.FromRgb(0x10, 0x10, 0x12) : Color.FromRgb(0xF6, 0xF6, 0xF8));
        resources["DockMuted"] = Palette.Frozen(Palette.Readable(Palette.Lerp(color, ink, .68), color));
        resources["DockFaint"] = Palette.Frozen(Palette.Readable(Palette.Lerp(color, ink, .56), color));
        resources["DockHairline"] = Palette.Frozen(Palette.Alpha(ink, light ? 0.12 : 0.10));
        resources["DockRaised"] = Palette.Frozen(Palette.Alpha(ink, light ? 0.055 : 0.06));
        resources["DockRaisedHover"] = Palette.Frozen(Palette.Alpha(ink, light ? 0.10 : 0.11));
        resources["DockAccent"] = Palette.Frozen(Palette.Accent);
        resources["DockRingTrack"] = Palette.Frozen(Palette.Alpha(ink, light ? 0.16 : 0.13));
        resources["DockRingRim"] = Palette.Frozen(Palette.Alpha(ink, light ? 0.10 : 0.07));
        resources["DockDiscFill"] = DiscBrush(color, light);
        resources["DockIsLight"] = light;
    }

    private static System.Windows.Media.Brush DiscBrush(Color surface, bool light)
    {
        var top = light ? Palette.Darken(surface, 0.10) : Palette.Lighten(surface, 0.13);
        var bottom = light ? Palette.Darken(surface, 0.02) : Palette.Darken(surface, 0.45);
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.42, 0.28),
            Center = new System.Windows.Point(0.5, 0.42),
            RadiusX = 0.86,
            RadiusY = 0.86
        };
        brush.GradientStops.Add(new GradientStop(top, 0));
        brush.GradientStops.Add(new GradientStop(Palette.Lerp(top, bottom, 0.6), 0.6));
        brush.GradientStops.Add(new GradientStop(bottom, 1));
        brush.Freeze();
        return brush;
    }
}
