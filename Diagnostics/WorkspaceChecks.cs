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
        CheckDisplayPreferences();
        Console.WriteLine("PASS: settings-open dock hover, independent resizable window, both comparison providers and exact local reset timestamps.");
    }
    private static void CheckDisplayPreferences()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ShowRemaining\":true,\"DisplayMode\":\"Dual\"}")!;
        var noon = new DateTimeOffset(new DateTime(2030, 1, 1, 12, 0, 30), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2030, 1, 1)));
        var vm = new ProviderViewModel(new("claude", "Claude", "", Fidelity.Manual, SnapshotStatus.Stale,
            [new("five_hour", "5-hour window", .73, noon), new("seven_day", "Weekly window", .34, noon.AddDays(3))], FetchedAt: noon), settings);
        if (settings.ShowDockResetTimes || !settings.ShowExpandedResetTimes || settings.Use24HourTime || vm.DockResetLines != "") throw new Exception("Old settings did not migrate to a clean dock and AM/PM clocks.");
        settings.ShowDockResetTimes = true;
        if (!vm.DockResetLines.Contains("12:00 PM") || vm.DockResetLines.Contains("12:00:30")) throw new Exception("Dock AM/PM preference failed.");
        settings.Use24HourTime = true; settings.ShowClockSeconds = true;
        if (!vm.DockResetLines.Contains("12:00:30")) throw new Exception("24-hour second precision failed.");
        if (TimeDisplay.Clock(noon.AddHours(-12)) != "12:00 AM" || TimeDisplay.Clock(noon, false, true) != "12:00:30 PM" || TimeDisplay.Clock(noon.AddHours(-12), true) != "00:00") throw new Exception("Noon/midnight clock boundary failed.");
        settings.ShowUsageSuffix = false; settings.ShowSecondaryPercentage = false; settings.ShowStatusBadge = false;
        if (vm.PercentText.Contains("left") || vm.SecondaryLine.Length > 0 || vm.ShowSavedBadge) throw new Exception("Independent dock text controls failed.");
        settings.ClaudeDockLabel = "Personal\nworkspace with a very long name";
        if (vm.DockName.Length > 18 || vm.DockName.Contains('\n')) throw new Exception("Custom dock label was not bounded.");
        settings.ShowWindowLabel = true; settings.CompactMode = true;
        if (vm.DockResetLines.Length > 0 || vm.DockWindowLabel.Length > 0 || vm.ShowPercentages || vm.ShowProviderNames) throw new Exception("Compact mode leaked dock text.");
        var saved = settings.Clone(); var clone = new AppSettings(); clone.CopyFrom(saved);
        if (clone.ShowExpandedResetTimes != saved.ShowExpandedResetTimes || clone.ClaudeDockLabel != saved.ClaudeDockLabel || clone.Use24HourTime != saved.Use24HourTime) throw new Exception("Display preferences did not survive clone/restore.");
        Console.WriteLine("PASS: clean-dock migration, independent text controls, custom labels, compact behavior, 12/24-hour noon/midnight/seconds and settings round trips.");
    }
}
