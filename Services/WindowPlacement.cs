using System.Text.Json;
using System.Windows;

namespace UsageNotch.Services;

public static class WindowPlacement
{
    private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized);
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageNotch", "window.json");
    public static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(PathName)) return;
            var p = JsonSerializer.Deserialize<Placement>(File.ReadAllText(PathName));
            if (p is null || !double.IsFinite(p.Left + p.Top + p.Width + p.Height)) return;
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            window.Width = Math.Clamp(p.Width, window.MinWidth, Math.Max(window.MinWidth, desktop.Width));
            window.Height = Math.Clamp(p.Height, window.MinHeight, Math.Max(window.MinHeight, desktop.Height));
            window.Left = Math.Clamp(p.Left, desktop.Left, Math.Max(desktop.Left, desktop.Right - window.Width));
            window.Top = Math.Clamp(p.Top, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - window.Height));
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (p.Maximized) window.WindowState = WindowState.Maximized;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }
    public static void Save(Window window)
    {
        try
        {
            var r = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
            if (r.IsEmpty) return;
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(new Placement(r.Left, r.Top, r.Width, r.Height, window.WindowState == WindowState.Maximized)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
