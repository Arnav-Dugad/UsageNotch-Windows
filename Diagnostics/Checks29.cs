using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UsageNotch.Models;
using UsageNotch.Services;
using UsageNotch.Services.Phone;

/// <summary>Windows 2.9: usage score, typical week, unusual days, notes, plan history, token facts, project budgets and the v4 phone fixture.</summary>
internal static class Checks29
{
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); }

    /// <summary>90 days ending today: 0.4 a day, 0.6 on the weekday of two days ago, and that day itself at 1.2 (unusual: twice its usual).</summary>
    private static List<DayUsage> Calendar(DateTime today, Func<DateTime, double?>? custom = null) => Enumerable.Range(0, 90).Select(i => today.AddDays(i - 89))
        .Select(d => new DayUsage(d, custom?.Invoke(d) ?? (d == today.AddDays(-2) ? 1.2 : d.DayOfWeek == today.AddDays(-2).DayOfWeek ? .6 : .4), d == today ? .5 : .9)).ToList();

    private static WindowSummary Summary(IReadOnlyList<DayUsage> calendar, IReadOnlyList<UnusedPeriod>? unused = null) =>
        new(calendar.TakeLast(30).ToList(), calendar, new double[168], new int[168], 30, unused ?? [], [], new ResetReliability(0, 0, 0, 0, 0, 0));

    public static void Run(string? artifacts)
    {
        var today = DateTime.Today; var now = new DateTimeOffset(today.AddHours(23));
        var calendar = Calendar(today);

        // Typical week and unusual days.
        var from = today.AddDays(-6);
        var typical = HistoryInsights.TypicalWeek(calendar, from);
        Check(typical is { } t && Math.Abs(t - (6 * .4 + .6)) < 1e-9, $"typical week is the median of earlier 7-day blocks ({typical})");
        Check(HistoryInsights.TypicalWeek(calendar.TakeLast(20).ToList(), from) is null, "no typical week with fewer than 3 earlier blocks");
        var odd = HistoryInsights.Unusual(calendar);
        Check(odd.Count == 1 && odd[0].Date == today.AddDays(-2) && Math.Abs(odd[0].Ratio - 1.2 / .6) < .01 && Math.Abs(odd[0].Typical - .6) < 1e-9, "an unusual day is ≥1.8× its weekday's median");
        Check(HistoryInsights.Unusual(Calendar(today, d => d == today.AddDays(-2) ? 1.0 : .6)).Count == 0, "1.67× the usual is not unusual");

        // Usage score.
        var settings = new AppSettings();
        ProviderViewModel P(string id, string name, double session, double weekly) => new(new ProviderSnapshot(id, name, "", Fidelity.Official, SnapshotStatus.Ok,
            id == "claude" ? [new LimitWindow("five_hour", "Current session", session, now.AddHours(2)), new LimitWindow("seven_day", "All models", weekly, now.AddDays(3))]
                           : [new LimitWindow("codex-primary", "5-hour window", session, now.AddHours(2)), new LimitWindow("codex-secondary", "Weekly window", weekly, now.AddDays(3))],
            FetchedAt: now, Plan: id == "claude" ? "Max 5×" : "Plus"), settings);
        var claude = P("claude", "Claude", .2, .71); var codex = P("codex", "Codex", .1, .30);
        var score = UsageScore.Compute([claude, codex], (p, w) => w.Id.Contains("seven") || w.Id.EndsWith("secondary") ? Summary(calendar, [new UnusedPeriod(now.AddDays(-4), p.Id == "claude" ? .5 : .2)]) : Summary(calendar))!;
        Check(score.Value == 51 && score.Previous == 35 && score.Providers.Select(e => e.Window).SequenceEqual(["seven_day", "codex-secondary"]), $"usage score averages weekly limits: 51, 35 last week ({score.Value}, {score.Previous})");
        Check(UsageScore.Compute([claude, codex], (p, w) => p.Id == "claude" ? Summary(calendar, [new UnusedPeriod(now.AddDays(-4), .5)]) : Summary(calendar))!.Previous is null, "no previous score unless every provider has a finished week");
        Check(UsageScore.Word(85) == "Near your limits" && UsageScore.Word(51) == "Busy" && UsageScore.Word(5) == "Light", "score words");

        // Report: typical, unusual, efficiency, value, confidence note and notes.
        var month = new TokenTotals(1_000_000, 2_000_000, 400_000_000, 10_000_000, 412.3);
        var tokens = new TokenReport("claude", "Claude Code", TokenTotals.Zero, new TokenTotals(1, 1, 33_000_000, 0, 50), [], [new TokenShare("UsageNotch-Windows", 30_000_000, 40, .9)], [], new long[24], Month: month, MonthFrom: new DateTime(today.Year, today.Month, 1));
        var notes = new[] { new DayNote(today.AddDays(-1), "Big refactor", 1), new DayNote(today.AddDays(-3), "", 2), new DayNote(today.AddDays(-20), "Old", 3) };
        var report = WeeklyReport.Build([claude], (_, _) => Summary(calendar), [tokens], true, now, false, notes)!;
        var texts = report.Insights.Select(i => i.Text).ToArray();
        foreach (var x in texts) Console.WriteLine("      " + x);
        var week = calendar.Where(d => d.Date >= from).Sum(d => d.Used!.Value);
        Check(Math.Abs(report.Providers[0].Typical!.Value - 3.0) < 1e-9 && Math.Abs(report.Providers[0].TypicalChange!.Value - (week - 3) / 3) < 1e-3, "report rows carry the typical week and the change against it");
        Check(texts.Any(x => x.StartsWith("You used Claude ") && x.EndsWith($" the week before ({Math.Round((week - 3) / 3 * 100):0}% above your typical week).")), "the week before and the typical week in one sentence");
        Check(texts.Any(x => x == $"{today.AddDays(-2):dddd} was about twice your normal {today.AddDays(-2):dddd} for Claude."), "unusual-day sentence (2× reads as about twice)");
        Check(texts.Any(x => x.StartsWith("Claude Code got about ") && x.EndsWith(" tokens from each session limit this week.")), "tokens-per-limit sentence");
        Check(texts.Any(x => x == "This month, Claude Code's tokens would cost about $412 at API prices. Your Max 5× plan is $100 a month."), "value sentence against the plan's price");
        Check(report.Insights.Count <= WeeklyReport.MaxInsights && report.Insights.Select(i => Array.IndexOf(WeeklyReport.Order, i.Kind)).Zip(report.Insights.Select(i => Array.IndexOf(WeeklyReport.Order, i.Kind)).Skip(1)).All(z => z.First <= z.Second), "at most 8 sentences, in the documented order");
        Check(report.Notes!.Count == 1 && report.Notes[0].Text == "Big refactor", "this week's notes, without deleted or older ones");
        Check(report.Coverage > .8 && report.Note is null, "no confidence note when the PC saw most of the week");
        var thin = WeeklyReport.Build([claude], (_, _) => Summary(calendar.Select(d => d with { Coverage = .2 }).ToList()), [], true, now, false)!;
        Check(thin.Note == "This PC saw 20% of this week, so which day and hour things happened is less certain. Totals are still complete.", "confidence note when the PC saw under half the week");
        Check(WeeklyReport.Money(100) == "$100" && WeeklyReport.Money(41.2) == "$41.20" && WeeklyReport.Money(1234.4) == "$1,234" && PlanPrices.For("claude", "Max 20×") == 200 && PlanPrices.For("codex", "Plus") is null, "prices and money read naturally");

        // Model mix: 5 biggest models and Other, 8 weeks oldest first.
        var blocks = new Dictionary<(int, string), long>();
        for (var b = 0; b < 8; b++) for (var m = 0; m < 7; m++) blocks[(b, "model-" + m)] = (m + 1) * 1000 + b;
        var weeks = TokenReport.Weeks(blocks, today);
        Check(weeks.Count == 8 && weeks[0].Start == today.AddDays(-55) && weeks[^1].Start == today.AddDays(-6)
            && weeks.Select((w, i) => w.Models.Count == 6 && w.Models[^1].Name == "Other" && w.Models[^1].Total == 3000 + 2 * (7 - i) && w.Models[0].Name == "model-6").All(x => x),
            "model mix keeps the 5 biggest models and groups the rest as Other");

        // Notes and plan history in the history database.
        var dir = Path.Combine(Path.GetTempPath(), "UsageNotch-29-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var history = new UsageHistory(Path.Combine(dir, "history.db"));
            var nowUtc = DateTimeOffset.UtcNow; var ms = nowUtc.ToUnixTimeMilliseconds();
            Check(history.SetNote(today, "  Big\nrefactor  ", nowUtc) && history.Notes().Single().Text == "Big refactor", "notes are one trimmed line");
            Check(!history.MergeNotes([new DayNote(today, "Older", ms - 60_000)], nowUtc) && history.Notes().Single().Text == "Big refactor", "an older note never replaces a newer one");
            var mine = history.Notes().Single().At;
            Check(!history.MergeNotes([new DayNote(today, "Same time", mine)], nowUtc) && history.Notes().Single().Text == "Big refactor", "on a tie, the PC's note wins");
            Check(history.MergeNotes([new DayNote(today, "", mine + 1)], nowUtc) && history.Notes().Single().Deleted, "an empty note from a phone deletes (kept as a tombstone)");
            Check(!history.MergeNotes([new DayNote(today.AddDays(5), "Future", ms), new DayNote(new DateTime(2010, 1, 1), "Old", ms), new DayNote(today, "Far future", ms + 3 * 86_400_000L)], nowUtc), "notes with impossible dates or times are ignored");
            Check(history.MergeNotes([new DayNote(today.AddDays(-1), new string('x', 300), ms)], nowUtc) && history.Notes().First(n => n.Date == today.AddDays(-1)).Text.Length == DayNote.MaxLength, "notes are cut to 140 characters");

            ProviderSnapshot Snap(string? plan, DateTimeOffset at, double used = .3) => new("claude", "Claude", "", Fidelity.Official, SnapshotStatus.Ok,
                [new LimitWindow("five_hour", "Current session", used, at.AddHours(3)), new LimitWindow("seven_day", "All models", .4, at.AddDays(3))], FetchedAt: at, AccountName: "Sample", Plan: plan);
            history.Record(Snap("Pro", nowUtc.AddDays(-20)), nowUtc.AddDays(-20));
            history.Record(Snap("Pro", nowUtc.AddDays(-19)), nowUtc.AddDays(-19));
            history.Record(Snap(null, nowUtc.AddDays(-18)), nowUtc.AddDays(-18));
            history.Record(Snap("Max 5×", nowUtc.AddDays(-2)), nowUtc.AddDays(-2));
            var plans = history.Plans("claude", history.AccountKey(Snap(null, nowUtc)));
            Check(plans.Select(p => p.Plan).SequenceEqual(["Pro", "Max 5×"]) && plans[0].At < plans[1].At, "plan history records each change once, oldest first");

            // Shared project budgets.
            var s1 = new AppSettings(); s1.ProjectBudgets["claude|UsageNotch Windows (2)"] = 50_000_000; s1.ProjectBudgets["claude|tiny"] = 5;
            SharedSettings.Capture(s1, 100);
            Check(s1.Shared.ContainsKey("projectBudget:claude|UsageNotch Windows (2)") && !s1.Shared.ContainsKey("projectBudget:claude|tiny"), "project budgets sync, invalid ones don't");
            var s2 = new AppSettings();
            SharedSettings.Merge(s2, SharedSettings.ToJson(s1), DateTimeOffset.UtcNow); SharedSettings.Apply(s2);
            Check(s2.ProjectBudgets.GetValueOrDefault("claude|UsageNotch Windows (2)") == 50_000_000, "a phone's project budget applies on the PC");
            var bad = new JsonObject { ["projectBudget:claude|x"] = new JsonObject { ["v"] = new JsonObject { ["tokens"] = 5 }, ["at"] = 500 }, ["projectBudget:claude|a|b"] = new JsonObject { ["v"] = new JsonObject { ["tokens"] = 1_000_000 }, ["at"] = 500 } };
            Check(!SharedSettings.Merge(s2, bad, DateTimeOffset.UtcNow), "project budgets outside 100K–1T tokens or with a bar in the name are rejected");

            // Snapshot: score, notes, plans, unusual days, report additions, token facts (v4 fixture).
            history.SetNote(today.AddDays(-1), "Big refactor", nowUtc);
            foreach (var p in ReportChecks.TwoWeeks(today.AddDays(-1))) history.Record(Snap("Max 5×", p.At, p.Used), p.At);
            var live = Snap("Max 5×", nowUtc, .2);
            var view = new ProviderViewModel(live, new AppSettings());
            WindowSummary? Sum(ProviderViewModel p, LimitWindow w) => HistoryInsights.Summarize(history.Read("claude", history.AccountKey(live), w.Id, nowUtc.AddDays(-91)), [], nowUtc);
            var liveScore = UsageScore.Compute([view], Sum);
            var liveReport = WeeklyReport.Build([view], Sum, [tokens], false, nowUtc, false, history.Notes());
            var extras = new PhoneSnapshot.Extras(true, [tokens], [], true, Report: liveReport, Score: liveScore,
                Facts: r => WeeklyReport.Facts(r, view, Sum(view, view.SessionWindow!)!.Total(7), "Current session"), ProjectBudgets: new Dictionary<string, long> { ["claude|UsageNotch-Windows"] = 50_000_000 });
            var json = PhoneSnapshot.Serialize(PhoneSnapshot.FromLive([live], new AppSettings { ShowAccountNames = false }, history, null, extras: extras));
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement; var features = root.GetProperty("features").EnumerateArray().Select(x => x.GetString()).ToArray();
                Check(new[] { "score", "notes", "plans", "tokens2" }.All(features.Contains), "snapshot lists score, notes, plans and tokens2");
                Check(root.GetProperty("score").GetProperty("value").GetInt32() == liveScore!.Value && root.GetProperty("score").GetProperty("providers")[0].GetProperty("window").GetString() == "seven_day", "score travels at the root");
                Check(root.GetProperty("notes").EnumerateArray().Any(n => n.GetProperty("text").GetString() == "Big refactor" && n.GetProperty("at").GetInt64() > 0), "notes travel at the root");
                var provider = root.GetProperty("providers")[0];
                Check(provider.GetProperty("plans").GetArrayLength() == 2 && provider.GetProperty("plans")[1].GetProperty("plan").GetString() == "Max 5×", "plan history travels with the provider");
                var src = root.GetProperty("tokens").GetProperty("sources")[0];
                Check(src.GetProperty("month").GetProperty("cost").GetDouble() == 412.3 && src.GetProperty("plan").GetProperty("price").GetDouble() == 100
                    && src.GetProperty("projects")[0].GetProperty("budget").GetProperty("tokens").GetInt64() == 50_000_000, "month, plan price and project budgets travel with tokens");
                Check(root.GetProperty("report").TryGetProperty("coverage", out _) && root.GetProperty("report").GetProperty("notes")[0].GetProperty("text").GetString() == "Big refactor", "report carries coverage and this week's notes");
            }
            if (artifacts is not null) { Directory.CreateDirectory(artifacts); File.WriteAllText(Path.Combine(artifacts, "phone-snapshot-v4-windows.json"), json); }

            // PUT /v1/notes through the real HTTPS server.
            using var identity = new PhoneIdentity(Path.Combine(dir, "identity"), "Test PC");
            var link = new PhoneLinkService(new AppSettings(), history, Path.Combine(dir, "link2"));
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var changed = 0; link.NotesChanged += () => changed++;
            var server = new PhoneServer(identity, _ => json, null, null, link.MergeNotes);
            server.Start(port, loopbackOnly: true).GetAwaiter().GetResult();
            try
            {
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert != null && Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(identity.Pin, StringComparison.OrdinalIgnoreCase) };
                using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
                HttpResponseMessage Put(string body) => client.PutAsync("/v1/notes", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                var date = today.AddDays(-4).ToString("yyyy-MM-dd");
                var ok = Put($$"""{"notes":[{"date":"{{date}}","text":"Exam week","at":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}]}""");
                var body = ok.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(ok.StatusCode == HttpStatusCode.OK && body.Contains("Exam week") && body.Contains("Big refactor") && changed == 1, "PUT /v1/notes merges a phone's note and returns every note");
                Check(Put("""{"notes":[{"date":"yesterday","text":"x","at":1}]}""").StatusCode == HttpStatusCode.BadRequest && Put("""{"nope":1}""").StatusCode == HttpStatusCode.BadRequest, "invalid notes are rejected with 400");
                Check(Put("{\"notes\":[" + string.Join(",", Enumerable.Repeat($$"""{"date":"{{date}}","text":"{{new string('y', 200)}}","at":1}""", 400)) + "]}").StatusCode == HttpStatusCode.RequestEntityTooLarge, "note uploads over 64 KB are refused");
            }
            finally { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); link.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

            // The tray ring's pulse frames draw.
            using (var frame = TrayRing.Draw(.8, false, true, .5)) Check(frame.Width == TrayRing.Size, "tray pulse frames draw");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }
}
