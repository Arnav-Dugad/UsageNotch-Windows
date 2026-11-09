using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using UsageNotch;
using UsageNotch.Controls;
using UsageNotch.Models;
using UsageNotch.Services;
using UsageNotch.Services.Phone;

/// <summary>"This week", all-time totals, forecast ranges, the tray ring, the share image and the v3 phone fixture (Windows 2.8).</summary>
internal static class ReportChecks
{
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); }

    /// <summary>Two weeks of one Claude session a day, 10–11 AM local: 30% a day the week before, 45% this week, and 100% two days ago.</summary>
    public static List<UsagePoint> TwoWeeks(DateTime today)
    {
        var points = new List<UsagePoint>();
        for (var d = 13; d >= 0; d--)
        {
            var day = today.AddDays(-d);
            var used = d >= 7 ? .30 : d == 2 ? 1.0 : .45;
            var start = new DateTimeOffset(day.AddHours(10)); var reset = new DateTimeOffset(day.AddHours(15));
            for (var i = 0; i <= 12; i++) points.Add(new UsagePoint(start.AddMinutes(i * 5), Math.Round(used * i / 12, 4), reset, "p" + d));
        }
        return points;
    }

    public static void Run(string? artifacts = null)
    {
        if (System.Windows.Application.Current is null) { var app = new App { IsReviewSession = true }; app.InitializeComponent(); }
        var today = DateTime.Today; var now = new DateTimeOffset(today.AddHours(23));
        var points = TwoWeeks(today);

        // All-time totals use the same counting as the daily totals.
        var counter = new HistoryInsights.AllTimeCounter();
        foreach (var p in points) counter.Add(p);
        var all = counter.Result()!;
        Check(Math.Abs(all.Used - 5.8) < 1e-6 && all.ActiveDays == 14 && all.RecordedDays == 14 && all.Hits == 1, $"all time: 5.8 limits over 14 active days, 1 limit reached (got {all.Used}, {all.ActiveDays}, {all.Hits})");
        Check(all.BestDay == today.AddDays(-2) && Math.Abs(all.BestUsed - 1) < 1e-6 && all.Since == points[0].At, "all time: the best day and the first reading");
        Check(new HistoryInsights.AllTimeCounter().Result() is null, "no readings, no all-time totals");

        // Real limit periods: segments split by a sleep gap or a 1-minute reset drift are still one weekly limit.
        var weekStart = new DateTimeOffset(today.AddDays(-9).AddHours(9)); var weekReset = weekStart.AddDays(7);
        var segmented = new List<UsagePoint>();
        for (var i = 0; i < 50; i++)
        {
            var at = weekStart.AddHours(i * 4); // 4-hour gaps: every reading starts a new stored segment
            var drift = i % 2 == 0 ? weekReset : weekReset.AddMinutes(-1);
            segmented.Add(at < weekReset ? new UsagePoint(at, Math.Min(1, i * .03), drift, "s" + i) : new UsagePoint(at, (i - 42) * .01, weekReset.AddDays(7), "n" + i));
        }
        var limitPeriods = HistoryInsights.LimitPeriods(segmented);
        Check(limitPeriods.Count == 2 && HistoryInsights.Unused(segmented, now).Count == 1 && Math.Abs(HistoryInsights.Unused(segmented, now)[0].Peak - segmented.Where(p => p.At < weekReset).Max(p => p.Used)) < 1e-9,
            "a week split by gaps and 1-minute reset drifts is one limit period, with one unused figure");
        Check(HistoryInsights.Hits(segmented.Select(p => p with { Used = p.Used >= .5 ? 1 : p.Used }).ToList(), now).Count == 1, "reaching a limit across several stored segments counts once");
        var rolling = Enumerable.Range(0, 30).Select(i => new UsagePoint(weekStart.AddHours(i), 0, weekStart.AddHours(i).AddDays(7), "r" + i)).ToList();
        Check(HistoryInsights.LimitPeriods(rolling).Count == 1 && HistoryInsights.Reliability(rolling, [], now).Moved == 0, "a rolling reset while nothing is used is neither a renewal nor a moved reset");

        // The weekly report.
        var summary = HistoryInsights.Summarize(points, [], now);
        Check(Math.Abs(summary.Total(7)!.Value - 3.7) < 1e-6 && Math.Abs(summary.Total(90)!.Value - 5.8) < 1e-6 && summary.Recorded(90) == 14, "7- and 90-day totals read from the calendar");
        var claude = new ProviderViewModel(new ProviderSnapshot("claude", "Claude", "", Fidelity.Official, SnapshotStatus.Ok,
            [new LimitWindow("five_hour", "Current session", .45, now.AddHours(2))], FetchedAt: now), new AppSettings());
        var report = WeeklyReport.Build([claude], (_, _) => summary, [], true, now, false)!;
        var row = report.Providers.Single();
        Check(Math.Abs(row.Used!.Value - 3.7) < 1e-6 && Math.Abs(row.Previous!.Value - 2.1) < 1e-6 && Math.Abs(row.Change!.Value - 1.6 / 2.1) < 1e-3, "this week 3.7 limits against 2.1 the week before");
        Check(row.ActiveDays == 7 && row.Hits == 1 && row.BusiestDay == today.AddDays(-2) && row.Streak == 14, "active days, limits reached, busiest day and streak");
        Check(report.Headline == "A busier week than the last", "headline: busier");
        Check(report.Summary.EndsWith("· 7 active days") && report.From == today.AddDays(-6) && report.To == today, "summary line and dates");
        var texts = report.Insights.Select(i => i.Text).ToArray();
        foreach (var t in texts) Console.WriteLine("      " + t);
        Check(texts[0] == "You used Claude 76% more than the week before.", "change sentence");
        Check(texts.Contains("You reached the Claude session limit once this week."), "limit reached sentence");
        Check(texts.Contains($"{today.AddDays(-2):dddd} was your busiest day, using a full Claude session limit."), "busiest day sentence");
        Check(texts.Any(t => t.StartsWith($"You're usually busiest on {today.AddDays(-2).DayOfWeek}s around 10")), "busiest hours sentence");
        Check(texts.Contains("14 days in a row with Claude.") && report.Insights.Count <= WeeklyReport.MaxInsights, "streak sentence, at most 7");
        Check(report.Insights.Select(i => i.Kind).SequenceEqual(report.Insights.Select(i => i.Kind).OrderBy(k => Array.IndexOf(new[] { "change", "hits", "busiest", "hours", "unused", "streak", "tokens" }, k))), "insights keep their documented order");

        var sunday = today.AddDays(((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7).AddHours(19);
        var recap = AlertService.Recap([claude], AlertContext.Empty with { Report = report }, new DateTimeOffset(sunday));
        Check(recap is not null && recap.Message.StartsWith("A busier week than the last. You used Claude 76% more") && AlertService.Recap([claude], AlertContext.Empty with { Report = report }, new DateTimeOffset(sunday.AddHours(-2))) is null,
            "the Sunday recap speaks with the report's words, only after 6 PM");

        // A quiet week, a steady week, and a week without a comparison.
        var quiet = points.Select(p => p.At.LocalDateTime.Date >= today.AddDays(-6) ? p with { Used = Math.Round(p.Used / 3, 4) } : p).ToList();
        Check(WeeklyReport.Build([claude], (_, _) => HistoryInsights.Summarize(quiet, [], now), [], true, now, false)!.Headline == "A quieter week than the last", "headline: quieter");
        var recent = points.Where(p => p.At.LocalDateTime.Date >= today.AddDays(-4)).ToList();
        var fresh = WeeklyReport.Build([claude], (_, _) => HistoryInsights.Summarize(recent, [], now), [], true, now, false)!;
        Check(fresh.Headline == "Your first week of history" && fresh.Providers[0].Change is null && fresh.Insights.All(i => i.Kind != "change"), "no comparison without a week before");
        // Weighted headline: a barely used provider that stopped doesn't make a steady week "quieter".
        var codexPoints = TwoWeeks(today).Select(p => p with { Used = p.At.LocalDateTime.Date >= today.AddDays(-6) ? 0 : Math.Round(p.Used / 20, 4) }).ToList();
        var codex = new ProviderViewModel(new ProviderSnapshot("codex", "Codex", "", Fidelity.Official, SnapshotStatus.Ok, [new LimitWindow("codex-primary", "5-hour window", 0, now.AddHours(2))], FetchedAt: now), new AppSettings());
        var steady = points.Select(p => p.At.LocalDateTime.Date >= today.AddDays(-6) ? p with { Used = Math.Round(p.Used * (p.At.LocalDateTime.Date == today.AddDays(-2) ? .3 : .3 / .45), 4) } : p).ToList();
        var mixed = WeeklyReport.Build([claude, codex], (p, _) => HistoryInsights.Summarize(p.Id == "codex" ? codexPoints : steady, [], now), [], true, now, false)!;
        Check(mixed.Headline == "A steady week" && mixed.Insights.Any(i => i.Text == "You didn't use Codex this week."), $"headline weighs providers by use; an unused provider reads plainly ({mixed.Headline})");
        Check(WeeklyReport.Lower("Current session") == "session" && WeeklyReport.Lower("All models") == "weekly" && WeeklyReport.Lower("5-hour window") == "5-hour"
            && WeeklyReport.Lower("GPT-5.3-Codex-Spark · Weekly window") == "GPT-5.3-Codex-Spark Weekly" && WeeklyReport.Lower("Monthly limit") == "monthly", "limit names read naturally in sentences");
        var tokens = new TokenReport("claude", "Claude Code", new TokenTotals(0, 0, 0, 0, null), new TokenTotals(10, 10, 0, 12_400_000, 42.5), [], [new TokenShare("UsageNotch-Windows", 9_000_000, 30, .7), new TokenShare("Other", 3_400_000, 12.5, .3)], [], new long[24]);
        var withTokens = WeeklyReport.Build([claude], (_, _) => summary, [tokens], true, now, false)!;
        var hidden = WeeklyReport.Build([claude], (_, _) => summary, [tokens], false, now, false)!;
        Check(withTokens.Insights.Any(i => i.Kind == "tokens") || withTokens.Insights.Count == WeeklyReport.MaxInsights, "tokens sentence when there's room");
        Check(!JsonSerializer.Serialize(hidden).Contains("UsageNotch-Windows"), "project names stay out of the report when not shared");

        // Forecast ranges.
        var f = new PaceForecast("", "", 15, 80, LowRate: .10, HighRate: .20);
        var t0 = new DateTimeOffset(today.AddHours(12));
        Check(UsageForecast.RangeText(f, .5, t0.AddHours(10), t0, false) == "likely 2:30–5:00 PM", "range: likely 2:30–5:00 PM (" + UsageForecast.RangeText(f, .5, t0.AddHours(10), t0, false) + ")");
        Check(UsageForecast.RangeText(f, .5, t0.AddHours(10), t0, true) == "likely 14:30–17:00", "range in 24-hour time");
        Check(UsageForecast.RangeText(f, .5, t0.AddHours(4), t0, false) == "as early as 2:30 PM", "range: only the early time before the reset");
        Check(UsageForecast.RangeText(f with { LowRate = 0 }, .5, t0.AddHours(10), t0, false) == "as early as 2:30 PM", "range: no slow pace observed");
        Check(UsageForecast.RangeText(f with { LowRate = .199 }, .5, t0.AddHours(10), t0, false) is null, "range: under 10 minutes apart is left out");
        Check(UsageForecast.RangeText(f, .5, t0.AddHours(2), t0, false) is null && UsageForecast.RangeText(f, 1, t0.AddHours(10), t0, false) is null, "range: none when even the fast pace misses the reset, or at the limit");
        Check(UsageForecast.RangeText(f, .5, t0.AddHours(12), new DateTimeOffset(today.AddHours(10)), false) == "likely 12:30–3:00 PM", "range across noon keeps both AM/PM marks right");

        // The tray ring and tooltip.
        using (var icon = TrayRing.Draw(.64, false, true)) Check(icon.Width == TrayRing.Size && icon.Height == TrayRing.Size, "tray ring draws a 32 px icon");
        using (var icon = TrayRing.Draw(1.2, true, false)) Check(icon is not null, "tray ring clamps over-limit readings");
        var many = Enumerable.Range(0, 12).Select(i => new ProviderViewModel(new ProviderSnapshot("claude:a" + i, "Claude · A very long account name " + i, "", Fidelity.Official, SnapshotStatus.Ok, [new LimitWindow("five_hour", "Current session", .5, null)]), new AppSettings())).ToList();
        Check(TrayRing.Tooltip(many, "UsageNotch 2.8.0").Length <= 127 && TrayRing.Tooltip([claude], "UsageNotch 2.8.0") == "UsageNotch 2.8.0\nClaude: 45% used", "tray tooltip lists providers within 127 characters");

        // Every provider mark parses and draws inside its 24-unit box (packed SVG arc flags are written out).
        var strip = new System.Windows.Media.DrawingVisual();
        using (var dc = strip.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 6 * 120, 240));
            var ids = new[] { "claude", "codex", "gemini", "cursor", "openai_api", "anthropic_api" };
            for (var i = 0; i < ids.Length; i++)
            {
                Check(ProviderLogo.Shape(ProviderLogo.KeyFor(ids[i])) is { } g && g.Bounds.Left >= -0.5 && g.Bounds.Top >= -0.5 && g.Bounds.Right <= 24.5 && g.Bounds.Bottom <= 24.5 && g.Bounds.Width > 18, $"{ids[i]} logo fits its box ({ProviderLogo.Shape(ProviderLogo.KeyFor(ids[i]))?.Bounds})");
                ProviderLogo.Draw(dc, ids[i], new Rect(i * 120 + 12, 12, 96, 96), System.Windows.Media.Brushes.Black, true);
                ProviderLogo.Draw(dc, ids[i], new Rect(i * 120 + 12, 132, 96, 96), System.Windows.Media.Brushes.Black, false);
            }
        }
        if (artifacts is not null)
        {
            var bmp = new RenderTargetBitmap(720, 240, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bmp.Render(strip);
            Directory.CreateDirectory(artifacts);
            using var file = File.Create(Path.Combine(artifacts, "logos.png"));
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); enc.Save(file);
        }

        // The share image.
        foreach (var (dark, classic) in new[] { (true, false), (false, false), (true, true) })
        {
            var image = ShareWeekCard.Render([claude], (_, _) => summary, report, [tokens], new AppSettings { ColoredLogos = true }, dark, classic);
            Check(image.PixelWidth == 1080 && image.PixelHeight == 1920, $"share image is 1080×1920 ({(dark ? "dark" : "light")}{(classic ? ", classic" : "")})");
            if (artifacts is not null)
            {
                Directory.CreateDirectory(artifacts);
                using var file = File.Create(Path.Combine(artifacts, $"share-week-{(dark ? "dark" : "light")}{(classic ? "-classic" : "")}.png"));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file);
            }
        }

        // Phone protocol: report and allTime travel in the snapshot (v3 fixture for the Android tests).
        var dir = Path.Combine(Path.GetTempPath(), "UsageNotch-Report-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var history = new UsageHistory(Path.Combine(dir, "history.db"));
            var realNow = DateTimeOffset.UtcNow; var realToday = DateTime.Today;
            var live = TwoWeeks(realToday.AddDays(-1));
            foreach (var p in live) history.Record(new ProviderSnapshot("claude", "Claude", "", Fidelity.Official, SnapshotStatus.Ok,
                [new LimitWindow("five_hour", "Current session", p.Used, p.Reset), new LimitWindow("seven_day", "All models", .3, realNow.AddDays(3))], FetchedAt: p.At, AccountName: "Sample"), p.At);
            var snapshot = new ProviderSnapshot("claude", "Claude", "", Fidelity.Official, SnapshotStatus.Ok,
                [new LimitWindow("five_hour", "Current session", .2, realNow.AddHours(3)), new LimitWindow("seven_day", "All models", .3, realNow.AddDays(3))], FetchedAt: realNow, AccountName: "Sample");
            var view = new ProviderViewModel(snapshot, new AppSettings());
            var liveSummary = (Func<ProviderViewModel, LimitWindow, WindowSummary?>)((p, w) => HistoryInsights.Summarize(history.Read("claude", history.AccountKey(snapshot), w.Id, realNow.AddDays(-91)), [], realNow));
            var liveReport = WeeklyReport.Build([view], liveSummary, [], false, realNow, false);
            AllTimeTotals? AllTime(ProviderSnapshot s, LimitWindow w) { var c = new HistoryInsights.AllTimeCounter(); history.ForEach(s.Id, history.AccountKey(s), w.Id, c.Add); return c.Result(); }
            var extras = new PhoneSnapshot.Extras(true, [], [], false, Report: liveReport, AllTime: AllTime);
            var json = PhoneSnapshot.Serialize(PhoneSnapshot.FromLive([snapshot], new AppSettings { ShowAccountNames = false }, history, null, extras: extras));
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var features = root.GetProperty("features").EnumerateArray().Select(x => x.GetString()).ToArray();
                Check(features.Contains("report") && features.Contains("alltime") && root.GetProperty("schema").GetInt32() == 1, "snapshot lists the report and alltime features, schema stays 1");
                var r = root.GetProperty("report");
                Check(r.GetProperty("headline").GetString() == "A busier week than the last" && r.GetProperty("providers")[0].GetProperty("id").GetString() == "claude"
                    && r.GetProperty("insights").GetArrayLength() >= 3 && r.GetProperty("from").GetString()!.Length == 10, "report travels with headline, rows and sentences");
                var allTime = root.GetProperty("providers")[0].GetProperty("history")[0].GetProperty("allTime");
                Check(Math.Abs(allTime.GetProperty("used").GetDouble() - 5.8) < 1e-6 && allTime.GetProperty("activeDays").GetInt32() == 14 && allTime.GetProperty("hits").GetInt32() == 1
                    && allTime.GetProperty("bestDay").GetString() == realToday.AddDays(-3).ToString("yyyy-MM-dd"), "allTime travels in history[]");
                Check(!json.Contains("Sample"), "no account name in the fixture");
            }
            if (artifacts is not null) File.WriteAllText(Path.Combine(artifacts, "phone-snapshot-v3-windows.json"), json);
            Check(history.Counts().Readings > 0 && history.DeleteAll() && history.Counts() is { Readings: 0, Events: 0, Since: null } && AllTime(snapshot, snapshot.Windows[0]) is null, "Delete history removes every reading and the all-time totals");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }
}
