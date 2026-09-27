using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace UsageNotch.Services;

public static class WindowMaterials
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static bool Apply(Window window, bool transient)
    {
        // Layered WPF popups keep their solid fallback; system backdrops need a normal HWND.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast || window.AllowsTransparency) return false;
        try
        {
            var handle = new WindowInteropHelper(window).Handle; int dark = 1, backdrop = transient ? 3 : 2;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            if (DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) != 0) return false;
            window.Background = System.Windows.Media.Brushes.Transparent;
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            System.Windows.Shell.WindowChrome.SetWindowChrome(window, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.NoResize ? 0 : 7), GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = false });
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
