using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using UsageNotch.Models;

namespace UsageNotch.Services;

/// <summary>
/// The tray icon as a live ring: your first provider's main limit, in the usage colours, with the percentage inside.
/// The tooltip lists every provider. Drawn at 32 px so it stays sharp on high-DPI taskbars.
/// </summary>
public static class TrayRing
{
    public const int Size = 32;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>The provider the ring shows: the first in dock order with a known main limit.</summary>
    public static ProviderViewModel? Primary(IEnumerable<ProviderViewModel> items) =>
        items.FirstOrDefault(i => i.Status is SnapshotStatus.Ok or SnapshotStatus.Stale && i.UsedFraction is not null);

    /// <summary>A new icon; the caller owns and disposes it.</summary>
    public static Icon Draw(double used, bool remaining, bool dark, double pulse = 0)
    {
        used = Math.Clamp(used, 0, 1);
        var shown = remaining ? 1 - used : used;
        var ink = Palette.Usage(used);
        using var bitmap = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            const float stroke = 4.5f; var r = new RectangleF(stroke / 2 + .5f, stroke / 2 + .5f, Size - stroke - 1, Size - stroke - 1);
            // A glow behind the ring while it pulses (crossing your warning level): rises and falls once per pulse.
            if (pulse > 0)
            {
                var alpha = (int)Math.Round(170 * Math.Sin(Math.Clamp(pulse, 0, 1) * Math.PI));
                using var glow = new SolidBrush(Color.FromArgb(alpha, ink.R, ink.G, ink.B));
                g.FillEllipse(glow, 0, 0, Size, Size);
            }
            using (var disc = new SolidBrush(dark ? Color.FromArgb(235, 16, 18, 24) : Color.FromArgb(240, 250, 251, 253))) g.FillEllipse(disc, r);
            using (var track = new Pen(dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(45, 0, 0, 0), stroke)) g.DrawEllipse(track, r);
            if (shown > .004)
            {
                using var arc = new Pen(Color.FromArgb(255, ink.R, ink.G, ink.B), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (shown >= .996) g.DrawEllipse(arc, r); else g.DrawArc(arc, r, -90, (float)(shown * 360));
            }
            var label = $"{Math.Round(shown * 100):0}";
            using var font = new Font("Segoe UI", label.Length >= 3 ? 9.5f : 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(dark ? Color.White : Color.FromArgb(18, 21, 27));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, text, new RectangleF(0, .5f, Size, Size), format);
        }
        var handle = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }

    /// <summary>Whether the taskbar is dark (Settings → Personalisation → Colours → Windows mode).</summary>
    public static bool TaskbarDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int light || light == 0;
        }
        catch { return true; }
    }

    /// <summary>The tray tooltip: one line per provider, within Windows' 127-character limit.</summary>
    public static string Tooltip(IEnumerable<ProviderViewModel> items, string title)
    {
        var lines = new List<string> { title };
        foreach (var i in items.Where(i => i.UsedFraction is not null))
            lines.Add($"{i.DisplayName}: {Math.Round(Math.Clamp(i.UsedFraction!.Value, 0, 1) * 100):0}% used");
        var text = string.Join("\n", lines);
        return text.Length <= 127 ? text : text[..126] + "…";
    }
}
