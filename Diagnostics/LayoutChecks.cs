using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageNotch;
using UsageNotch.Controls;
using UsageNotch.Models;
using UsageNotch.Services;

internal static class LayoutChecks
{
    public static void Run()
    {
        if (System.Windows.Application.Current is null)
        {
            var app = new App { IsReviewSession = true };
            app.InitializeComponent();
        }
        var count = 0;
        foreach (var remaining in new[] { false, true })
        foreach (var sleek in new[] { false, true })
        foreach (var edge in new[] { "Left", "Right", "Top" })
        foreach (var floating in new[] { false, true })
        foreach (var compact in new[] { false, true })
        foreach (var scale in new[] { .75, 1d, 1.5 })
        foreach (var spacing in new[] { 4d, 24d, 48d })
        foreach (var mode in new[] { "Session", "Dual" })
        foreach (var showText in new[] { false, true })
        {
            var settings = new AppSettings { ReviewSession = true, CompactMode = compact, UiScale = scale, ProviderSpacing = spacing,
                DisplayMode = mode, ShowPercentages = showText, ShowProviderNames = showText, FloatingDock = floating, Edge = edge,
                ReducedMotion = true, CursorEnabled = false, ShowRemaining = remaining, SleekMode = sleek };
            using var coordinator = new UsageCoordinator(settings);
            coordinator.Items[0].Snapshot = new("claude", "Claude", "", Fidelity.Manual, SnapshotStatus.Stale,
                [new("five_hour", "5-hour window", remaining ? 0 : .73, DateTimeOffset.UtcNow.AddHours(3)), new("seven_day", "Weekly window", remaining ? 0 : .07, DateTimeOffset.UtcNow.AddDays(3))], "Saved reading");
            coordinator.Items[1].Snapshot = new("codex", "Codex", "", Fidelity.Manual, SnapshotStatus.Ok,
                [new("default-primary", "5-hour window", .21, DateTimeOffset.UtcNow.AddHours(4)), new("default-secondary", "Weekly window", .37, DateTimeOffset.UtcNow.AddDays(2))]);
            coordinator.Items[2].Snapshot = new("gemini", "Gemini", "", Fidelity.Manual, SnapshotStatus.Unsupported, [], "Not available");
            var window = new MainWindow(coordinator, settings);
            var root = (FrameworkElement)window.Content;
            // Match the production lifecycle: Loaded runs after the first template layout.
            root.Measure(new Size(100, 600));
            root.Arrange(new Rect(0, 0, 100, 600));
            root.UpdateLayout();
            typeof(MainWindow).GetMethod("Dock", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false, 430]);
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();
            var rings = Descendants<ProgressRing>(root).ToArray();
            if (rings.Length != 3) throw new Exception("Expected three rendered provider rings.");
            var centres = rings.Select(ring => ring.TransformToAncestor(root).Transform(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2))).ToArray();
            if (edge == "Top")
            {
                if (centres.Any(p => Math.Abs(p.Y - centres[0].Y) > 1.6)) throw new Exception("Top ring baseline alignment.");
                if (Math.Abs((centres[2].X - centres[1].X) - (centres[1].X - centres[0].X)) > 1.6) throw new Exception($"Unequal top column spacing: {floating}/{compact}/{scale}/{spacing}/{mode}/{showText}; centres={string.Join(';', centres)}");
                if (centres[0].X < 16 || centres[^1].X > window.Width - 16) throw new Exception("Clipped top rings.");
            }
            else
            {
                if (centres.Any(p => Math.Abs(p.X - window.Width / 2) > 1.6)) throw new Exception($"Ring horizontal alignment: {edge}/{floating}/{compact}/{scale}/{spacing}/{mode}/{showText}");
                if (Math.Abs((centres[2].Y - centres[1].Y) - (centres[1].Y - centres[0].Y)) > 1.6) throw new Exception("Unequal row spacing.");
                if (centres[0].Y < 16 || centres[^1].Y > window.Height - 16) throw new Exception($"Clipped rings: compact={compact}, scale={scale}, spacing={spacing}, mode={mode}, text={showText}; height={window.Height}; y={string.Join(',', centres.Select(p => p.Y))}");
            }
            var surface = (DockSurface)window.FindName("DockBackground");
            var notch = (Border)window.FindName("Notch");
            if (surface.Outline is null || notch.Clip is null) throw new Exception("Dock hit-target clipping is missing.");
            var bounds = surface.Outline.Bounds;
            if (Math.Abs(bounds.Width - surface.ActualWidth) > 1.1 || Math.Abs(bounds.Height - surface.ActualHeight) > 1.1)
                throw new Exception("Dock outline failed to resize with its surface.");
            if (centres.Any(p => !surface.Outline.FillContains(p))) throw new Exception("Provider ring lies outside dock silhouette.");
            for (var index = 0; index < rings.Length; index++)
            {
                var ring = rings[index];
                var radius = ring.ActualWidth / 2 - 3;
                for (var angle = 0; angle < 360; angle += 30)
                {
                    var point = ring.TransformToAncestor(root).Transform(new Point(ring.ActualWidth / 2 + radius * Math.Cos(angle * Math.PI / 180),
                        ring.ActualHeight / 2 + radius * Math.Sin(angle * Math.PI / 180)));
                    if (!surface.Outline.FillContains(point)) throw new Exception($"Ring perimeter clipped: sleek={sleek} {edge}/{floating}/{compact}/{scale}/{spacing}/{mode}/{showText}; ring={index} point={point} size={window.Width},{window.Height} centre={centres[index]}");
                }
            }
            if (!showText && coordinator.Items.Any(item => item.SecondaryLine.Length > 0)) throw new Exception("Hidden percentages leaked weekly labels.");
            window.Close();
            count++;
        }
        foreach (var surface in new[] { Colors.White, Colors.Black, Color.FromRgb(128, 128, 128), Color.FromRgb(42, 30, 77) })
        foreach (var fraction in new[] { 0d, .21, .6, .73, .95, 1d })
        {
            var readable = Palette.Readable(Palette.Usage(fraction), surface);
            if (Palette.Contrast(readable, surface) < 4.49) throw new Exception("Usage text contrast regression.");
        }
        Console.WriteLine($"PASS: {count} rendered layout combinations (compact/full, scale, spacing, text, dual), mixed states and colour contrast.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
