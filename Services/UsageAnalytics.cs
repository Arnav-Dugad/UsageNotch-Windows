namespace UsageNotch.Services;

public sealed record UsageInsight(string Text, DateTimeOffset Start, DateTimeOffset End);
public sealed record ObservedSession(DateTimeOffset Start, DateTimeOffset End, double Consumption, double PeakPace, int Resets, int Missing);
public sealed record PatternCell(string Label, double? Pace, double CoveredHours, int Days);

public static class UsageAnalytics
{
    public static bool Continuous(UsagePoint a, UsagePoint b) => a.Period == b.Period && b.At > a.At && b.At - a.At <= TimeSpan.FromMinutes(20) && b.Used >= a.Used - .0001;
    public static IReadOnlyList<ObservedSession> Sessions(IReadOnlyList<UsagePoint> points, IReadOnlyList<UsageEvent> events)
    {
        var result = new List<ObservedSession>();
        if (points.Count == 0) return result;
        var start = points[0].At; double consumption = 0, peak = 0; int missing = 0;
        void Finish(DateTimeOffset end) => result.Add(new(start, end, consumption * 100, peak * 100,
            events.Count(e => e.Kind == "Confirmed reset" && e.At >= start && e.At <= end), missing));
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i];
            if (b.At - a.At > TimeSpan.FromMinutes(20))
            { missing++; Finish(a.At); start = b.At; consumption = 0; peak = 0; missing = 0; continue; }
            if (Continuous(a, b))
            {
                consumption += Math.Max(0, b.Used - a.Used);
                peak = Math.Max(peak, Math.Max(0, b.Used - a.Used) / (b.At - a.At).TotalHours);
            }
            else missing++;
        }
        Finish(points[^1].At);
        return result.TakeLast(6).Reverse().ToArray();
    }
    public static IReadOnlyList<UsageInsight> Insights(IReadOnlyList<UsagePoint> points, DateTimeOffset now)
    {
        var result = new List<UsageInsight>();
        (double rate, double coverage) Rate(DateTimeOffset start, DateTimeOffset end)
        {
            double used = 0, hours = 0;
            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i];
                if (a.At < start || b.At > end || !Continuous(a, b)) continue;
                used += Math.Max(0, b.Used - a.Used); hours += (b.At - a.At).TotalHours;
            }
            return (hours > 0 ? used / hours : 0, hours / (end - start).TotalHours);
        }
        var recent = Rate(now.AddHours(-1), now); var before = Rate(now.AddHours(-2), now.AddHours(-1));
        if (recent.coverage >= .75 && before.coverage >= .75 && before.rate > .0001)
            result.Add(new($"Observed pace was {recent.rate / before.rate:0.0}× the preceding hour ({recent.rate * 100:0.0} vs {before.rate * 100:0.0} pp/h). Both hours ≥75% covered.", now.AddHours(-2), now));
        if (points.Count > 1)
        {
            var gaps = Enumerable.Range(1, points.Count - 1).Count(i => points[i].At - points[i - 1].At > TimeSpan.FromMinutes(20));
            if (gaps > 0) result.Add(new($"{gaps} sampling gap(s) in this interval. Missing time is excluded from pace calculations.", points[0].At, points[^1].At));
        }
        return result;
    }
    public static IReadOnlyList<PatternCell> Patterns(IReadOnlyList<UsagePoint> points, bool weekdays)
    {
        int count = weekdays ? 7 : 24;
        var hours = new double[count]; var used = new double[count];
        var days = Enumerable.Range(0, count).Select(_ => new HashSet<DateOnly>()).ToArray();
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i]; if (!Continuous(a, b)) continue;
            var total = (b.At - a.At).TotalHours; var cursor = a.At;
            while (cursor < b.At)
            {
                var local = cursor.ToLocalTime();
                var next = cursor.AddMinutes(60 - local.Minute).AddSeconds(-local.Second).AddMilliseconds(-local.Millisecond);
                if (next <= cursor) next = cursor.AddHours(1);
                if (next > b.At) next = b.At;
                var duration = (next - cursor).TotalHours;
                var index = weekdays ? (int)local.DayOfWeek : local.Hour;
                hours[index] += duration; used[index] += Math.Max(0, b.Used - a.Used) * duration / total;
                days[index].Add(DateOnly.FromDateTime(local.Date)); cursor = next;
            }
        }
        var enoughHistory = points.Select(p => DateOnly.FromDateTime(p.At.ToLocalTime().Date)).Distinct().Count() >= 7;
        return Enumerable.Range(0, count).Select(i => new PatternCell(weekdays ? ((DayOfWeek)i).ToString()[..3] : $"{i:00}:00",
            enoughHistory && days[i].Count >= (weekdays ? 2 : 3) && hours[i] >= 1 ? used[i] / hours[i] * 100 : null, hours[i], days[i].Count)).ToArray();
    }
}
