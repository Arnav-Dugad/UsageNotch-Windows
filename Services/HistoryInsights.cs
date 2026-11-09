namespace UsageNotch.Services;

/// <summary>One local date: usage as a fraction of the limit (null without readings) and how much of the day was observed (0..1).</summary>
public sealed record DayUsage(DateTime Date, double? Used, double Coverage = 0);
public sealed record UnusedPeriod(DateTimeOffset End, double Peak);
public sealed record LimitHit(DateTimeOffset At, DateTimeOffset? Reset);
public sealed record ResetReliability(int Periods, int Fixed, int Moved, double AverageShiftMinutes, int Confirmed, int Changed);
/// <summary>
/// Everything this PC has recorded for one window: the first reading, total consumption in limits (2.5 = two and a half
/// full limits), days with usage and days with readings, limit periods that reached 99.5%, and the busiest day.
/// </summary>
public sealed record AllTimeTotals(DateTimeOffset Since, double Used, int ActiveDays, int RecordedDays, int Hits, DateTime? BestDay, double BestUsed);

/// <summary>Everything the History page and paired phones show for one limit window, computed from the local readings.</summary>
public sealed record WindowSummary(
    IReadOnlyList<DayUsage> Days, IReadOnlyList<DayUsage> Calendar, double[] Heat, int[] Observed, int Streak,
    IReadOnlyList<UnusedPeriod> Unused, IReadOnlyList<LimitHit> Hits, ResetReliability Reliability)
{
    // The 90-day calendar holds the same days as Days (30), so 7, 30 and 90 day ranges all read from it.
    public double? Total(int days) => Calendar.TakeLast(days).Where(d => d.Used is not null).Select(d => d.Used!.Value).DefaultIfEmpty().Sum() is var sum && Calendar.TakeLast(days).Any(d => d.Used is not null) ? sum : null;
    public int Recorded(int days) => Calendar.TakeLast(days).Count(d => d.Used is not null);
    /// <summary>How much of the last <paramref name="days"/> days this PC observed, 0..1.</summary>
    public double Coverage(int days) => Calendar.TakeLast(days).Select(d => d.Coverage).DefaultIfEmpty(0).Average();
    public DayUsage? Busiest(int days) => Calendar.TakeLast(days).Where(d => d.Used is > 0).MaxBy(d => d.Used);
    /// <summary>The busiest observed weekday × hour cells, most first: (weekday 0 = Sunday, hour, consumption).</summary>
    public IReadOnlyList<(int Weekday, int Hour, double Used)> BusiestHours(int count = 3) => Enumerable.Range(0, 168)
        .Where(i => Observed[i] > 0 && Heat[i] > .0001).OrderByDescending(i => Heat[i]).Take(count).Select(i => (i / 24, i % 24, Heat[i])).ToArray();
}

public static class HistoryInsights
{
    public const int HistoryDays = 30, CalendarDays = 90;

