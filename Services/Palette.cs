using System.Windows.Media;
using UsageNotch.Models;
using Color = System.Windows.Media.Color;

namespace UsageNotch.Services;

/// <summary>Single source of truth for usage colours so rings, bars, chips and text always agree.</summary>
public static class Palette
{
    private static readonly (double Stop, Color Color)[] Ramp =
    [
        (0.00, Color.FromRgb(0x2E, 0xE0, 0xA8)),
        (0.42, Color.FromRgb(0x66, 0xE0, 0x62)),
        (0.60, Color.FromRgb(0xF2, 0xD0, 0x3C)),
        (0.78, Color.FromRgb(0xFF, 0x9E, 0x2C)),
        (0.92, Color.FromRgb(0xFF, 0x64, 0x4E)),
        (1.00, Color.FromRgb(0xFF, 0x3B, 0x30))
    ];

    public static readonly Color Unknown = Color.FromRgb(0x6E, 0x6E, 0x7A);
    public static readonly Color Warning = Color.FromRgb(0xFF, 0xB5, 0x47);
    public static readonly Color Danger = Color.FromRgb(0xFF, 0x5F, 0x57);
    public static readonly Color Loading = Color.FromRgb(0x8F, 0xB6, 0xFF);
    public static readonly Color Accent = Color.FromRgb(0x6E, 0x8E, 0xFF);

    public static Color Usage(double fraction)
    {
        var f = Math.Clamp(fraction, 0, 1);
        for (var i = 1; i < Ramp.Length; i++)
        {
            if (f > Ramp[i].Stop) continue;
            var span = Ramp[i].Stop - Ramp[i - 1].Stop;
            var t = span <= 0 ? 0 : (f - Ramp[i - 1].Stop) / span;
            return Lerp(Ramp[i - 1].Color, Ramp[i].Color, t);
        }
        return Ramp[^1].Color;
    }

    public static Color For(SnapshotStatus status, double? fraction) => status switch
    {
        SnapshotStatus.Error => Danger,
        SnapshotStatus.NeedsAuth or SnapshotStatus.Unsupported => Warning,
        SnapshotStatus.Loading => Loading,
        _ => fraction.HasValue ? Usage(fraction.Value) : Unknown
    };

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static Color Alpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), color.R, color.G, color.B);

    public static Color Mix(Color color, Color towards, double amount) => Lerp(color, towards, amount);

    public static Color Lighten(Color color, double amount) => Lerp(color, Colors.White, amount);

    public static Color Darken(Color color, double amount) => Lerp(color, Colors.Black, amount);

    public static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    public static bool IsLight(Color color) => Luminance(color) > 0.179;

    public static double Contrast(Color a, Color b) =>
        (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);

    public static Color Readable(Color ink, Color surface, double minimum = 4.5)
    {
        if (Contrast(ink, surface) >= minimum) return ink;
        var target = Contrast(Colors.Black, surface) >= Contrast(Colors.White, surface) ? Colors.Black : Colors.White;
        double low = 0, high = 1;
        for (var i = 0; i < 16; i++)
        {
            var mid = (low + high) / 2;
            if (Contrast(Lerp(ink, target, mid), surface) < minimum) low = mid;
            else high = mid;
        }
        return Lerp(ink, target, high);
    }

    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
