using System.IO;
using Microsoft.Data.Sqlite;
using UsageNotch.Models;
using UsageNotch.Services;
using UsageNotch.Services.Phone;

/// <summary>
/// Prints "This week" and the all-time totals from this PC's real history, read-only (a copy of the database is used),
/// so the sentences can be checked against real data. Prints numbers and sentences only, no account details.
/// </summary>
internal static class RealReport
{
    public static int Run()
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageNotch", "history.db");
        if (!File.Exists(source)) { Console.WriteLine("No history on this PC."); return 1; }
        var copy = Path.Combine(Path.GetTempPath(), "UsageNotch-RealReport-" + Guid.NewGuid().ToString("N") + ".db");
        using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy }.ToString()))
        { from.Open(); to.Open(); from.BackupDatabase(to); }
        try
        {
            var history = new UsageHistory(copy);
            var stored = PhoneSnapshot.FromDatabase(copy);
            var now = DateTimeOffset.UtcNow;
            var accounts = new Dictionary<string, string>();
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT provider, account FROM provider_state"; using var r = cmd.ExecuteReader(); while (r.Read()) accounts[r.GetString(0)] = r.GetString(1); }
            var views = new List<ProviderViewModel>();
            foreach (var p in stored.Providers)
            {
                var snap = new ProviderSnapshot(p.Id, p.Name, "", Fidelity.Official, SnapshotStatus.Ok,
                    p.Windows.Select(w => new LimitWindow(w.Id, w.Label, w.Used, w.Reset is { } r ? DateTimeOffset.FromUnixTimeMilliseconds(r) : null)).ToList(), FetchedAt: now);
                views.Add(new ProviderViewModel(snap, new AppSettings()));
            }
            var counts = history.Counts();
            Console.WriteLine($"{counts.Readings:N0} readings since {counts.Since?.ToLocalTime():MMM d, yyyy}; providers: {string.Join(", ", views.Select(v => v.Id))}");
            WindowSummary? Summary(ProviderViewModel p, LimitWindow w) =>
                accounts.TryGetValue(p.Id, out var a) ? HistoryInsights.Summarize(history.Read(p.Id, a, w.Id, now.AddDays(-91)), history.ReadEvents(p.Id, a, w.Id, now.AddDays(-30)), now) : null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var report = WeeklyReport.Build(views, Summary, [], false, now, false);
            Console.WriteLine($"Report in {clock.ElapsedMilliseconds} ms");
            if (report is null) { Console.WriteLine("No report (no main limits with readings)."); }
            else
            {
                Console.WriteLine($"{report.Headline} — {report.Summary}");
                foreach (var row in report.Providers) Console.WriteLine($"  {row.Name} ({row.Label}): {row.Used:0.###} vs {row.Previous:0.###} limits, change {row.Change:P0}, {row.ActiveDays} active days, {row.Hits} hits, streak {row.Streak}");
                foreach (var i in report.Insights) Console.WriteLine($"  • [{i.Kind}] {i.Text}");
            }
            foreach (var v in views.Where(v => accounts.ContainsKey(v.Id)))
                if (v.WeeklyWindow is { } wk)
                {
                    var pts = history.Read(v.Id, accounts[v.Id], wk.Id, now.AddDays(-40));
                    foreach (var g in pts.GroupBy(p => p.Period).Select(g => g.ToArray()).OrderBy(g => g[0].At).TakeLast(6))
                        Console.WriteLine($"  {v.Id}/{wk.Id} period {g[0].At.ToLocalTime():MMM d HH:mm}–{g[^1].At.ToLocalTime():MMM d HH:mm} n={g.Length} peak={g.Max(p => p.Used):0.###} reset={g[^1].Reset?.ToLocalTime():MMM d HH:mm}");
                    foreach (var u in HistoryInsights.Unused(pts, now)) Console.WriteLine($"    unused end {u.End.ToLocalTime():MMM d HH:mm} peak {u.Peak:0.###}");
                }
            foreach (var v in views)
            {
                if (!accounts.TryGetValue(v.Id, out var a)) continue;
                foreach (var w in new[] { v.SessionWindow, v.WeeklyWindow }.Where(w => w is not null).Distinct())
                {
                    clock.Restart();
                    var counter = new HistoryInsights.AllTimeCounter(); history.ForEach(v.Id, a, w!.Id, counter.Add);
                    var all = counter.Result();
                    Console.WriteLine($"  All time {v.Id}/{w.Id}: {all?.Used:0.##} limits since {all?.Since.ToLocalTime():MMM d}, {all?.ActiveDays} active of {all?.RecordedDays} days, {all?.Hits} hits, best {all?.BestDay:MMM d} ({all?.BestUsed:0.##}) in {clock.ElapsedMilliseconds} ms");
                }
            }
            return 0;
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(copy); } catch { } }
    }
}