    /// <summary>
    /// Daily consumption (30 and 90 days), a 30-day weekday × hour heatmap, the streak, finished periods, limit hits and
    /// reset reliability, in local time. Consumption is the sum of increases between continuous readings; gaps and resets
    /// add nothing and are never guessed.
    /// </summary>
    public static WindowSummary Summarize(IReadOnlyList<UsagePoint> points, IReadOnlyList<UsageEvent> events, DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date; var heatFrom = today.AddDays(-HistoryDays + 1);
        var consumed = new Dictionary<DateTime, double>(); var observed = new HashSet<DateTime>(); var covered = new Dictionary<DateTime, double>();
        var heat = new double[168]; var hours = new int[168]; var seenHours = new HashSet<(DateTime, int)>();
        for (var i = 0; i < points.Count; i++)
        {
            var local = points[i].At.ToLocalTime(); var cell = (int)local.DayOfWeek * 24 + local.Hour;
            var recent = local.Date >= heatFrom;
            observed.Add(local.Date);
            if (recent && seenHours.Add((local.Date, local.Hour))) hours[cell]++;
            if (i == 0) continue;
            var (delta, timed) = UsageAnalytics.Consumed(points[i - 1], points[i]);
            // Usage after a gap or a renewal counts on the day it was read; only timed usage shapes the hour map.
            consumed[local.Date] = consumed.GetValueOrDefault(local.Date) + delta;
            if (recent && timed) heat[cell] += delta;
            if (points[i].At - points[i - 1].At <= TimeSpan.FromMinutes(20))
            {
                // Observed time, split across local dates.
                var from = points[i - 1].At.ToLocalTime();
                while (from < local)
                {
                    var next = from.Date.AddDays(1) < local.DateTime ? new DateTimeOffset(from.Date.AddDays(1), from.Offset) : local;
                    covered[from.Date] = covered.GetValueOrDefault(from.Date) + (next - from).TotalHours;
                    from = next;
                }
            }
        }
        var nowLocal = now.ToLocalTime();
        double Coverage(DateTime d) => Math.Clamp(covered.GetValueOrDefault(d) / (d == today ? Math.Max(1 / 60d, (nowLocal - new DateTimeOffset(today, nowLocal.Offset)).TotalHours) : 24), 0, 1);
        List<DayUsage> Range(int count) => Enumerable.Range(0, count).Select(i => today.AddDays(i - count + 1))
            .Select(d => new DayUsage(d, observed.Contains(d) ? Math.Round(consumed.GetValueOrDefault(d), 4) : null, Math.Round(Coverage(d), 3))).ToList();
        bool Used(DateTime d) => observed.Contains(d) && consumed.GetValueOrDefault(d) > .0001;
        var streak = 0; var cursor = Used(today) ? today : today.AddDays(-1);
        while (Used(cursor)) { streak++; cursor = cursor.AddDays(-1); }
        return new WindowSummary(Range(HistoryDays), Range(CalendarDays), heat.Select(v => Math.Round(v, 4)).ToArray(), hours,
            streak, Unused(points, now), Hits(points, now), Reliability(points, events, now));
    }

    /// <summary>
    /// Readings grouped into real limit periods, oldest first: a period ends only when the limit renews
    /// (<see cref="UsageAnalytics.Renewed"/>). The stored period key also splits at sampling gaps and small reset-time
    /// drifts, which is right for pace estimates but would count one week's limit as several.
    /// </summary>
    public static List<UsagePoint[]> LimitPeriods(IReadOnlyList<UsagePoint> points)
    {
        var result = new List<UsagePoint[]>(); var current = new List<UsagePoint>();
        foreach (var p in points)
        {
            if (current.Count > 0 && UsageAnalytics.Renewed(current[^1], p)) { result.Add(current.ToArray()); current.Clear(); }
            current.Add(p);
        }
        if (current.Count > 0) result.Add(current.ToArray());
        return result;
    }

    /// <summary>The peak reading of each finished limit period (one that renewed, or whose reset passed), oldest first.</summary>
    public static IReadOnlyList<UnusedPeriod> Unused(IReadOnlyList<UsagePoint> points, DateTimeOffset now, int max = 12)
    {
        var result = new List<UnusedPeriod>();
        var periods = LimitPeriods(points);
        for (var i = 0; i < periods.Count; i++)
        {
            var g = periods[i]; var last = g[^1];
            var finished = i < periods.Count - 1 || last.Reset is { } r && r <= now;
            if (!finished || g.Length < 3) continue;
            var next = i < periods.Count - 1 ? periods[i + 1][0].At : now;
            result.Add(new UnusedPeriod(last.Reset is { } reset && reset <= next ? reset : last.At, Math.Clamp(g.Max(p => p.Used), 0, 1)));
        }
        return result.TakeLast(max).ToArray();
    }

