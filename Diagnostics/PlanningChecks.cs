using System.IO;
using System.Text.Json.Nodes;
using UsageNotch.Models;
using UsageNotch.Services;

/// <summary>Budgets, best start, shared settings, history insights, planning alerts and local token parsing.</summary>
internal static class PlanningChecks
{
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); }

    public static void Run()
    {
        var now = DateTimeOffset.Now;
        // Budgets: within, near, on pace, over (the same rules as Android).
        var w = new LimitWindow("five_hour", "Current session", .40, now.AddHours(3));
        var budget = new BudgetSetting("claude", "five_hour", 60, null);
        Check(Budgets.Evaluate(budget, w, null, now, false).State == Budgets.State.Within, "a budget well above usage is within");
        Check(Budgets.Evaluate(budget, w with { UsedFraction = .55 }, null, now, false).State == Budgets.State.Near, "within 10 points of a budget is near");
        Check(Budgets.Evaluate(budget, w with { UsedFraction = .65 }, null, now, false).State == Budgets.State.Over, "past a budget is over");
        var fast = new PaceForecast("", "", 20, 100);
        Check(Budgets.Evaluate(budget, w, fast, now, false) is { State: Budgets.State.Pace } pace && pace.Text.Contains("100%"), "a forecast past the budget is on pace, with the projected figure");

        // Best time to start: renew in the middle of the busy block.
        var heat = new double[168]; var observed = new int[168];
        var weekday = (int)now.ToLocalTime().AddDays(1).DayOfWeek;
        for (var h = 14; h < 18; h++) { heat[weekday * 24 + h] = .2; observed[weekday * 24 + h] = 3; }
        var session = new LimitWindow("five_hour", "Current session", 0, null);
        var hint = BestStart.Hint(session, TimeSpan.FromHours(5), heat, observed, now.ToLocalTime().Date.AddDays(1).AddHours(6));
        Check(hint is { BusyFrom: 14, BusyTo: 18 } && hint.RenewAt.ToLocalTime().Hour == 16, "best start renews mid busy hours (4 PM for 2–6 PM)");

        // Shared settings: capture only what changed, newer remote entries win, ties keep ours, invalid entries are dropped.
        var mine = new AppSettings();
        Check(!SharedSettings.Capture(mine, 1) && mine.Shared.Count == 0, "defaults are never stamped, so they can't outvote a phone");
        mine.Budgets.Add(budget); mine.ClaudeDockLabel = "Personal";
        Check(SharedSettings.Capture(mine, 100) && mine.Shared.ContainsKey("budget:claude|five_hour") && mine.Shared["name:claude"].At == 100, "local edits are stamped");
        var remote = new JsonObject
        {
            ["budget:claude|five_hour"] = new JsonObject { ["v"] = new JsonObject { ["limit"] = 70, ["by"] = 1080 }, ["at"] = 200 },
            ["name:claude"] = new JsonObject { ["v"] = "Phone", ["at"] = 100 },
            ["accent:codex"] = new JsonObject { ["v"] = "#10A37F", ["at"] = 300 },
            ["budget:claude|x"] = new JsonObject { ["v"] = new JsonObject { ["limit"] = 500 }, ["at"] = 300 },
            ["order"] = new JsonObject { ["v"] = new JsonArray("codex", "claude"), ["at"] = 300 },
        };
        Check(SharedSettings.Merge(mine, remote, DateTimeOffset.UtcNow), "remote entries merge");
        SharedSettings.Apply(mine);
        Check(mine.Budgets.Single().Limit == 70 && mine.Budgets.Single().ByMinute == 1080 && mine.ClaudeDockLabel == "Personal" && mine.Accents["codex"] == "#10A37F"
              && mine.ProviderOrder.SequenceEqual(["codex", "claude"]) && !mine.Shared.ContainsKey("budget:claude|x"), "newer entries win, ties keep the PC's, invalid budgets are refused");
        mine.Budgets.Clear(); SharedSettings.Capture(mine, 400);
        Check(mine.Shared["budget:claude|five_hour"].V is null && !SharedSettings.Merge(mine, remote, DateTimeOffset.UtcNow), "removing a budget leaves a tombstone that older copies can't revive");

        // History insights: finished periods, limit hits, reset moves.
        var t0 = now.AddDays(-3);
        var points = new List<UsagePoint>();
        for (var i = 0; i < 6; i++) points.Add(new UsagePoint(t0.AddMinutes(i * 10), .1 + i * .18, t0.AddHours(5), "a"));
        for (var i = 0; i < 4; i++) points.Add(new UsagePoint(t0.AddHours(6).AddMinutes(i * 10), .05 + i * .1, t0.AddHours(11), "b"));
        points.Add(new UsagePoint(t0.AddHours(6).AddMinutes(45), .4, t0.AddHours(11).AddMinutes(30), "c"));
        var summary = HistoryInsights.Summarize(points, [new UsageEvent(t0.AddHours(6), "Confirmed reset", ""), new UsageEvent(t0.AddHours(6).AddMinutes(45), "Window changed", "")], now);
        Check(summary.Unused.Count == 2 && Math.Abs(summary.Unused[0].Peak - 1) < 1e-9, "finished periods report their peak (unused = 1 − peak)");
        Check(summary.Hits.Count == 1 && summary.Hits[0].Reset == t0.AddHours(5), "reaching the limit is logged with the reset it waited for");
        Check(summary.Reliability is { Moved: 1, Confirmed: 1, Changed: 1 } r && Math.Abs(r.AverageShiftMinutes - 30) < .1, "a reset time that moved before it arrived counts as a move");

        // Planning alerts fire once.
        var settings = new AppSettings { ReviewSession = true, AlertsEnabled = true };
        settings.Budgets.Add(new BudgetSetting("claude", "five_hour", 60, null));
        var alerts = new AlertService(settings, () => now);
        var raised = new List<AlertNotice>(); alerts.Raised += raised.Add;
        var vm = new ProviderViewModel(new ProviderSnapshot("claude", "Claude", "", Fidelity.Official, SnapshotStatus.Ok, [new LimitWindow("five_hour", "Current session", .7, now.AddMinutes(8))], FetchedAt: now), settings);
        var context = new AlertContext(new Dictionary<string, PaceForecast?> { [AlertContext.Key("claude", "five_hour")] = new PaceForecast("", "", 25, 90) }, new Dictionary<string, StartHint?>(), new Dictionary<string, WindowSummary?>());
        alerts.Evaluate([vm], context); var first = raised.Count;
        alerts.Evaluate([vm], context);
        Check(first >= 2 && raised.Any(a => a.Title.Contains("over your budget")) && raised.Any(a => a.Title.Contains("renews in")) && raised.Count == first, "budget and pre-reset alerts fire once per period");

        // Account types: an Edu seat's monthly credits, the plan, why a limit stopped and free resets (Codex app-server schema).
        using (var edu = System.Text.Json.JsonDocument.Parse("""
            {"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1793443246},"secondary":{"usedPercent":30,"windowDurationMins":10080,"resetsAt":1794030046},
              "credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"individualLimit":{"limit":"150","used":"30","remainingPercent":80,"resetsAt":1795000000},"planType":"edu","rateLimitReachedType":"workspace_member_credits_depleted"},
             "rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1793443246},"secondary":{"usedPercent":30,"windowDurationMins":10080,"resetsAt":1794030046},
              "individualLimit":{"limit":"150","used":"30","remainingPercent":80,"resetsAt":1795000000},"planType":"edu"}},
             "rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","title":"Full reset (Weekly + 5 hr)","expiresAt":1794001071}]}}}
            """))
        {
            var windows = UsageNotch.Providers.CodexSubscriptionProvider.ParseRateLimits(edu.RootElement);
            var monthly = windows.Single(x => x.Id == "codex-monthly");
            Check(Math.Abs(monthly.UsedFraction!.Value - .2) < 1e-9 && monthly.UsedAmount == 30 && monthly.LimitAmount == 150 && monthly.Unit == "credits", "an Edu seat's monthly limit reads as 30 of 150 credits (20%)");
            Check(windows.Any(x => x.Id == "codex-reset-credits" && x.UsedFraction is null), "an available free limit reset is shown, without a percentage");
            var (plan, status) = UsageNotch.Providers.CodexSubscriptionProvider.PlanAndStatus(edu.RootElement);
            Check(plan == "Edu" && status!.Contains("workspace owner"), "the ChatGPT plan and the reason a limit stopped are explained");
        }
        var todayKey = now.ToLocalTime().Date.ToString("yyyy-MM-dd");
        using (var usage = System.Text.Json.JsonDocument.Parse("{\"result\":{\"summary\":{\"lifetimeTokens\":1888779660,\"peakDailyTokens\":218035337,\"currentStreakDays\":0,\"longestStreakDays\":7},\"dailyUsageBuckets\":[{\"startDate\":\"" + todayKey + "\",\"tokens\":1000},{\"startDate\":\"2026-01-01\",\"tokens\":5}]}}"))
        {
            var account = UsageNotch.Providers.CodexSubscriptionProvider.ParseTokenUsage(usage.RootElement)!;
            var merged = TokenReport.WithAccount(null, "codex", "Codex", account, now);
            Check(account.Lifetime == 1888779660 && merged.AllDevices && merged.Today.Total == 1000 && merged.Days.Count == 30 && merged.Today.Cost is null, "Codex token totals come from OpenAI across devices, without an invented cost");
        }
        Check(UsageNotch.Providers.ClaudeSubscriptionProvider.PlanName("max", "default_claude_max_5x") == "Max 5×" && UsageNotch.Providers.ClaudeSubscriptionProvider.PlanName("pro", null) == "Pro", "Claude plans read as Max 5× and Pro");
        using (var credit = System.Text.Json.JsonDocument.Parse("""{"utilization":99.99,"limit_dollars":250,"used_dollars":249.99}"""))
            Check(UsageNotch.Providers.ClaudeSubscriptionProvider.LabelFor("iguana_necktie", credit.RootElement) == "Usage credit · $250", "unknown Claude limits with a dollar cap read as usage credits");
        Check(UsageNotch.Providers.CursorSubscriptionProvider.PlanName("pro") == "Pro" && UsageNotch.Providers.CursorSubscriptionProvider.PlanName("free_trial") == "Free trial", "Cursor memberships read as plans");

        // Accuracy: usage while the PC slept and usage right after a renewal are counted, never guessed beyond that.
        var day = now.ToLocalTime().Date.AddHours(9); var period = new DateTimeOffset(day, now.ToLocalTime().Offset);
        var slept = new List<UsagePoint> { new(period, .10, period.AddHours(5), "a"), new(period.AddMinutes(10), .20, period.AddHours(5), "a"),
            new(period.AddHours(2), .60, period.AddHours(5), "a"),                           // the PC slept for 2 h: +40 points, timing unknown
            new(period.AddHours(6), .15, period.AddHours(10), "b") };                        // renewed during the gap: 15 points since then
        var sleptSummary = HistoryInsights.Summarize(slept, [], period.AddHours(6).AddMinutes(1));
        Check(Math.Abs(sleptSummary.Days[^1].Used!.Value - (.10 + .40 + .15)) < 1e-9, "daily totals include usage across sleep gaps and after renewals (0.65, not 0.10)");
        Check(sleptSummary.Days[^1].Coverage is > 0 and < .1, "coverage shows how little of the day was observed");
        Check(sleptSummary.Heat.Sum() is var heatSum && Math.Abs(heatSum - .10) < 1e-9, "only usage with a known time shapes the busy-hours map");

        // Local tokens: Claude lines repeat usage per content block; Codex totals are cumulative.
        var dir = Path.Combine(Path.GetTempPath(), "UsageNotch-Tokens-" + Guid.NewGuid().ToString("N"));
        var claudeDir = Path.Combine(dir, ".claude", "projects", "proj"); Directory.CreateDirectory(claudeDir);
        var stamp = DateTimeOffset.UtcNow.ToString("o");
        string Line(string id, int input, int output) => $"{{\"cwd\":\"C:\\\\Code\\\\UsageNotch\",\"requestId\":\"r{id}\",\"timestamp\":\"{stamp}\",\"message\":{{\"id\":\"m{id}\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output},\"cache_read_input_tokens\":1000,\"cache_creation_input_tokens\":200,\"cache_creation\":{{\"ephemeral_1h_input_tokens\":200}}}}}}}}";
        File.WriteAllText(Path.Combine(claudeDir, "s.jsonl"), Line("1", 10, 100) + "\n" + Line("1", 10, 100) + "\n" + Line("2", 5, 50) + "\n");
        var codexDir = Path.Combine(dir, ".codex", "sessions", "2026"); Directory.CreateDirectory(codexDir);
        string Total(int input, int cached, int output) => $"{{\"timestamp\":\"{stamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"input_tokens\":{input},\"cached_input_tokens\":{cached},\"output_tokens\":{output}}}}}}}}}";
        File.WriteAllText(Path.Combine(codexDir, "rollout.jsonl"), $"{{\"timestamp\":\"{stamp}\",\"type\":\"session_meta\",\"payload\":{{\"cwd\":\"C:\\\\Code\\\\Site\"}}}}\n{{\"timestamp\":\"{stamp}\",\"type\":\"turn_context\",\"payload\":{{\"model\":\"gpt-test\"}}}}\n" + Total(1000, 400, 50) + "\n" + Total(1000, 400, 50) + "\n" + Total(1500, 600, 80) + "\n");
        var tokens = new LocalTokenUsage(Path.Combine(dir, "tokens.db"));
        var sources = new[] { new LocalTokenUsage.Source("claude", "Claude Code", Path.Combine(dir, ".claude", "projects"), false), new LocalTokenUsage.Source("codex", "Codex", Path.Combine(dir, ".codex", "sessions"), true) };
        typeof(LocalTokenUsage).GetMethod("Scan", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(tokens, [sources, CancellationToken.None]);
        var claude = tokens.Report(sources[0], DateTimeOffset.Now)!; var codexReport = tokens.Report(sources[1], DateTimeOffset.Now)!;
        Check(claude.Today.Input == 15 && claude.Today.Output == 150 && claude.Today.CacheRead == 2000 && claude.Projects[0].Name == "UsageNotch", "Claude Code lines are counted once per message, by project");
        var expected = (15 * 4 + 150 * 20 + 2000 * .2 + 400 * 4 * 2) / 1_000_000;
        Check(Math.Abs(claude.Today.Cost!.Value - expected) < 1e-9, "API-price equivalent uses the model's input, output, cache-read and 1-hour cache-write prices");
        Check(codexReport.Today.Input == 900 && codexReport.Today.CacheRead == 600 && codexReport.Today.Output == 80 && codexReport.Today.Cost is null && codexReport.Projects[0].Name == "Site", "Codex counts only increases of its cumulative totals and has no invented cost");
        File.AppendAllText(Path.Combine(claudeDir, "s.jsonl"), Line("3", 1, 1) + "\n");
        typeof(LocalTokenUsage).GetMethod("Scan", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(tokens, [sources, CancellationToken.None]);
        Check(tokens.Report(sources[0], DateTimeOffset.Now)!.Today.Input == 16, "later scans read only what was appended");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch { }
    }
}
