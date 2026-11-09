using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageNotch;
using UsageNotch.Controls;
using UsageNotch.Models;
using UsageNotch.Services;

/// <summary>
/// Renders the 2.8 surfaces to PNG for review: History (Liquid glass and Classic, dark and light), the What's new sheet and
/// the welcome. Uses two weeks of sample readings; nothing touches the real history.
/// </summary>
internal static class Shots
{
    public static int Run()
    {
        var app = new App { IsReviewSession = true }; app.InitializeComponent();
        var folder = Path.Combine(Environment.CurrentDirectory, "Diagnostics", "Artifacts", "Shots"); Directory.CreateDirectory(folder);
        var db = Path.Combine(Path.GetTempPath(), "UsageNotch-Shots-" + Guid.NewGuid().ToString("N") + ".db");
        var history = new UsageHistory(db);
        var settings = new AppSettings { ReviewSession = true, ReducedMotion = true, GeminiEnabled = false, CursorEnabled = false, TrackLocalTokens = true };
        var coordinator = new UsageCoordinator(settings, history);
        var now = DateTimeOffset.Now; var today = DateTime.Today;
        foreach (var (id, window, label, scale) in new[] { ("claude", "five_hour", "Current session", 1.0), ("codex", "codex-primary", "5-hour window", .6) })
        {
            ProviderSnapshot Snap(double used, DateTimeOffset? reset, DateTimeOffset at) => new(id, id == "claude" ? "Claude" : "Codex", "", Fidelity.Official, SnapshotStatus.Ok,
                [new LimitWindow(window, label, used, reset), new LimitWindow(id == "claude" ? "seven_day" : "codex-secondary", id == "claude" ? "All models" : "Weekly window", .42, now.AddDays(3))],
                FetchedAt: at, AccountName: "Sample", Plan: id == "claude" ? "Max 5×" : "Plus");
            // Ten weeks of one session a day (10–11 AM), busier on Tuesdays, with one unusual day and a plan change.
            for (var d = 70; d >= 1; d--)
            {
                var day = today.AddDays(-d);
                var used = (day.DayOfWeek == DayOfWeek.Tuesday ? .6 : .4) * (d <= 7 ? 1.15 : 1) * scale;
                if (d == 3) used = Math.Min(1, used * 2.3);
                var start = new DateTimeOffset(day.AddHours(10)); var reset = new DateTimeOffset(day.AddHours(15));
                for (var i = 0; i <= 12; i++)
                {
                    var at = start.AddMinutes(i * 5);
                    var snap = Snap(Math.Round(used * i / 12, 4), reset, at);
                    if (id == "claude" && d > 40) snap = snap with { Plan = "Pro" };
                    history.Record(snap, at);
                }
            }
            // The last three hours: a steady climb, so the pace line and its range show.
            for (var i = 0; i <= 36; i++) { var at = now.AddMinutes(-180 + i * 5); history.Record(Snap(.05 + i * .01 * scale, now.AddHours(2.5), at), at); }
            coordinator.Items.First(x => x.Id == id).Snapshot = Snap(.05 + .36 * scale, now.AddHours(2.5), now);
        }

        history.SetNote(today.AddDays(-2), "Big refactor of the sync code", DateTimeOffset.Now);
        history.SetNote(today.AddDays(-5), "Exam week", DateTimeOffset.Now);
        var shots = new List<(string Name, Func<Window> Make)>();
        foreach (var dark in new[] { true, false })
            shots.Add(($"overview-{(dark ? "dark" : "light")}", () =>
            {
                settings.AppTheme = dark ? "Dark" : "Light"; settings.ClassicStyle = false;
                var w = new Window { Width = 1240, Height = 1000, Left = -30000, Top = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false };
                Theme.Apply(w.Resources, settings);
                var view = new StatsDashboard(); view.Connect(coordinator);
                var root = new System.Windows.Controls.Grid(); root.Children.Add(new AmbientBackground { Animate = false, Colors = coordinator.Items.Take(3).Select(p => p.AccentColor).ToArray() }); root.Children.Add(view);
                w.Content = root;
                return w;
            }));
        foreach (var (dark, classic) in new[] { (true, false), (false, false), (true, true), (false, true) })
            shots.Add(($"history-{(dark ? "dark" : "light")}{(classic ? "-classic" : "")}", () =>
            {
                settings.AppTheme = dark ? "Dark" : "Light"; settings.ClassicStyle = classic;
                var w = new Window { Width = 1240, Height = 3200, Left = -30000, Top = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false };
                Theme.Apply(w.Resources, settings);
                var view = new HistoryView(); view.Connect(coordinator);
                var root = new System.Windows.Controls.Grid(); root.Children.Add(new AmbientBackground { Animate = false, Colors = coordinator.Items.Take(3).Select(p => p.AccentColor).ToArray() }); root.Children.Add(view);
                w.Content = root;
                return w;
            }));
        foreach (var first in new[] { false, true })
            shots.Add((first ? "welcome" : "whats-new", () =>
            {
                settings.AppTheme = "Dark"; settings.ClassicStyle = false;
                var w = new WhatsNewWindow(settings, first, null, null) { Left = -30000, Top = 0, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                return w;
            }));

        var index = 0; Window? current = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        void Next()
        {
            if (current is not null)
            {
                // The whole scrolling page at 1:1, not just the part a screen-sized window shows.
                var content = (FrameworkElement)current.Content;
                if (content is System.Windows.Controls.Grid { Children.Count: 2 } g && g.Children[1] is HistoryView hv && hv.Children[0] is System.Windows.Controls.ScrollViewer { Content: FrameworkElement page }) content = page;
                if (content is System.Windows.Controls.Grid { Children.Count: 2 } g2 && g2.Children[1] is StatsDashboard sd && sd.Content is System.Windows.Controls.Grid { Children.Count: > 0 } dashHost && dashHost.Children[0] is System.Windows.Controls.ScrollViewer { Content: FrameworkElement page2 }) content = page2;
                var width = Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right); var height = Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                var host = new DrawingVisual();
                using (var dc = host.RenderOpen())
                {
                    dc.DrawRectangle(new LinearGradientBrush(Theme.Current.AmbientTop, Theme.Current.AmbientBottom, 90), null, new Rect(0, 0, width, height));
                    dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(-content.Margin.Left, -content.Margin.Top, width, height) },
                        null, new Rect(0, 0, width, height));
                }
                bitmap.Render(host);
                using var file = File.Create(Path.Combine(folder, shots[index - 1].Name + ".png"));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                Console.WriteLine("Wrote " + shots[index - 1].Name + ".png");
                current.Close();
            }
            if (index >= shots.Count) { timer.Stop(); coordinator.Dispose(); app.Shutdown(); return; }
            current = shots[index++].Make(); current.Show();
        }
        timer.Tick += (_, _) => Next();
        app.Startup += (_, _) => { Next(); timer.Start(); };
        var code = app.Run();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(db); } catch { }
        return code;
    }
}
