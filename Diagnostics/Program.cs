using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageNotch;
using UsageNotch.Models;
using UsageNotch.Providers;
using UsageNotch.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--gemini-client-check"))
        {
            var client = GeminiOAuthClient.Load();
            Console.WriteLine("PASS: installed Gemini CLI OAuth configuration resolved without logging or embedding client values.");
            return 0;
        }
        if (args.Length == 4 && args[0] == "--verify-release")
        {
            var manifest = UpdateService.VerifyManifest(File.ReadAllBytes(args[1]), File.ReadAllText(args[2]));
            UpdateService.VerifyFile(args[3], manifest);
            Console.WriteLine("PASS: production public key and signed release executable " + manifest.Version);
            return 0;
        }
        if (args.Contains("--phone-check")) return PhoneLinkChecks.Run(args.SkipWhile(arg => arg != "--fixtures").Skip(1).FirstOrDefault());
        if (args.Contains("--history-check")) { HistoryUpdateChecks.Run(); return 0; }
        if (args.Contains("--tokens-scan"))
        {
            // Reads this PC's real transcripts into a throwaway database and prints the reports (counts only).
            var db = Path.Combine(Path.GetTempPath(), "UsageNotch-tokens-" + Guid.NewGuid().ToString("N") + ".db");
            var tokens = new LocalTokenUsage(db); var clock = System.Diagnostics.Stopwatch.StartNew();
            tokens.ScanAsync(new AppSettings()).GetAwaiter().GetResult();
            Console.WriteLine($"First scan: {clock.Elapsed.TotalSeconds:0.0} s, {tokens.DatabaseBytes() / 1024} KB");
            clock.Restart(); tokens.ScanAsync(new AppSettings()).GetAwaiter().GetResult();
            Console.WriteLine($"Second scan: {clock.Elapsed.TotalMilliseconds:0} ms");
            foreach (var r in tokens.Reports(new AppSettings(), DateTimeOffset.Now))
                Console.WriteLine($"{r.Label}: today {r.Today.Total:N0} (${r.Today.Cost:0.00}), week {r.Week.Total:N0} (${r.Week.Cost:0.00}); top projects {string.Join(", ", r.Projects.Take(3).Select(x => $"{x.Name} {x.Share:P0}"))}; models {string.Join(", ", r.Models.Take(3).Select(x => x.Name))}");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(db); File.Delete(db + "-wal"); File.Delete(db + "-shm"); } catch { }
            return 0;
        }
        if (args.Contains("--serve-sample"))
        {
            // A real phone link with sample readings on 127.0.0.1 (the Android emulator reaches it as 10.0.2.2), for end-to-end tests.
            var dir = Path.Combine(Path.GetTempPath(), "UsageNotch-Serve-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var sampleSettings = new AppSettings { ShowAccountNames = true, ReviewSession = true };
            var sampleHistory = new UsageHistory(Path.Combine(dir, "history.db"));
            var start = DateTimeOffset.UtcNow.AddHours(-3); var reset = DateTimeOffset.UtcNow.AddHours(2);
            // Two earlier weeks of Claude sessions, so "This week" and all-time totals have something to say.
            foreach (var p in ReportChecks.TwoWeeks(DateTime.Today.AddDays(-1)))
                sampleHistory.Record(new ProviderSnapshot("claude", "Claude", "", Fidelity.Official, SnapshotStatus.Ok, [new("five_hour", "Current session", p.Used, p.Reset), new("seven_day", "All models", .2, reset.AddDays(4))], FetchedAt: p.At, AccountName: "Sample"), p.At);
            List<ProviderSnapshot> live = [];
            for (var i = 0; i <= 36; i++)
            {
                var at = start.AddMinutes(i * 5);
                live = [new ProviderSnapshot("claude", "Claude", "", Fidelity.Official, SnapshotStatus.Ok, [new("five_hour", "Current session", .1 + i * .015, reset), new("seven_day", "All models", .2 + i * .002, reset.AddDays(4))], FetchedAt: at, AccountName: "Sample"),
                        new ProviderSnapshot("codex", "Codex", "", Fidelity.Official, SnapshotStatus.Ok, [new("codex-primary", "5-hour limit", .05 + i * .01, reset.AddHours(1)), new("codex-secondary", "Weekly limit", .3, reset.AddDays(3))], FetchedAt: at, AccountName: "Sample")];
                foreach (var snap in live) sampleHistory.Record(snap, at);
            }
            var link = new UsageNotch.Services.Phone.PhoneLinkService(sampleSettings, sampleHistory, Path.Combine(dir, "link"), Path.Combine(dir, "history.db"));
            link.RefreshRequested = () => { Console.WriteLine($"{DateTime.Now:T} POST /v1/refresh"); return Task.FromResult("done"); };
            link.SettingsReceived = shared =>
            {
                Console.WriteLine($"{DateTime.Now:T} PUT /v1/settings {shared.ToJsonString()}");
                SharedSettings.Capture(sampleSettings, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                SharedSettings.Merge(sampleSettings, shared, DateTimeOffset.UtcNow); SharedSettings.Apply(sampleSettings);
                Console.WriteLine($"{DateTime.Now:T} budgets now: {string.Join(", ", sampleSettings.Budgets.Select(b => $"{b.Provider}|{b.Window} {b.Limit}%"))}");
                return SharedSettings.ToJson(sampleSettings);
            };
            link.Update(live);
            link.ExtrasProvider = () =>
            {
                var now = DateTimeOffset.UtcNow;
                WindowSummary? Summary(ProviderViewModel p, LimitWindow w) { lock (sampleHistory) return HistoryInsights.Summarize(sampleHistory.Read(p.Id, sampleHistory.AccountKey(p.Snapshot), w.Id, now.AddDays(-91)), [], now); }
                AllTimeTotals? AllTime(ProviderSnapshot s, LimitWindow w) { var c = new HistoryInsights.AllTimeCounter(); lock (sampleHistory) sampleHistory.ForEach(s.Id, sampleHistory.AccountKey(s), w.Id, c.Add); return c.Result(); }
                var report = WeeklyReport.Build(live.Select(x => new ProviderViewModel(x, sampleSettings)).ToArray(), Summary, [], true, now, false);
                return new UsageNotch.Services.Phone.PhoneSnapshot.Extras(false, [], [], true, Report: report, AllTime: AllTime);
            };
            if (!link.StartAsync(loopbackOnly: true).GetAwaiter().GetResult()) { Console.WriteLine("Couldn't start: " + link.Status); return 1; }
            var code = link.EnsureIdentity().CompactCode("10.0.2.2");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "usagenotch-e2e-code.txt"), code);
            Console.WriteLine("Serving on 127.0.0.1:" + UsageNotch.Services.Phone.PhoneIdentity.Port + "; pairing code written. " + code);
            var stop = new ManualResetEventSlim(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            stop.Wait(TimeSpan.FromMinutes(int.TryParse(Environment.GetEnvironmentVariable("SERVE_MINUTES"), out var minutes) ? minutes : 20));
            link.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--codex-raw"))
        {
            // Prints the Codex app-server's raw rate-limit and account replies, with e-mail addresses masked.
            var exe = CodexSubscriptionProvider.FindCodex() ?? throw new Exception("Codex not found");
            var home = args.SkipWhile(a => a != "--codex-raw").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
            using var process = System.Diagnostics.Process.Start(CodexSubscriptionProvider.CreateStartInfo(exe, home))!;
            async Task<string?> Ask(int id, string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }));
                await process.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (!timeout.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    if (line is null) return null;
                    if (line.Contains($"\"id\":{id}")) return line;
                }
                return null;
            }
            Ask(1, "initialize", new { clientInfo = new { name = "usage-notch", title = "UsageNotch", version = "2.7.0" } }).GetAwaiter().GetResult();
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { method = "initialized", @params = new { } })); process.StandardInput.Flush();
            string Mask(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? "(none)", "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+", "<email>");
            Console.WriteLine("RATE LIMITS: " + Mask(Ask(2, "account/rateLimits/read", new { }).GetAwaiter().GetResult()));
            Console.WriteLine("ACCOUNT: " + Mask(Ask(3, "account/read", new { refreshToken = false }).GetAwaiter().GetResult()));
            Console.WriteLine("USAGE: " + Mask(Ask(4, "account/usage/read", new { }).GetAwaiter().GetResult()));
            try { process.Kill(true); } catch { }
            return 0;
        }
        if (args.Contains("--status-check"))
        {
            using var statusHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var pages = new StatusPages(statusHttp);
            pages.RefreshAsync(new AppSettings(), ["claude", "codex", "cursor"], force: true).GetAwaiter().GetResult();
            foreach (var id in new[] { "claude", "codex", "cursor" })
                Console.WriteLine($"{id}: {(pages.For(id) is { } h ? $"{h.Indicator} · {h.Description} · {h.Incidents.Count} open" : "unavailable")}");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--verify-history-copy")
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = args[2], Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using (var attach = connection.CreateCommand())
            { attach.CommandText = "ATTACH DATABASE $path AS previous"; attach.Parameters.AddWithValue("$path", args[1]); attach.ExecuteNonQuery(); }
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM (SELECT provider,account,window,at,used,reset,period FROM previous.observations EXCEPT SELECT provider,account,window,at,used,reset,period FROM main.observations)";
            if (Convert.ToInt64(check.ExecuteScalar()) != 0) throw new Exception("A previous observation is missing or changed.");
            check.CommandText = "SELECT COUNT(*) FROM previous.observations";
            Console.WriteLine($"PASS: all {check.ExecuteScalar()} pre-update observations preserved exactly."); return 0;
        }
        if (args.Contains("--update-check"))
        {
            using var updates = new UpdateService(); updates.CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine(updates.Status); return updates.Status.Contains("up to date") || updates.Ready ? 0 : 1;
        }
        if (args.Contains("--workspace-preview")) return WorkspacePreview();
        if (args.Contains("--shots")) return Shots.Run();
        if (args.Contains("--report-real")) return RealReport.Run();
        if (args.Contains("--preview")) return Preview();
        if (args.Contains("--connect-gemini")) { GeminiSignIn.Launch(); return 0; }
        if (args.Contains("--check") || args.Contains("--upgrade-check"))
        {
            try { if (args.Contains("--upgrade-check")) { UpgradeChecks.Run(); return 0; } return Check(); }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }
        if (args.Contains("--gemini-discovery")) return DiscoverGemini();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        IUsageProvider provider = args.Contains("--claude") ? new ClaudeSubscriptionProvider(http) : args.Contains("--gemini") ? new GeminiSubscriptionProvider(http) : new CodexSubscriptionProvider();
        var snapshot = provider.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"Status: {snapshot.Status}");
        Console.WriteLine("Account label available: " + !string.IsNullOrWhiteSpace(snapshot.AccountName));
        if (!string.IsNullOrWhiteSpace(snapshot.StatusText)) Console.WriteLine(snapshot.StatusText);
        foreach (var window in snapshot.Windows)
            Console.WriteLine($"{window.Label}: {window.UsedFraction:P0}, reset {window.ResetsAt?.ToLocalTime():g}");
        return snapshot.Status == SnapshotStatus.Ok ? 0 : 1;
    }

    private static int Check()
    {
        HistoryUpdateChecks.Run();
        PlanningChecks.Run();
        LayoutChecks.Run();
        ReportChecks.Run(Path.Combine(Environment.CurrentDirectory, "Diagnostics", "Artifacts", "Report"));
        WorkspaceChecks.Run();
        UpgradeChecks.Run();
        CheckClaudeCooldown();
        ClaudeReliabilityChecks.Run();
        var parse = typeof(GeminiSubscriptionProvider).GetMethod("ParseQuota", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var document = JsonDocument.Parse("""
            {"buckets":[{"modelId":"gemini-3-pro","remainingFraction":1},{"modelId":"gemini-3-flash","remainingFraction":0,"remainingAmount":"0"}]}
            """);
        var windows = (List<LimitWindow>)parse.Invoke(null, [document.RootElement])!;
        if (windows.Count != 2 || windows[0].UsedFraction != 0 || windows[1].UsedFraction != 1)
            throw new Exception("Gemini quota boundary parsing failed.");
        using var unsupported = JsonDocument.Parse("""{"ineligibleTiers":[{"reasonCode":"UNSUPPORTED_CLIENT"}]}""");
        var eligibility = typeof(GeminiSubscriptionProvider).GetMethod("IsUnsupportedClient", BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!(bool)eligibility.Invoke(null, [unsupported.RootElement])!)
            throw new Exception("Unsupported-client detection failed.");
        var settings = new AppSettings { DisplayMode = "Dual" };
        var sample = Sample("claude", "Claude", 0.73, 0.07);
        var model = new ProviderViewModel(sample, settings);
        if (!model.Dual || model.UsedFraction != 0.73 || model.SecondaryFraction != 0.07 || model.SecondaryLine != "7d 7%")
            throw new Exception("Dual-window mapping failed.");
        settings.DisplayMode = "Weekly";
        if (model.UsedFraction != 0.07) throw new Exception("Weekly-window selection failed.");
        settings.DisplayMode = "Highest";
        if (model.UsedFraction != 0.73) throw new Exception("Highest-window selection failed.");
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        if (roundTrip.DisplayMode != settings.DisplayMode) throw new Exception("Settings round trip failed.");
        Console.WriteLine("PASS: Gemini full/exhausted quota, unsupported-client detection, dual/session/weekly mappings, settings round trip.");
        settings.DockColor = "#EADEFF";
        settings.FloatingDock = true;
        settings.DockLeft = 312;
        settings.DockTop = 128;
        roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        if (!roundTrip.FloatingDock || roundTrip.DockLeft != 312 || roundTrip.DockTop != 128 || roundTrip.DockColor != "#EADEFF") throw new Exception("Dock persistence failed.");
        var resources = new ResourceDictionary();
        DockTheme.Apply(resources, "#FFFFFF");
        if (Palette.Luminance(((SolidColorBrush)resources["DockText"]).Color) > 0.1) throw new Exception("White theme contrast failed.");
        DockTheme.Apply(resources, "invalid");
        if (Palette.Luminance(((SolidColorBrush)resources["DockText"]).Color) < 0.7) throw new Exception("Color fallback failed.");
        Console.WriteLine("PASS: dock coordinates, custom color persistence, white theme contrast, invalid color fallback.");

        CheckAlerts();
        CheckHotkeys();
        CheckMorph();
        CheckLiveSettings();
        return 0;
    }

    /// <summary>Every control must edit the running dock immediately, and Cancel must put it all back.</summary>
    private static void CheckLiveSettings()
    {
        if (System.Windows.Application.Current is null)
        {
            var host = new App { IsReviewSession = true };
            host.InitializeComponent();
        }

        var settings = new AppSettings { ReviewSession = true };
        var snapshot = settings.Clone();
        var window = new SettingsWindow(settings);
        var changes = 0;
        window.SettingsChanged += () => changes++;

        ((System.Windows.Controls.CheckBox)window.FindName("CompactBox")!).IsChecked = true;
        if (changes == 0 || !settings.CompactMode) throw new Exception("A toggle did not reach the dock live.");

        ((System.Windows.Controls.Slider)window.FindName("SpacingSlider")!).Value = 40;
        if (Math.Abs(settings.ProviderSpacing - 40) > 0.01) throw new Exception("A slider did not reach the dock live.");

        ((System.Windows.Controls.RadioButton)window.FindName("ModeDual")!).IsChecked = true;
        if (settings.DisplayMode != "Dual") throw new Exception("A mode change did not reach the dock live.");

        ((System.Windows.Controls.TextBox)window.FindName("OpenAiBudget")!).Text = "25";
        if (settings.OpenAiMonthlyBudgetUsd is not 25) throw new Exception("A text field did not reach the dock live.");
        ((System.Windows.Controls.CheckBox)window.FindName("DockResetBox")!).IsChecked = true;
        ((System.Windows.Controls.CheckBox)window.FindName("ExpandedResetBox")!).IsChecked = false;
        ((System.Windows.Controls.RadioButton)window.FindName("Clock24")!).IsChecked = true;
        ((System.Windows.Controls.TextBox)window.FindName("ClaudeLabelField")!).Text = "Personal";
        if (!settings.ShowDockResetTimes || settings.ShowExpandedResetTimes || !settings.Use24HourTime || settings.ClaudeDockLabel != "Personal") throw new Exception("New display controls were not applied live.");

        settings.FloatingDock = true;
        ((System.Windows.Controls.RadioButton)window.FindName("EdgeTop")!).IsChecked = true;
        if (settings.Edge != "Top" || settings.FloatingDock) throw new Exception("Top edge selection failed to snap.");
        ((System.Windows.Controls.Slider)window.FindName("PositionSlider")!).Value = .25;
        if (settings.HorizontalPosition != .25 || settings.VerticalPosition != snapshot.VerticalPosition) throw new Exception("Top position overwrote side position.");
        ((System.Windows.Controls.RadioButton)window.FindName("EdgeLeft")!).IsChecked = true;
        if (settings.Edge != "Left" || settings.HorizontalPosition != .25) throw new Exception("Side switch lost top position.");
        ((System.Windows.Controls.RadioButton)window.FindName("EdgeTop")!).IsChecked = true;
        if (((System.Windows.Controls.Slider)window.FindName("PositionSlider")!).Value != .25) throw new Exception("Top position failed to restore.");

        if (window.Committed) throw new Exception("Live edits must not count as a save.");
        if (changes < 4) throw new Exception($"Expected one live update per edit, saw {changes}.");

        settings.CopyFrom(snapshot);
        var defaults = new AppSettings();
        if (settings.CompactMode || settings.DisplayMode != defaults.DisplayMode
            || Math.Abs(settings.ProviderSpacing - defaults.ProviderSpacing) > 0.01
            || settings.OpenAiMonthlyBudgetUsd is not null || settings.ShowDockResetTimes || !settings.ShowExpandedResetTimes || settings.Use24HourTime || settings.ClaudeDockLabel.Length > 0)
            throw new Exception("Cancelling did not restore the snapshot.");
        if (!settings.ReviewSession) throw new Exception("Snapshot restore must not clear the review flag.");

        Console.WriteLine("PASS: every settings control edits the dock live, and cancelling restores the snapshot exactly.");
    }

    /// <summary>The capsule/teardrop outline must stay finite and inside the control at every blend.</summary>
    private static void CheckMorph()
    {
        var build = typeof(UsageNotch.Controls.DockSurface).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var size in new[] { (100.0, 470.0), (76.0, 190.0), (150.0, 1000.0) })
        {
            Rect? previous = null;
            for (var step = 0; step <= 10; step++)
            {
                var t = step / 10.0;
                var geometry = (Geometry?)build.Invoke(null, [size.Item1, size.Item2, t]);
                if (geometry is null) throw new Exception($"Dock outline missing at {size} t={t}.");
                var bounds = geometry.Bounds;
                if (double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height) || bounds.IsEmpty)
                    throw new Exception($"Dock outline degenerate at {size} t={t}.");
                if (bounds.Left < -0.5 || bounds.Top < -0.5 || bounds.Right > size.Item1 + 0.5 || bounds.Bottom > size.Item2 + 0.5)
                    throw new Exception($"Dock outline escapes its bounds at {size} t={t}: {bounds}.");
                if (previous is { } last && Math.Abs(bounds.Height - last.Height) > size.Item2 * 0.35)
                    throw new Exception($"Dock outline jumps between blend steps at {size} t={t}.");
                previous = bounds;
            }
        }
        Console.WriteLine("PASS: dock outline stays finite, in-bounds and continuous across the capsule-to-teardrop blend.");
    }

    private static void CheckHotkeys()
    {
        if (!HotkeyService.TryParse("Ctrl+Alt+U", out var modifiers, out var key) || modifiers != 3 || key == 0)
            throw new Exception("Hotkey chord parsing failed.");
        if (HotkeyService.TryParse("U", out _, out _)) throw new Exception("Bare key must be rejected as a global hotkey.");
        if (HotkeyService.TryParse("Ctrl+Shift+NotAKey", out _, out _)) throw new Exception("Unknown key must be rejected.");
        if (HotkeyService.Describe(System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.D3) != "Ctrl+Alt+3")
            throw new Exception("Hotkey description failed.");
        Console.WriteLine("PASS: hotkey chords parse, reject modifier-less and unknown keys, and round trip.");
    }

    private static void CheckAlerts()
    {
        var settings = new AppSettings { AlertWarnPercent = 80, AlertCriticalPercent = 95, ReviewSession = true };
        var alerts = new AlertService(settings);
        var raised = new List<AlertNotice>();
        alerts.Raised += raised.Add;
        var reset = DateTimeOffset.Now.AddMinutes(45);

        ProviderViewModel Model(double fraction, DateTimeOffset resetsAt) => new(
            new ProviderSnapshot("claude", "Claude", "", Fidelity.Official, SnapshotStatus.Ok,
                [new LimitWindow("five_hour", "Current session", fraction, resetsAt)]), settings);

        alerts.Evaluate([Model(0.10, reset)]);
        if (raised.Count != 0) throw new Exception("First evaluation must prime silently.");
        alerts.Evaluate([Model(0.50, reset)]);
        if (raised.Count != 0) throw new Exception("Below the warning threshold must stay quiet.");
        alerts.Evaluate([Model(0.82, reset)]);
        if (raised.Count != 1 || raised[0].Critical) throw new Exception("Warning threshold did not fire once.");
        alerts.Evaluate([Model(0.85, reset)]);
        if (raised.Count != 1) throw new Exception("Warning threshold repeated inside the same window.");
        alerts.Evaluate([Model(0.97, reset)]);
        if (raised.Count != 2 || !raised[1].Critical) throw new Exception("Critical threshold did not escalate.");
        alerts.Evaluate([Model(0.04, reset.AddHours(5))]);
        if (raised.Count != 3 || !raised[2].Title.Contains("reset")) throw new Exception("Window rollover did not report a reset.");
        alerts.Evaluate([Model(0.20, reset.AddHours(5))]);
        if (raised.Count != 3) throw new Exception("Alerts repeated after a reset.");
        Console.WriteLine("PASS: alerts prime silently, fire once per level, escalate, and report a reset exactly once.");
    }

    private sealed class ClaudeHandler : HttpMessageHandler
    {
        public int Count;
        public bool DateHeader;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var response = new HttpResponseMessage(Count == 1 ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.TooManyRequests);
            response.Content = new StringContent("""{"five_hour":{"utilization":73},"seven_day":{"utilization":7}}""");
            if (Count > 1) response.Headers.RetryAfter = DateHeader
                ? new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(10))
                : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }
    }

    private static void CheckClaudeCooldown()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"claudeAiOauth":{"accessToken":"diagnostic-fake-token"}}""");
            foreach (var dateHeader in new[] { false, true })
            {
                using var handler = new ClaudeHandler { DateHeader = dateHeader };
                using var http = new HttpClient(handler);
                var provider = new ClaudeSubscriptionProvider(http, path);
                var first = provider.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
                provider.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (handler.Count != 1) throw new Exception("Healthy Claude readings must throttle duplicate requests.");
                first = first with { FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-3) };
                typeof(ClaudeSubscriptionProvider).GetField("_lastGood", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, first);
                var limited = provider.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
                var retry = provider.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (first.Status != SnapshotStatus.Ok || limited.Status != SnapshotStatus.Stale || limited.Windows[0].UsedFraction != .73 || limited.FetchedAt != first.FetchedAt)
                    throw new Exception("Claude 429 cache failed.");
                if (handler.Count != 2 || retry.Status != SnapshotStatus.Stale || !retry.StatusText!.Contains("not live")) throw new Exception("Claude cooldown failed.");
                var next = (DateTimeOffset)typeof(ClaudeSubscriptionProvider).GetField("_nextAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
                if ((next - DateTimeOffset.UtcNow).TotalMinutes < 9) throw new Exception("Retry-After not respected.");
            }
            Console.WriteLine("PASS: Claude 429 retains genuine readings and timestamp, suppresses retries, respects delta/date Retry-After.");
            using var emptyHandler = new ClaudeHandler { Count = 1 };
            using var emptyHttp = new HttpClient(emptyHandler);
            var emptyProvider = new ClaudeSubscriptionProvider(emptyHttp, path);
            var empty = emptyProvider.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            emptyProvider.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (empty.Status != SnapshotStatus.Error || empty.Windows.Count != 0 || emptyHandler.Count != 2)
                throw new Exception("Claude must not manufacture usage without a successful reading.");
            Console.WriteLine("PASS: first-request 429 remains unknown, with no invented reading or immediate retry.");
        }
        finally { File.Delete(path); }
    }

    private static int DiscoverGemini()
    {
        using var http = new HttpClient();
        var provider = new GeminiSubscriptionProvider(http);
        var getToken = typeof(GeminiSubscriptionProvider).GetMethod("GetAccessToken", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var credentialsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");
        var token = ((Task<string>)getToken.Invoke(provider, [credentialsPath, CancellationToken.None])!).GetAwaiter().GetResult();
        var post = typeof(GeminiSubscriptionProvider).GetMethod("Post", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var response = ((Task<JsonDocument>)post.Invoke(provider, ["loadCodeAssist",
            new { metadata = new { ideType = "IDE_UNSPECIFIED", platform = "PLATFORM_UNSPECIFIED", pluginType = "GEMINI" } },
            token, CancellationToken.None])!).GetAwaiter().GetResult();
        foreach (var field in response.RootElement.EnumerateObject())
        {
            Console.WriteLine(field.Name + ": " + field.Value.ValueKind);
            if (field.Value.ValueKind == JsonValueKind.Object)
                foreach (var nested in field.Value.EnumerateObject())
                    Console.WriteLine("  " + nested.Name + ": " + nested.Value.ValueKind +
                        (nested.Name is "id" or "name" ? " = " + nested.Value.ToString() : ""));
            if (field.Name is "allowedTiers" or "ineligibleTiers" && field.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in field.Value.EnumerateArray())
                    foreach (var nested in item.EnumerateObject())
                        if (nested.Name is "id" or "name" or "reasonCode" or "reasonMessage" or "isDefault" or "hasAcceptedTos" or "hasOnboardedPreviously")
                            Console.WriteLine("  " + nested.Name + " = " + nested.Value.ToString());
        }
        return 0;
    }

    private static int Preview()
    {
        var app = new App { IsReviewSession = true };
        app.InitializeComponent();
        var folder = Path.Combine(Environment.CurrentDirectory, "Diagnostics", "Artifacts");
        Directory.CreateDirectory(folder);

        foreach (var mode in new[] { "Session", "Dual", "Compact", "White", "Custom", "States", "Top", "Remaining" })
        {
            var settings = new AppSettings
            {
                ReviewSession = true,
                ShowRemaining = mode == "Remaining",
                Edge = mode == "Top" ? "Top" : "Right",
                CursorEnabled = false,
                DisplayMode = mode is "Dual" or "Compact" or "Top" or "Remaining" ? "Dual" : "Session",
                ReducedMotion = true,
                CompactMode = mode == "Compact",
                ShowProviderNames = mode == "Compact",
                FloatingDock = mode is "White" or "Custom",
                DockColor = mode == "White" ? "#FFFFFF" : mode == "Custom" ? "#2A1E4D" : "#0B0B0E"
            };
            using var coordinator = new UsageCoordinator(settings);
            if (mode == "States")
            {
                coordinator.Items[0].Snapshot = Sample("claude", "Claude", 0.96, 0.72);
                coordinator.Items[1].Snapshot = new ProviderSnapshot("codex", "Codex", "", Fidelity.Official,
                    SnapshotStatus.NeedsAuth, [], "Codex is installed but not signed in. Run codex login once, then refresh.",
                    "https://chatgpt.com/codex", DateTimeOffset.Now.AddMinutes(-6));
                coordinator.Items[2].Snapshot = new ProviderSnapshot("gemini", "Gemini", "", Fidelity.Official,
                    SnapshotStatus.Loading, [], "Securely checking your account…");
            }
            else
            {
                coordinator.Items[0].Snapshot = Sample("claude", "Claude", 0.73, 0.07);
                coordinator.Items[1].Snapshot = Sample("codex", "Codex", 0.21, 0.37);
                coordinator.Items[2].Snapshot = Sample("gemini", "Gemini", 0.52, 0.18);
            }

            var window = new MainWindow(coordinator, settings);
            var background = (UsageNotch.Controls.DockSurface)window.FindName("DockBackground");
            background.Morph = settings.FloatingDock ? 0 : 1;
            background.Glass = settings.GlassSurface;
            var root = (FrameworkElement)window.Content;
            root.Opacity = 1;
            Render(root, 100, 600);
            typeof(MainWindow).GetMethod("Dock", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [false, 430]);
            var dockWidth = (int)Math.Ceiling(window.Width);
            var dockHeight = (int)Math.Ceiling(window.Height);
            var dock = Render(root, dockWidth, dockHeight);

            var popup = (FrameworkElement)window.FindName("PopupCard");
            popup.DataContext = coordinator.Items[mode == "States" ? 1 : 0];
            popup.Opacity = 1;
            if (mode == "Top")
            {
                var tail = (System.Windows.Shapes.Path)window.FindName("PopupTail");
                tail.VerticalAlignment = VerticalAlignment.Top;
                tail.Margin = new Thickness(161, 9, 0, 0);
            }
            var card = Render(popup, 368, double.PositiveInfinity);

            var scene = new DrawingVisual();
            using (var context = scene.RenderOpen())
            {
                var backdrop = new LinearGradientBrush(Color.FromRgb(0x1B, 0x22, 0x2C), Color.FromRgb(0x0E, 0x12, 0x18), 60);
                context.DrawRectangle(backdrop, null, new Rect(0, 0, 800, 600));
                context.DrawText(new FormattedText(mode + " · live WPF render · sample data", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.White, 1), new Point(26, 22));
                if (mode == "Top")
                {
                    context.DrawImage(dock, new Rect((800 - dockWidth) / 2, 54, dockWidth, dockHeight));
                    context.DrawImage(card, new Rect(216, 54 + dockHeight - 4, 368, card.PixelHeight));
                }
                else
                {
                    context.DrawImage(dock, new Rect(800 - dockWidth, 62, dockWidth, dockHeight));
                    context.DrawImage(card, new Rect(800 - dockWidth - 360, 96, 368, card.PixelHeight));
                }
            }
            Save(scene, 800, 600, Path.Combine(folder, mode + ".png"));
        }

        RenderMorphStrip(folder);

        var previewSettings = new AppSettings { ReviewSession = true, ReducedMotion = true };
        var previewHistoryPath = Path.Combine(folder, "sample-" + Guid.NewGuid().ToString("N") + ".db");
        var previewHistory = new UsageHistory(previewHistoryPath);
        using var previewCoordinator = new UsageCoordinator(previewSettings, previewHistory);
        var previewNow = DateTimeOffset.UtcNow;
        var previewReset = previewNow.AddHours(2);
        for (var i = 0; i <= 24; i++)
        {
            var at = previewNow.AddMinutes(-120 + i * 5);
            var sample = new ProviderSnapshot("claude", "Claude · sample data", "", Fidelity.Manual, SnapshotStatus.Ok,
                [new("five_hour", "5-hour session", .15 + i * .02, previewReset), new("seven_day", "Weekly window", .18 + i * .001, previewNow.AddDays(3))], FetchedAt: at, AccountName: "Sample");
            previewHistory.Record(sample, at);
            previewCoordinator.Items[0].Snapshot = sample;
            var codex = new ProviderSnapshot("codex", "Codex · sample data", "", Fidelity.Manual, SnapshotStatus.Ok,
                [new("default-primary", "5-hour window", .1 + i * .009, previewNow.AddHours(3)), new("default-secondary", "Weekly window", .35, previewNow.AddDays(4))], FetchedAt: at, AccountName: "Sample");
            previewHistory.Record(codex, at); previewCoordinator.Items.First(p => p.Id == "codex").Snapshot = codex;
        }
        var settingsWindow = new SettingsWindow(previewSettings, previewCoordinator);
        var settingsRoot = (FrameworkElement)settingsWindow.Content;
        settingsRoot.Opacity = 1;
        settingsRoot.RenderTransform = Transform.Identity;
        Render(settingsRoot, 1260, 880);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        wait.Tick += (_, _) => { wait.Stop(); frame.Continue = false; }; wait.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
        void Pump(int ms)
        {
            var f = new System.Windows.Threading.DispatcherFrame();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) => { t.Stop(); f.Continue = false; }; t.Start(); System.Windows.Threading.Dispatcher.PushFrame(f);
        }
        var tabs = (TabControl)settingsWindow.FindName("SettingsTabs");
        void Shot(int tab, string name, double width = 1260)
        {
            tabs.SelectedIndex = tab; Render(settingsRoot, width, 880); Pump(900);
            SaveImage(Render(settingsRoot, width, 880), Path.Combine(folder, name));
        }
        Shot(0, "Stats.png");
        SaveImage(Render(settingsRoot, 820, 880), Path.Combine(folder, "StatsCompact.png"));
        var dashboard = (UsageNotch.Controls.StatsDashboard)settingsWindow.FindName("StatsView");
        dashboard.Open("claude", null); Pump(1200);
        SaveImage(Render(settingsRoot, 1260, 880), Path.Combine(folder, "ProviderDetail.png"));
        dashboard.CloseDetail(); Pump(300);
        Shot(1, "History.png");
        Shot(2, "Settings.png");
        Shot(3, "Appearance.png");
        Shot(5, "AlertSettings.png", 1100);
        Shot(6, "Providers.png");
        Shot(7, "Budgets.png");
        Shot(8, "TimeSettings.png");
        Shot(10, "Data.png");
        previewSettings.AppTheme = "Light";
        UsageNotch.Services.Theme.Apply(settingsWindow.Resources, previewSettings);
        Shot(0, "StatsLight.png");
        Shot(1, "HistoryLight.png");
        Shot(3, "AppearanceLight.png");
        previewSettings.AppTheme = "Dark";
        UsageNotch.Services.Theme.Apply(settingsWindow.Resources, previewSettings);
        var alert = new AlertWindow(new AlertNotice("Claude · getting close", "Current session · 82% used · 18% left · resets in 47 min", false, "claude", .82, IsPreview: true), new AppSettings { ReviewSession = true, ReducedMotion = true });
        SaveImage(Render((FrameworkElement)alert.Content, 380, double.PositiveInfinity), Path.Combine(folder, "AlertCard.png"));

        Console.WriteLine("Rendered dock, popup, compact, states and settings to " + folder);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(previewHistoryPath);
        return 0;
    }

    private static int WorkspacePreview()
    {
        var app = new App { IsReviewSession = true }; app.InitializeComponent();
        var settings = new AppSettings { ReviewSession = true, ReducedMotion = false, GeminiEnabled = false, CursorEnabled = false, Edge = "Right" };
        var folder = Path.Combine(Environment.CurrentDirectory, "Diagnostics", "Artifacts"); Directory.CreateDirectory(folder);
        var history = new UsageHistory(Path.Combine(folder, "workspace-" + Guid.NewGuid().ToString("N") + ".db"));
        using var coordinator = new UsageCoordinator(settings, history);
        var now = DateTimeOffset.UtcNow;
        foreach (var id in new[] { "claude", "codex" })
        {
            for (int i = 0; i <= 48; i++)
            {
                var at = now.AddMinutes(-240 + i * 5);
                var snapshot = new ProviderSnapshot(id, id == "claude" ? "Claude · sample" : "Codex · sample", "", Fidelity.Manual, SnapshotStatus.Ok,
                    [new(id == "claude" ? "five_hour" : "default-primary", "5-hour window", .08 + i * (id == "claude" ? .012 : .007), now.AddHours(id == "claude" ? 2 : 3)),
                     new(id == "claude" ? "seven_day" : "default-secondary", "Weekly window", .2 + i * .002, now.AddDays(4))], FetchedAt: at, AccountName: "Preview fixture");
                history.Record(snapshot, at); coordinator.Items.First(p => p.Id == id).Snapshot = snapshot;
            }
        }
        var dock = new MainWindow(coordinator, settings); dock.Show(); dock.OpenSettings();
        return app.Run();
    }

    /// <summary>Renders the capsule-to-teardrop blend so the morph can be inspected frame by frame.</summary>
    private static void RenderMorphStrip(string folder)
    {
        var settings = new AppSettings { CursorEnabled = false, GeminiEnabled = false, ReducedMotion = true, DisplayMode = "Session", DockColor = "#0B0B0E" };
        using var coordinator = new UsageCoordinator(settings);
        coordinator.Items[0].Snapshot = Sample("claude", "Claude", 0.73, 0.07);
        coordinator.Items[1].Snapshot = Sample("codex", "Codex", 0.21, 0.37);
        var window = new MainWindow(coordinator, settings);
        var background = (UsageNotch.Controls.DockSurface)window.FindName("DockBackground");
        var root = (FrameworkElement)window.Content;
        root.Opacity = 1;

        var steps = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        var frames = new List<RenderTargetBitmap>();
        foreach (var step in steps)
        {
            background.Morph = step;
            background.InvalidateVisual();
            frames.Add(Render(root, 100, 380));
        }

        var scene = new DrawingVisual();
        using (var context = scene.RenderOpen())
        {
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x20, 0x28, 0x32), Color.FromRgb(0x0D, 0x11, 0x16), 60), null, new Rect(0, 0, 800, 470));
            context.DrawText(new FormattedText("Dock outline · free capsule → edge teardrop", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.White, 1), new Point(26, 22));
            for (var i = 0; i < frames.Count; i++)
            {
                context.DrawImage(frames[i], new Rect(40 + i * 150, 60, 100, 380));
                context.DrawText(new FormattedText($"morph {steps[i]:0.00}", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.LightGray, 1), new Point(40 + i * 150, 446));
            }
        }
        Save(scene, 800, 470, Path.Combine(folder, "Morph.png"));
    }

    private static ProviderSnapshot Sample(string id, string name, double primary, double weekly) =>
        new(id, name, "", Fidelity.Official, SnapshotStatus.Ok,
            [new LimitWindow(id == "claude" ? "five_hour" : "default-primary", id == "gemini" ? "Gemini Pro" : "Current session", primary, DateTimeOffset.Now.AddMinutes(51)),
             new LimitWindow(id == "claude" ? "seven_day" : "default-secondary", id == "gemini" ? "Gemini Flash" : "All models", weekly, DateTimeOffset.Now.AddDays(4))], AccountName: "Sample account");

    private static RenderTargetBitmap Render(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        var actualHeight = double.IsPositiveInfinity(height) ? Math.Ceiling(element.DesiredSize.Height) : height;
        element.Arrange(new Rect(0, 0, width, actualHeight));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)actualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static void Save(Visual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        SaveImage(bitmap, path);
    }

    private static void SaveImage(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
