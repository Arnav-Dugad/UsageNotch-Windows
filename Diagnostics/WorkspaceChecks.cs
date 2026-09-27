using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UsageNotch;
using UsageNotch.Models;
using UsageNotch.Services;

internal static class WorkspaceChecks
{
    public static void Run()
    {
        var settings = new AppSettings { ReviewSession = true, ReducedMotion = true, GeminiEnabled = false, CursorEnabled = false };
        using var coordinator = new UsageCoordinator(settings);
        var dock = new MainWindow(coordinator, settings);
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetField("_settingsOpen", fields)!.SetValue(dock, true);
        var cell = new Border { DataContext = coordinator.Items[0] };
        typeof(MainWindow).GetMethod("Cell_MouseEnter", fields)!.Invoke(dock, [cell, new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice, 0)]);
        if (!(bool)typeof(MainWindow).GetField("_insideCell", fields)!.GetValue(dock)! ||
            !((DispatcherTimer)typeof(MainWindow).GetField("_hoverTimer", fields)!.GetValue(dock)!).IsEnabled)
            throw new Exception("Settings prevented dock hover expansion.");
        ((DispatcherTimer)typeof(MainWindow).GetField("_hoverTimer", fields)!.GetValue(dock)!).Stop();
        var window = new SettingsWindow(settings, coordinator);
        if (window.ResizeMode != ResizeMode.CanResize || window.AllowsTransparency || window.Owner is not null)
            throw new Exception("Dashboard cannot independently resize/minimize.");
        var dashboard = (UsageNotch.Controls.StatsDashboard)window.FindName("StatsView");
        var cards = (WrapPanel)dashboard.FindName("Cards");
        if (!cards.Children.OfType<UsageNotch.Controls.ProviderStatsCard>().Select(c => c.ProviderId).Contains("claude") ||
            !cards.Children.OfType<UsageNotch.Controls.ProviderStatsCard>().Select(c => c.ProviderId).Contains("codex"))
            throw new Exception("Comparison omitted a provider.");
        var reset = new LimitWindow("test", "5-hour window", .2, DateTimeOffset.UtcNow.AddHours(1));
        if (!reset.ResetLocalText.Contains(reset.ResetsAt!.Value.ToLocalTime().ToString("HH:mm:ss zzz"))) throw new Exception("Exact local reset missing offset or seconds.");
        window.Close(); dock.Close();
        Console.WriteLine("PASS: settings-open dock hover, independent resizable window, both comparison providers and exact local reset timestamps.");
    }
}