    /// <summary>When a limit was reached (≥ 99.5%) in the last 30 days, once per limit period, with the reset it waited for.</summary>
    public static IReadOnlyList<LimitHit> Hits(IReadOnlyList<UsagePoint> points, DateTimeOffset now, int max = 20) =>
        LimitPeriods(points).Select(g => g.FirstOrDefault(p => p.Used >= .995)).Where(p => p is not null && p.At >= now.AddDays(-30))
            .Select(p => new LimitHit(p!.At, p.Reset)).OrderBy(h => h.At).TakeLast(max).ToArray();

    /// <summary>
    /// How often the provider kept its reported reset time. A move is a change of more than 2 minutes before the old reset
    /// arrived; confirmed and changed count the history events of the last 30 days.
    /// </summary>
    public static ResetReliability Reliability(IReadOnlyList<UsagePoint> points, IReadOnlyList<UsageEvent> events, DateTimeOffset now)
    {
        var recent = points.Where(p => p.At >= now.AddDays(-30)).ToArray();
        var limitPeriods = LimitPeriods(recent);
        var shifts = new List<double>(); var moved = 0;
        foreach (var g in limitPeriods)
        {
            var any = false;
            for (var i = 1; i < g.Length; i++)
            {
                if (g[i - 1].Reset is not { } before || g[i].Reset is not { } after) continue;
                if (Math.Abs((after - before).TotalMinutes) <= 2 || g[i].At >= before) continue;
                // A rolling reset that moves along while nothing is used (Codex before the window starts) isn't a move.
                if (g[i].Used < .005 && g[i - 1].Used < .005) continue;
                shifts.Add(Math.Abs((after - before).TotalMinutes)); any = true;
            }
            if (any) moved++;
        }
        var periods = limitPeriods.Count;
        var since = now.AddDays(-30);
        return new ResetReliability(periods, Math.Max(0, periods - moved), moved, shifts.Count == 0 ? 0 : Math.Round(shifts.Average(), 1),
            events.Count(e => e.At >= since && e.Kind == "Confirmed reset"), events.Count(e => e.At >= since && e.Kind == "Window changed"));
    }

    /// <summary>Unused capacity of the finished periods in a span, as an average fraction, or null with no finished period.</summary>
    public static double? AverageUnused(IReadOnlyList<UnusedPeriod> unused) => unused.Count == 0 ? null : unused.Average(u => 1 - u.Peak);

    /// <summary>
    /// All-time totals from a stream of readings, oldest first, using the same counting as the daily totals: rises between
    /// readings, usage after a renewal, and nothing for small rounding drops.
    /// </summary>
    public sealed class AllTimeCounter
    {
        private UsagePoint? _last;
        private DateTimeOffset? _since;
        private readonly Dictionary<DateTime, double> _days = new();
        private int _hits;
        private bool _hitThisPeriod;
        private double _total;

        public void Add(UsagePoint p)
        {
            if (!double.IsFinite(p.Used) || p.Used < 0 || p.Used > 1000) return;
            _since ??= p.At;
            var date = p.At.ToLocalTime().Date;
            var delta = _last is { } prev ? UsageAnalytics.Consumed(prev, p).Used : 0;
            _days[date] = _days.GetValueOrDefault(date) + delta;
            _total += delta;
            // One hit per real limit period, as in History.
            if (_last is { } before && UsageAnalytics.Renewed(before, p)) _hitThisPeriod = false;
            if (p.Used >= .995 && !_hitThisPeriod) { _hits++; _hitThisPeriod = true; }
            _last = p;
        }

        public AllTimeTotals? Result()
        {
            if (_since is not { } since) return null;
            var best = _days.Where(d => d.Value > .0001).OrderByDescending(d => d.Value).ThenBy(d => d.Key).Select(d => (DateTime?)d.Key).FirstOrDefault();
            return new AllTimeTotals(since, Math.Round(_total, 4), _days.Count(d => d.Value > .0001), _days.Count, _hits,
                best, best is { } b ? Math.Round(_days[b], 4) : 0);
        }
    }
}
