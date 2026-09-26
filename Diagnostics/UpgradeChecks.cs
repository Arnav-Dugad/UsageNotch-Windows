using System.Windows;
using System.Windows.Media;
using UsageNotch;
using UsageNotch.Models;
using UsageNotch.Services;

internal static class UpgradeChecks
{
    public static void Run()
    {
        DragChecks.Run();
        var settings = new AppSettings { ReviewSession = true, DisplayMode = "Dual", ShowRemaining = true };
        var model = new ProviderViewModel(new("claude", "Claude", "", Fidelity.Manual, SnapshotStatus.Ok,
            [new("five_hour", "Session", .73), new("seven_day", "Week", .07)], AccountName: "Sample account"), settings);
        if (Math.Abs(model.RingFraction!.Value - .27) > .0001 || model.PercentText != "27% left" || model.SecondaryLine != "7d 93% left" || model.UsedFraction != .73)
            throw new Exception("Remaining mode changed underlying usage or formatted incorrectly.");
        var converter = new UsageDisplayConverter();
        if ((string)converter.Convert([.73, true], typeof(string), "", System.Globalization.CultureInfo.InvariantCulture) != "27% left") throw new Exception("Detail remaining percentage mismatch.");
        if ((string)converter.Convert([DependencyProperty.UnsetValue, true], typeof(string), "", System.Globalization.CultureInfo.InvariantCulture) != "—") throw new Exception("Unknown usage became a fake remaining percentage.");
        settings.ShowAccountNames = false;
        if (model.AccountLabel != "") throw new Exception("Account privacy toggle failed.");
        settings.ShowAccountNames = true;
        if (model.AccountLabel != "Sample account") throw new Exception("Account label missing.");
        Console.WriteLine("PASS: used/remaining numbers, dual rings, unknown usage, original thresholds and account-name visibility.");

        var transform = new ScaleTransform(.2, .2);
        var firstMotion = Motion.To(transform, ScaleTransform.ScaleXProperty, 1, 2000, false);
        if (firstMotion?.From != .2 || firstMotion.To != 1) throw new Exception("Motion starting value is not preserved.");
        var before = transform.ScaleX;
        var interrupted = Motion.To(transform, ScaleTransform.ScaleXProperty, .7, 2000, false);
        if (interrupted?.From != before || interrupted.To != .7) throw new Exception("Interrupted motion did not capture its current value.");
        var reduced = Motion.To(transform, ScaleTransform.ScaleXProperty, 1, 500, true);
        if ((double)transform.GetAnimationBaseValue(ScaleTransform.ScaleXProperty) != 1 || reduced is not null) throw new Exception("Reduced motion supplied an animation or wrong target.");
        Console.WriteLine("PASS: motion transition starting/target values and reduced-motion behavior (timeline checks, not frame-rate measurements).");
        CheckAlerts();
    }

    private static void CheckAlerts()
    {
        var now = new DateTimeOffset(2026, 9, 8, 23, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 8)));
        var settings = new AppSettings { ReviewSession = true, AlertWarnPercent = 80, AlertCriticalPercent = 95 };
        var alerts = new AlertService(settings, () => now);
        var sent = new List<AlertNotice>();
        alerts.Raised += sent.Add;
        var reset = now.AddHours(5);
        ProviderViewModel Model(double value, SnapshotStatus status = SnapshotStatus.Ok, DateTimeOffset? end = null, string name = "Work") =>
            new(new("claude", "Claude", "", Fidelity.Official, status, [new("five_hour", "Session", value, end ?? reset)], AccountName: name), settings);
        alerts.Evaluate([Model(.2)]);
        alerts.Evaluate([Model(.82, SnapshotStatus.Stale)]);
        if (sent.Count != 0) throw new Exception("Saved readings triggered warnings.");
        alerts.Evaluate([Model(.82)]);
        if (sent.Count != 1) throw new Exception("Live threshold did not trigger.");
        alerts.Evaluate([Model(.82, SnapshotStatus.Error)]);
        alerts.Evaluate([]);
        alerts.Evaluate([Model(.83)]);
        if (sent.Count != 1) throw new Exception("Outage erased deduplication state.");
        alerts.Evaluate([Model(.84, end: reset.AddSeconds(30))]);
        if (sent.Count != 1) throw new Exception("Reset-time jitter caused another warning.");
        settings.QuietHoursEnabled = true;
        alerts.Evaluate([Model(.97)]);
        if (sent.Count != 1 || alerts.History.Count != 2 || !alerts.History[0].Suppressed) throw new Exception("Quiet hours must save alerts without showing them.");
        if (!alerts.IsQuiet(now) || alerts.IsQuiet(now.AddHours(10))) throw new Exception("Overnight quiet-hour range failed.");
        settings.QuietStartHour = 8; settings.QuietEndHour = 17;
        if (alerts.IsQuiet(now) || !alerts.IsQuiet(now.AddHours(10))) throw new Exception("Daytime quiet-hour range failed.");
        settings.QuietHoursEnabled = false;
        alerts.Snooze(TimeSpan.FromHours(1));
        if (!alerts.IsQuiet(now) || alerts.IsQuiet(now.AddHours(2))) throw new Exception("Snooze expiry failed.");
        alerts.Resume();
        if (alerts.IsQuiet(now)) throw new Exception("Resume failed.");
        alerts.Evaluate([Model(.03, end: reset.AddHours(5))]);
        if (sent.Count != 2 || !sent[^1].Title.Contains("reset")) throw new Exception("Genuine reset notification missing.");
        alerts.Evaluate([Model(.99, name: "Personal")]);
        if (sent.Count != 2) throw new Exception("New account must prime silently.");
        var restart = new AlertService(settings, () => now);
        restart.Raised += sent.Add;
        restart.Evaluate([Model(.99, name: "Personal")]);
        restart.Evaluate([Model(.99, name: "Personal")]);
        if (sent.Count != 2) throw new Exception("Restart repeated an existing warning.");
        alerts.Preview();
        if (!sent[^1].IsPreview) throw new Exception("Test alert is not explicitly marked sample data.");
        Console.WriteLine("PASS: alert outage recovery, stale suppression, reset jitter, quiet hours, snooze/resume, resets, account separation, restart deduplication and real preview routing.");
    }
}
