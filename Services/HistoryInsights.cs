namespace UsageNotch.Services;

public sealed record DayUsage(DateTime Date, double? Used);
public sealed record UnusedPeriod(DateTimeOffset End, double Peak);
public sealed record LimitHit(DateTimeOffset At, DateTimeOffset? Reset);
public sealed record ResetReliability(int Periods, int Fixed, int Moved, double AverageShiftMinutes, int Confirmed, int Changed);

/// <summary>Everything the History page and paired phones show for one limit window, computed from the local readings.</summary>
public sealed record WindowSummary(
    IReadOnlyList<DayUsage> Days, IReadOnlyList<DayUsage> Calendar, double[] Heat, int[] Observed, int Streak,
    IReadOnlyList<UnusedPeriod> Unused, IReadOnlyList<LimitHit> Hits, ResetReliability Reliability)
{
    public double? Total(int days) => Days.TakeLast(days).Where(d => d.Used is not null).Select(d => d.Used!.Value).DefaultIfEmpty().Sum() is var sum && Days.TakeLast(days).Any(d => d.Used is not null) ? sum : null;
    public int Recorded(int days) => Days.TakeLast(days).Count(d => d.Used is not null);
    public DayUsage? Busiest(int days) => Days.TakeLast(days).Where(d => d.Used is > 0).MaxBy(d => d.Used);
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
        var consumed = new Dictionary<DateTime, double>(); var observed = new HashSet<DateTime>();
        var heat = new double[168]; var hours = new int[168]; var seenHours = new HashSet<(DateTime, int)>();
        for (var i = 0; i < points.Count; i++)
        {
            var local = points[i].At.ToLocalTime(); var cell = (int)local.DayOfWeek * 24 + local.Hour;
            var recent = local.Date >= heatFrom;
            observed.Add(local.Date);
            if (recent && seenHours.Add((local.Date, local.Hour))) hours[cell]++;
            if (i == 0 || !UsageAnalytics.Continuous(points[i - 1], points[i])) continue;
            var delta = Math.Max(0, points[i].Used - points[i - 1].Used);
            consumed[local.Date] = consumed.GetValueOrDefault(local.Date) + delta;
            if (recent) heat[cell] += delta;
        }
        List<DayUsage> Range(int count) => Enumerable.Range(0, count).Select(i => today.AddDays(i - count + 1))
            .Select(d => new DayUsage(d, observed.Contains(d) ? Math.Round(consumed.GetValueOrDefault(d), 4) : null)).ToList();
        bool Used(DateTime d) => observed.Contains(d) && consumed.GetValueOrDefault(d) > .0001;
        var streak = 0; var cursor = Used(today) ? today : today.AddDays(-1);
        while (Used(cursor)) { streak++; cursor = cursor.AddDays(-1); }
        return new WindowSummary(Range(HistoryDays), Range(CalendarDays), heat.Select(v => Math.Round(v, 4)).ToArray(), hours,
            streak, Unused(points, now), Hits(points, now), Reliability(points, events, now));
    }

    /// <summary>The peak reading of each finished limit period (one with a later period, or a passed reset), oldest first.</summary>
    public static IReadOnlyList<UnusedPeriod> Unused(IReadOnlyList<UsagePoint> points, DateTimeOffset now, int max = 12)
    {
        var result = new List<UnusedPeriod>();
        var groups = points.GroupBy(p => p.Period).Select(g => g.ToArray()).OrderBy(g => g[0].At).ToArray();
        for (var i = 0; i < groups.Length; i++)
        {
            var g = groups[i]; var last = g[^1];
            var finished = i < groups.Length - 1 || last.Reset is { } r && r <= now;
            if (!finished || g.Length < 3) continue;
            result.Add(new UnusedPeriod(last.Reset is { } reset && reset <= now ? reset : last.At, Math.Clamp(g.Max(p => p.Used), 0, 1)));
        }
        return result.TakeLast(max).ToArray();
    }

    /// <summary>When a limit was reached (≥ 99.5%) in the last 30 days, with the reset it waited for.</summary>
    public static IReadOnlyList<LimitHit> Hits(IReadOnlyList<UsagePoint> points, DateTimeOffset now, int max = 20) =>
        points.Where(p => p.At >= now.AddDays(-30)).GroupBy(p => p.Period)
            .Select(g => g.FirstOrDefault(p => p.Used >= .995)).Where(p => p is not null)
            .Select(p => new LimitHit(p!.At, p.Reset)).OrderBy(h => h.At).TakeLast(max).ToArray();

    /// <summary>
    /// How often the provider kept its reported reset time. A move is a change of more than 2 minutes before the old reset
    /// arrived; confirmed and changed count the history events of the last 30 days.
    /// </summary>
    public static ResetReliability Reliability(IReadOnlyList<UsagePoint> points, IReadOnlyList<UsageEvent> events, DateTimeOffset now)
    {
        var recent = points.Where(p => p.At >= now.AddDays(-30)).ToArray();
        var periods = recent.Select(p => p.Period).Distinct().Count();
        var shifts = new List<double>();
        for (var i = 1; i < recent.Length; i++)
        {
            if (recent[i - 1].Reset is not { } before || recent[i].Reset is not { } after) continue;
            if (Math.Abs((after - before).TotalMinutes) <= 2 || recent[i].At >= before) continue;
            if (recent[i].Used < recent[i - 1].Used - .1) continue; // A real reset, not a moved one.
            shifts.Add(Math.Abs((after - before).TotalMinutes));
        }
        var since = now.AddDays(-30);
        return new ResetReliability(periods, Math.Max(0, periods - shifts.Count), shifts.Count, shifts.Count == 0 ? 0 : Math.Round(shifts.Average(), 1),
            events.Count(e => e.At >= since && e.Kind == "Confirmed reset"), events.Count(e => e.At >= since && e.Kind == "Window changed"));
    }

    /// <summary>Unused capacity of the finished periods in a span, as an average fraction, or null with no finished period.</summary>
    public static double? AverageUnused(IReadOnlyList<UnusedPeriod> unused) => unused.Count == 0 ? null : unused.Average(u => 1 - u.Peak);
}
