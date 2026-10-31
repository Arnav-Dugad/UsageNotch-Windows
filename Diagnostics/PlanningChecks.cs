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
