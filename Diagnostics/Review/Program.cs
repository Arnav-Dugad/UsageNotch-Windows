using System.Windows.Threading;
using UsageNotch;
using UsageNotch.Models;
using UsageNotch.Services;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new App { IsReviewSession = true };
        app.InitializeComponent();
        var settings = new AppSettings { ReviewSession = true, FloatingDock = true, DockLeft = 650, DockTop = 120,
            DisplayMode = "Dual", CursorEnabled = false };
        using var coordinator = new UsageCoordinator(settings);
        foreach (var item in coordinator.Items)
            item.Snapshot = new ProviderSnapshot(item.Id, item.DisplayName, "", Fidelity.Manual, SnapshotStatus.Ok,
                [new LimitWindow(item.Id == "claude" ? "five_hour" : "default-primary", "Current session", .73, DateTimeOffset.Now.AddMinutes(51)),
                 new LimitWindow(item.Id == "claude" ? "seven_day" : "default-secondary", "All models", .07, DateTimeOffset.Now.AddDays(4))],
                "UI review · sample data · no account requests", FetchedAt: DateTimeOffset.Now);
        var window = new MainWindow(coordinator, settings) { ShowInTaskbar = true, Title = "UsageNotch UI Review — sample data" };
        app.MainWindow = window;
        window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        window.Show();
        Dispatcher.Run();
    }
}
