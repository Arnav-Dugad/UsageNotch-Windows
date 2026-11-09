using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageNotch.Models;
using UsageNotch.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace UsageNotch.Controls;

/// <summary>
/// "My week in AI", a 1080×1920 story image laid out like the Android app's: your rings, the week's headline, each
/// provider's week, up to three sentences from the weekly report, and the busiest day, streak and tokens. It holds no
/// account names or e-mail addresses. Drawn directly, so it never captures a half-finished animation.
/// </summary>
public static class ShareWeekCard
{
    public const int Width = 1080, Height = 1920;
    private const double Pad = 84;

    public sealed record Week(ProviderViewModel Provider, string Label, double? Total, DayUsage? Busiest, int Streak);

    public static BitmapSource Render(IReadOnlyList<ProviderViewModel> providers, Func<ProviderViewModel, LimitWindow, WindowSummary?> summaryFor,
        WeekReport? report, IReadOnlyList<TokenReport> tokens, AppSettings settings, bool dark, bool classic)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, providers, summaryFor, report, tokens, settings, dark, classic);
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Saves the image to Pictures\UsageNotch and copies it to the clipboard. Returns the file, or null with the reason.</summary>
    public static (string? File, string Message) SaveAndCopy(BitmapSource image)
    {
        string? file = null;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "UsageNotch");
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, $"UsageNotch-week-{DateTime.Now:yyyy-MM-dd-HHmm}.png");
            using var stream = File.Create(file);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { file = null; }
        var copied = false;
        try { System.Windows.Clipboard.SetImage(image); copied = true; } catch { }
        return (file, (file, copied) switch
        {
            ({ }, true) => "Saved to Pictures › UsageNotch and copied. Paste it anywhere.",
            ({ }, false) => "Saved to Pictures › UsageNotch.",
            (null, true) => "Copied. Paste it anywhere.",
            _ => "Couldn't save or copy the image."
        });
    }

    public static IReadOnlyList<Week> Weeks(IReadOnlyList<ProviderViewModel> providers, Func<ProviderViewModel, LimitWindow, WindowSummary?> summaryFor)
    {
        var today = DateTime.Today; var list = new List<Week>();
        foreach (var p in providers)
        {
            var w = p.SessionWindow is { UsedFraction: not null } s ? s : p.WeeklyWindow is { UsedFraction: not null } wk ? wk : null;
            if (w is null || summaryFor(p, w) is not { } sum) continue;
            var days = sum.Calendar.Where(d => d.Date > today.AddDays(-7) && d.Date <= today).ToList();
            var recorded = days.Where(d => d.Used is not null).ToList();
            list.Add(new Week(p, w.Label, recorded.Count == 0 ? null : recorded.Sum(d => d.Used!.Value), days.Where(d => d.Used is > 0).MaxBy(d => d.Used), sum.Streak));
        }
        return list;
    }

    private static void Draw(DrawingContext dc, IReadOnlyList<ProviderViewModel> providers, Func<ProviderViewModel, LimitWindow, WindowSummary?> summaryFor,
        WeekReport? report, IReadOnlyList<TokenReport> tokens, AppSettings settings, bool dark, bool classic)
    {
        double w = Width, h = Height;
        var text = dark ? Color.FromRgb(0xF4, 0xF6, 0xFA) : Color.FromRgb(0x10, 0x13, 0x18);
        var muted = dark ? Color.FromRgb(0xA9, 0xB1, 0xBF) : Color.FromRgb(0x5B, 0x63, 0x71);
        var card = (classic, dark) switch { (true, true) => Color.FromRgb(0x1C, 0x1C, 0x1E), (true, false) => Colors.White, (false, true) => Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), _ => Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) };
        var surface = dark ? Color.FromRgb(0x15, 0x19, 0x22) : Colors.White;

        // Background: ambient light for Liquid glass, the plain grouped background for Classic.
        if (classic) dc.DrawRectangle(Frozen(dark ? Colors.Black : Color.FromRgb(0xF2, 0xF2, 0xF7)), null, new Rect(0, 0, w, h));
        else
        {
            dc.DrawRectangle(Frozen(new LinearGradientBrush(dark ? Color.FromRgb(0x06, 0x08, 0x0C) : Color.FromRgb(0xF4, 0xF6, 0xFA), dark ? Color.FromRgb(0x0C, 0x0F, 0x16) : Color.FromRgb(0xE6, 0xEA, 0xF2), 90)), null, new Rect(0, 0, w, h));
            var colors = providers.Take(3).Select(p => p.AccentColor).Concat(DesignTokens.Ambient.Select(Accents.FromArgb)).Take(4).ToArray();
            double[] xs = [.15, .85, .3, .75], ys = [.12, .3, .62, .88];
            for (var i = 0; i < colors.Length; i++)
            {
                var glow = new RadialGradientBrush(Color.FromArgb((byte)(dark ? 0x66 : 0x55), colors[i].R, colors[i].G, colors[i].B), Color.FromArgb(0, colors[i].R, colors[i].G, colors[i].B))
                { Center = new Point(.5, .5), GradientOrigin = new Point(.5, .5), RadiusX = .5, RadiusY = .5 };
                var r = w * .75;
                dc.DrawEllipse(Frozen(glow), null, new Point(w * xs[i], h * ys[i]), r, r);
            }
        }

        // Header.
        try
        {
            var mark = new BitmapImage(new Uri("pack://application:,,,/UsageNotch;component/Assets/UsageNotch-logo.png"));
            dc.DrawImage(mark, new Rect(Pad, 120, 92, 92));
        }
        catch { }
        Label(dc, "UsageNotch", Pad + 116, 146, 600, 40, text, FontWeights.SemiBold);
        Label(dc, "My week in AI", Pad, 270, w - Pad * 2, 104, text, FontWeights.Bold);
        var today = DateTime.Today;
        Label(dc, $"{today.AddDays(-6).ToString("MMM d", CultureInfo.InvariantCulture)} – {today.ToString("MMM d", CultureInfo.InvariantCulture)}", Pad, 404, w - Pad * 2, 42, muted);

        // Rings, as on the dock.
        var shown = providers.Where(p => p.SessionWindow is { UsedFraction: not null }).Take(4).ToArray();
        var ring = shown.Length <= 2 ? 300.0 : shown.Length == 3 ? 250 : 210;
        var slot = (w - Pad * 2) / Math.Max(1, shown.Length);
        var track = dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x0B, 0x0D, 0x12);
        double y = 520;
        for (var i = 0; i < shown.Length; i++)
        {
            var p = shown[i]; var cx = Pad + slot * i + slot / 2; var cy = y + ring / 2;
            var used = Math.Clamp(p.SessionWindow!.UsedFraction ?? 0, 0, 1);
            var shownFraction = settings.ShowRemaining ? 1 - used : used;
            Ring(dc, new Point(cx, cy), ring / 2 - ring * .06, ring * .09, shownFraction, track, Palette.Usage(used));
            Logo(dc, p.Id, new Point(cx, cy), ring * .3, text, settings.ColoredLogos);
            Label(dc, $"{Math.Round(shownFraction * 100):0}%" + (settings.ShowRemaining ? " left" : ""), cx - (slot - 16) / 2, cy + ring / 2 + 22, slot - 16, 52, text, FontWeights.SemiBold, TextAlignment.Center);
            Label(dc, p.DisplayName, cx - (slot - 16) / 2, cy + ring / 2 + 84, slot - 16, 34, muted, align: TextAlignment.Center);
        }
        y += shown.Length == 0 ? 0 : ring + 160;

        // The report's headline.
        if (report is not null)
        {
            Label(dc, report.Headline, Pad, y, w - Pad * 2, 50, text, FontWeights.SemiBold);
            y += 86;
        }

        // The week: one row per provider.
        var weeks = Weeks(providers, summaryFor).Take(4).ToArray();
        if (weeks.Length > 0)
        {
            const double rowH = 96; var height = 52 + rowH * weeks.Length;
            Panel(dc, y, height, card);
            for (var i = 0; i < weeks.Length; i++)
            {
                var wk = weeks[i]; var top = y + 30 + rowH * i;
                Logo(dc, wk.Provider.Id, new Point(Pad + 68, top + 46), 56, text, settings.ColoredLogos);
                Label(dc, wk.Provider.DisplayName, Pad + 124, top + 6, 420, 38, text, FontWeights.SemiBold);
                Label(dc, wk.Label, Pad + 124, top + 54, 420, 30, muted);
                Label(dc, Ui.Limits(wk.Total), w - Pad - 340, top + 2, 300, 56, Palette.Readable(wk.Provider.AccentColor, surface), FontWeights.Bold, TextAlignment.Right);
                Label(dc, wk.Total is >= Ui.MultipleFrom ? "limits' worth" : "of one limit", w - Pad - 340, top + 62, 300, 26, muted, align: TextAlignment.Right);
            }
            y += height + 36;
        }

        // Up to three sentences from the report.
        var lines = report?.Insights.Where(i => i.Kind != "tokens").Take(3).ToArray() ?? [];
        if (lines.Length > 0 && y + 120 < h - 160)
        {
            var texts = lines.Select(l => Text(l.Text, 34, text, maxWidth: w - Pad * 2 - 112)).ToArray();
            var height = 56 + texts.Sum(t => t.Height + 22) - 22;
            if (y + height < h - 160)
            {
                Panel(dc, y, height, card);
                var top = y + 28;
                for (var i = 0; i < texts.Length; i++)
                {
                    var accent = providers.FirstOrDefault(p => p.Id == lines[i].Provider)?.AccentColor ?? Accents.FromArgb(DesignTokens.Live);
                    dc.DrawEllipse(Frozen(accent), null, new Point(Pad + 52, top + 22), 7, 7);
                    dc.DrawText(texts[i], new Point(Pad + 76, top));
                    top += texts[i].Height + 22;
                }
                y += height + 36;
            }
        }

        // Tiles: the busiest day, the streak and tokens.
        var busiest = weeks.Where(x => x.Busiest is not null).MaxBy(x => x.Busiest!.Used);
        var streak = weeks.Length == 0 ? 0 : weeks.Max(x => x.Streak);
        var source = tokens.FirstOrDefault(t => t.Week.Total > 0);
        var tiles = new List<(string Label, string Value, string Note)>();
        if (busiest is { Busiest: { } day }) tiles.Add(("Busiest day", day.Date.ToString("dddd", CultureInfo.InvariantCulture), $"{busiest.Provider.DisplayName} · {Ui.Limits(day.Used)} of the limit"));
        tiles.Add(("Streak", streak > 0 ? $"{streak} day{(streak == 1 ? "" : "s")}" : "—", "in a row with usage"));
        if (source is not null) tiles.Add(("Tokens this week", Ui.Tokens(source.Week.Total), source.Week.Cost is { } c ? $"≈ {Ui.Money(c)} at API prices" : source.Label));
        if (source?.Projects.FirstOrDefault() is { } project) tiles.Add(("Top project", project.Name, $"{Math.Round(project.Share * 100):0}% of {source.Label}"));
        var tileW = (w - Pad * 2 - 28) / 2; const double tileH = 200;
        for (var i = 0; i < Math.Min(4, tiles.Count); i++)
        {
            var x = Pad + i % 2 * (tileW + 28); var top = y + i / 2 * (tileH + 28);
            if (top + tileH > h - 150) break;
            dc.DrawRoundedRectangle(Frozen(card), null, new Rect(x, top, tileW, tileH), 40, 40);
            Label(dc, tiles[i].Label, x + 36, top + 30, tileW - 72, 28, muted);
            Label(dc, tiles[i].Value, x + 36, top + 70, tileW - 72, Math.Min(58, 58 * 9.0 / Math.Max(9, tiles[i].Value.Length)), text, FontWeights.Bold);
            Label(dc, tiles[i].Note, x + 36, top + 142, tileW - 72, 26, muted);
        }
        Label(dc, "Made with UsageNotch", Pad, h - 120, w - Pad * 2, 30, muted, align: TextAlignment.Center);
    }

    private static void Panel(DrawingContext dc, double top, double height, Color color) =>
        dc.DrawRoundedRectangle(Frozen(color), null, new Rect(Pad, top, Width - Pad * 2, height), 44, 44);

    private static void Ring(DrawingContext dc, Point center, double radius, double thickness, double fraction, Color track, Color ink)
    {
        dc.DrawEllipse(null, new Pen(Frozen(track), thickness), center, radius, radius);
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction <= .001) return;
        var pen = new Pen(Frozen(ink), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (fraction >= .999) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open()) { ctx.BeginFigure(start, false, false); ctx.ArcTo(end, new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise, true, false); }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private static void Logo(DrawingContext dc, string provider, Point center, double size, Color ink, bool colored) =>
        ProviderLogo.Draw(dc, provider, new Rect(center.X - size / 2, center.Y - size / 2, size, size), Frozen(ink), colored);

    private static FormattedText Text(string value, double size, Color color, FontWeight? weight = null, double maxWidth = 0, TextAlignment align = TextAlignment.Left)
    {
        var t = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(size >= 40 ? Ui.Display : Ui.Body, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, Frozen(color), 1.0)
        { TextAlignment = align };
        if (maxWidth > 0) t.MaxTextWidth = maxWidth;
        return t;
    }

    /// <summary>One line of text, cut short with an ellipsis when it would not fit.</summary>
    private static void Label(DrawingContext dc, string value, double x, double top, double width, double size, Color color, FontWeight? weight = null, TextAlignment align = TextAlignment.Left)
    {
        var t = Text(value, size, color, weight, width, align);
        t.MaxLineCount = 1; t.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(t, new Point(x, top));
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
}
