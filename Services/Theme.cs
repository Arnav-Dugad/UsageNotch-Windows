using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace UsageNotch.Services;

/// <summary>The workspace palette for one appearance, from design/tokens.json (the same values Android uses).</summary>
public sealed record UiTokens(bool Dark, Color Background, Color BackgroundEnd, Color AmbientTop, Color AmbientBottom, Color Card, Color Raised,
    Color Hairline, Color Text, Color Muted, Color Faint, Color Accent, Color OnAccent, Color Track, Color Ink, Color Glass, Color GlassRim, double AmbientAlpha);

/// <summary>
/// Light and dark appearance for Stats &amp; Settings, following Windows or your choice. Pages bind to the "Ui.*" brushes with
/// DynamicResource, so switching repaints everything in place.
/// </summary>
public static class Theme
{
    public static UiTokens Current { get; private set; } = For(true);
    /// <summary>Classic style: solid grouped cards on a plain background, no ambient light or glass.</summary>
    public static bool Classic { get; private set; }
    public static event Action? Changed;

    private static Color C(uint argb) => Accents.FromArgb(argb);

    public static UiTokens For(bool dark) => dark
        ? new(true, C(DesignTokens.Dark.Background), C(DesignTokens.Dark.BackgroundEnd), C(DesignTokens.Dark.AmbientTop), C(DesignTokens.Dark.AmbientBottom),
            C(DesignTokens.Dark.Card), C(DesignTokens.Dark.Raised), C(DesignTokens.Dark.Hairline), C(DesignTokens.Dark.Text), C(DesignTokens.Dark.Muted),
            C(DesignTokens.Dark.Faint), C(DesignTokens.Dark.Accent), C(DesignTokens.Dark.OnAccent), C(DesignTokens.Dark.Track), C(DesignTokens.Dark.Ink),
            C(DesignTokens.Dark.GlassFill), C(DesignTokens.Dark.GlassRim), DesignTokens.Dark.AmbientAlpha)
        : new(false, C(DesignTokens.Light.Background), C(DesignTokens.Light.BackgroundEnd), C(DesignTokens.Light.AmbientTop), C(DesignTokens.Light.AmbientBottom),
            C(DesignTokens.Light.Card), C(DesignTokens.Light.Raised), C(DesignTokens.Light.Hairline), C(DesignTokens.Light.Text), C(DesignTokens.Light.Muted),
            C(DesignTokens.Light.Faint), C(DesignTokens.Light.Accent), C(DesignTokens.Light.OnAccent), C(DesignTokens.Light.Track), C(DesignTokens.Light.Ink),
            C(DesignTokens.Light.GlassFill), C(DesignTokens.Light.GlassRim), DesignTokens.Light.AmbientAlpha);

    /// <summary>Windows' app mode (Settings → Personalisation → Colours).</summary>
    public static bool SystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch { return true; }
    }

    public static bool IsDark(AppSettings settings) => settings.AppTheme switch { "Light" => false, "Dark" => true, _ => SystemDark() };

    public static void Apply(ResourceDictionary resources, AppSettings settings)
    {
        var t = For(IsDark(settings));
        if (settings.ClassicStyle)
            // The grouped look of the phone's Classic style: true black or iOS grey behind solid cards.
            t = t with
            {
                Background = t.Dark ? Colors.Black : Color.FromRgb(0xF2, 0xF2, 0xF7), BackgroundEnd = t.Dark ? Colors.Black : Color.FromRgb(0xF2, 0xF2, 0xF7),
                AmbientTop = t.Dark ? Colors.Black : Color.FromRgb(0xF2, 0xF2, 0xF7), AmbientBottom = t.Dark ? Colors.Black : Color.FromRgb(0xF2, 0xF2, 0xF7),
                Card = t.Dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White, Raised = t.Dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Color.FromRgb(0xF2, 0xF2, 0xF7),
                Glass = t.Dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White, GlassRim = t.Dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00), AmbientAlpha = 0
            };
        var changed = t != Current || Classic != settings.ClassicStyle;
        Current = t; Classic = settings.ClassicStyle;
        SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        resources["Ui.Background"] = B(t.Background);
        resources["Ui.Card"] = B(t.Card);
        resources["Ui.Raised"] = B(t.Raised);
        resources["Ui.Hairline"] = B(t.Hairline);
        resources["Ui.Text"] = B(t.Text);
        resources["Ui.Muted"] = B(t.Muted);
        resources["Ui.Faint"] = B(t.Faint);
        resources["Ui.Accent"] = B(t.Accent);
        resources["Ui.OnAccent"] = B(t.OnAccent);
        resources["Ui.Track"] = B(t.Track);
        resources["Ui.Ink"] = B(t.Ink);
        resources["Ui.Glass"] = B(t.Glass);
        resources["Ui.GlassStrong"] = Classic ? B(t.Dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White) : B(Palette.Alpha(t.Dark ? Color.FromRgb(0x1A, 0x1E, 0x28) : Colors.White, t.Dark ? .86 : .9));
        resources["Ui.Inner"] = B(t.Dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
        resources["Ui.InnerHover"] = B(t.Dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0x00, 0x00, 0x00));
        resources["Ui.Selected"] = B(t.Dark ? Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        resources["Ui.Danger"] = B(Palette.Danger);
        resources["Ui.Warning"] = B(Palette.Warning);
        resources["Ui.Live"] = B(C(DesignTokens.Live));
        resources["Ui.Forecast"] = B(C(DesignTokens.Forecast));
        // A specular rim: bright where light enters at the top left, fading through the sides.
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        rim.GradientStops.Add(new GradientStop(Palette.Alpha(t.GlassRim, t.GlassRim.A / 255d), 0));
        rim.GradientStops.Add(new GradientStop(Palette.Alpha(t.GlassRim, t.Dark ? .06 : .35), .45));
        rim.GradientStops.Add(new GradientStop(Palette.Alpha(t.GlassRim, t.Dark ? .16 : .55), 1));
        rim.Freeze();
        resources["Ui.Rim"] = Classic ? B(t.GlassRim) : rim;
        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(t.Dark ? 0x12 : 0x40), 0xFF, 0xFF, 0xFF), 0));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), .5));
        sheen.Freeze();
        resources["Ui.Sheen"] = Classic ? B(Colors.Transparent) : sheen;
        // Prominent keys: the accent, a touch lighter at the top, like a lit glass key.
        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fill.GradientStops.Add(new GradientStop(Palette.Lighten(t.Accent, t.Dark ? .18 : .12), 0));
        fill.GradientStops.Add(new GradientStop(t.Dark ? t.Accent : Palette.Darken(t.Accent, .06), 1));
        fill.Freeze();
        resources["Ui.AccentFill"] = fill;
        if (changed) Changed?.Invoke();
    }

    /// <summary>Calls back when Windows switches between light and dark apps.</summary>
    public static IDisposable WatchSystem(Action changed)
    {
        UserPreferenceChangedEventHandler handler = (_, e) => { if (e.Category == UserPreferenceCategory.General) changed(); };
        SystemEvents.UserPreferenceChanged += handler;
        return new Unsubscribe(() => SystemEvents.UserPreferenceChanged -= handler);
    }

    private sealed class Unsubscribe(Action action) : IDisposable { public void Dispose() => action(); }
}
