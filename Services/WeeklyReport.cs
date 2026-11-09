using System.Globalization;
using UsageNotch.Models;

namespace UsageNotch.Services;

/// <summary>One provider's main limit over the last 7 local days, against the 7 days before.</summary>
public sealed record ReportProvider(string Id, string Name, string Window, string Label, double? Used, double? Previous, double? Change,
    int ActiveDays, int Hits, DateTime? BusiestDay, double? BusiestUsed, int Streak, double? Typical = null, double? TypicalChange = null);

/// <summary>What a token source's numbers mean against your limits and plan: tokens per full main limit this week, and this month's API-price equivalent against the plan's price.</summary>
public sealed record TokenFacts(double? PerLimit, string? LimitName, double? MonthCost, string? PlanName, double? PlanPrice);

/// <summary>A plain-language sentence about the week. Kind is change, hits, busiest, hours, unused, streak or tokens.</summary>
public sealed record ReportInsight(string Kind, string? Provider, string Text);

/// <summary>"This week": the last 7 local days, ending today, compared with the 7 days before.</summary>
public sealed record WeekReport(DateTime From, DateTime To, string Headline, string Summary, IReadOnlyList<ReportProvider> Providers, IReadOnlyList<ReportInsight> Insights,
    double Coverage = 1, string? Note = null, IReadOnlyList<DayNote>? Notes = null);

/// <summary>
/// Builds the weekly report from the same daily totals History shows, so every number in a sentence can be found on the
/// History page. A comparison is only made when both weeks have at least 3 days with readings and the earlier week used at
/// least 5% of a limit; otherwise the change is left out rather than guessed.
/// </summary>
public static class WeeklyReport
{
    public const int MaxInsights = 8;
    public static readonly string[] Order = ["change", "typical", "hits", "busiest", "unusual", "hours", "unused", "streak", "efficiency", "value", "tokens"];

    /// <summary>Tokens per full main limit this week (from half a limit used), and the month's API-price equivalent against the plan.</summary>
    public static TokenFacts Facts(TokenReport t, ProviderViewModel? p, double? weekUsed, string? limitLabel)
    {
        double? perLimit = weekUsed is >= .5 && t.Week.Total > 0 ? Math.Round(t.Week.Total / weekUsed.Value) : null;
        var price = p is null ? null : PlanPrices.For(p.Kind, p.Plan);
        return new TokenFacts(perLimit, perLimit is null || limitLabel is null ? null : Lower(limitLabel), t.Month?.Cost is { } c ? Math.Round(c, 2) : null, price is null ? null : p!.Plan, price);
    }

    public static WeekReport? Build(IReadOnlyList<ProviderViewModel> providers, Func<ProviderViewModel, LimitWindow, WindowSummary?> summaryFor,
        IReadOnlyList<TokenReport> tokens, bool projects, DateTimeOffset now, bool use24, IReadOnlyList<DayNote>? notes = null)
    {
        // English sentences with the same digits and day names on every PC and phone.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return BuildCore(providers, summaryFor, tokens, projects, now, use24, notes ?? []); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static WeekReport? BuildCore(IReadOnlyList<ProviderViewModel> providers, Func<ProviderViewModel, LimitWindow, WindowSummary?> summaryFor,
        IReadOnlyList<TokenReport> tokens, bool projects, DateTimeOffset now, bool use24, IReadOnlyList<DayNote> notes)
    {
        var today = now.ToLocalTime().Date; var from = today.AddDays(-6); var previousFrom = from.AddDays(-7);
        var rows = new List<ReportProvider>(); var insights = new List<ReportInsight>();
        var summaries = new List<(ProviderViewModel P, LimitWindow W, WindowSummary S)>();
        var activeDates = new HashSet<DateTime>();
        foreach (var p in providers)
        {
            var w = p.SessionWindow is { UsedFraction: not null } session ? session : p.WeeklyWindow is { UsedFraction: not null } weekly ? weekly : null;
            if (w is null || summaryFor(p, w) is not { } s) continue;
            summaries.Add((p, w, s));
            var week = s.Calendar.Where(d => d.Date >= from && d.Date <= today).ToList();
            var before = s.Calendar.Where(d => d.Date >= previousFrom && d.Date < from).ToList();
            var used = Sum(week); var previous = Sum(before);
            double? change = used is { } u && previous is { } pv && pv >= .05 && Recorded(week) >= 3 && Recorded(before) >= 3 ? (u - pv) / pv : null;
            foreach (var d in week.Where(d => d.Used is > .0001)) activeDates.Add(d.Date);
            var busiest = week.Where(d => d.Used is > .0001).MaxBy(d => d.Used);
            var typical = HistoryInsights.TypicalWeek(s.Calendar, from);
            double? typicalChange = used is { } tu && typical is >= .05 ? (tu - typical.Value) / typical.Value : null;
            rows.Add(new ReportProvider(p.Id, p.DisplayName, w.Id, w.Label, Round(used), Round(previous), change is { } c ? Math.Round(c, 4) : null,
                week.Count(d => d.Used is > .0001), s.Hits.Count(h => h.At.ToLocalTime().Date >= from), busiest?.Date, Round(busiest?.Used), s.Streak,
                typical, typicalChange is { } t ? Math.Round(t, 4) : null));
        }
        if (rows.Count == 0) return null;

        // 1. How the week compares with the one before, and with your typical week: one sentence per provider.
        static string Typical(double t) => t >= 1 ? $"{t + 1:0.#}× your typical week" : t >= .1 ? $"{Math.Round(t * 100):0}% above your typical week"
            : t <= -.1 ? $"{Math.Round(-t * 100):0}% below your typical week" : "close to your typical week";
        foreach (var r in rows.Where(r => r.Change is not null))
        {
            var c = r.Change!.Value;
            var typical = r.TypicalChange is { } tc && r.Used is >= .005 ? $" ({Typical(tc)})" : "";
            insights.Add(new("change", r.Id, r.Used < .005 ? $"You didn't use {r.Name} this week."
                : c >= 3 ? $"You used {r.Name} {c + 1:0.#}× as much as the week before{typical}."
                : c >= .1 ? $"You used {r.Name} {Math.Round(c * 100):0}% more than the week before{typical}."
                : c <= -.1 ? $"You used {r.Name} {Math.Round(-c * 100):0}% less than the week before{typical}."
                : $"Your {r.Name} use was steady, within 10% of the week before{typical}."));
        }
        // 1b. Against your typical week, when there's no comparison with the week before.
        foreach (var r in rows.Where(r => r.Change is null && r.TypicalChange is not null && r.Used is >= .005))
            insights.Add(new("typical", r.Id, $"Your {r.Name} week was {Typical(r.TypicalChange!.Value)}."));
        // 2. Limits reached.
        foreach (var r in rows.Where(r => r.Hits > 0))
            insights.Add(new("hits", r.Id, $"You reached the {r.Name} {Lower(r.Label)} limit {Times(r.Hits)} this week."));
        if (rows.All(r => r.Hits == 0) && rows.Any(r => r.ActiveDays > 0))
            insights.Add(new("hits", null, "You never hit a limit this week."));
        // 3. The busiest day.
        if (rows.Where(r => r.BusiestUsed is > .0001).MaxBy(r => r.BusiestUsed) is { BusiestDay: { } day, BusiestUsed: { } most } top)
            insights.Add(new("busiest", top.Id, most >= 1.05
                ? $"{day:dddd} was your busiest day, with {most:0.#} full {top.Name} {Lower(top.Label)} limits."
                : most >= .995 ? $"{day:dddd} was your busiest day, using a full {top.Name} {Lower(top.Label)} limit."
                : $"{day:dddd} was your busiest day, using {Math.Round(most * 100):0}% of a {top.Name} {Lower(top.Label)} limit."));
        // 3b. The most unusual day for its weekday.
        var unusual = summaries.SelectMany(x => HistoryInsights.Unusual(x.S.Calendar, 7).Where(u => u.Date >= from).Select(u => (x.P, U: u))).OrderByDescending(x => x.U.Ratio).FirstOrDefault();
        if (unusual.P is not null)
        {
            var dayName = unusual.U.Date.ToString("dddd");
            insights.Add(new("unusual", unusual.P.Id, unusual.U.Ratio is >= 1.9 and < 2.15
                ? $"{dayName} was about twice your normal {dayName} for {unusual.P.DisplayName}."
                : $"{dayName} was {unusual.U.Ratio:0.#}× your normal {dayName} for {unusual.P.DisplayName}."));
        }
        // 4. When you usually work.
        if (summaries.Select(x => (x.P, Hours: x.S.BusiestHours(1))).FirstOrDefault(x => x.Hours.Count > 0) is { P: not null } busy)
        {
            var (weekday, hour, _) = busy.Hours[0];
            insights.Add(new("hours", busy.P.Id, $"You're usually busiest on {(DayOfWeek)weekday}s around {BudgetSetting.Clock(hour * 60, use24)}."));
        }
        // 5. What the last weekly limit left behind.
        foreach (var p in summaries.Select(x => x.P))
        {
            if (p.WeeklyWindow is not { UsedFraction: not null } weekly || weekly.Id == p.SessionWindow?.Id || summaryFor(p, weekly) is not { } ws) continue;
            if (ws.Unused.LastOrDefault() is not { } last || last.End.ToLocalTime().Date < from) continue;
            var unused = 1 - last.Peak;
            if (unused >= .995) insights.Add(new("unused", p.Id, $"Your last {p.DisplayName} weekly limit renewed unused."));
            else if (unused >= .2) insights.Add(new("unused", p.Id, $"Your last {p.DisplayName} weekly limit renewed with {Math.Round(unused * 100):0}% unused."));
            else if (unused <= .05) insights.Add(new("unused", p.Id, $"You used nearly all of your last {p.DisplayName} weekly limit."));
        }
        // 6. Long streaks.
        foreach (var r in rows.Where(r => r.Streak >= 7))
            insights.Add(new("streak", r.Id, $"{r.Streak} days in a row with {r.Name}."));
        // 6b. Tokens per limit, and the month against the plan's price.
        foreach (var t in tokens)
        {
            var p = providers.FirstOrDefault(x => x.Id == t.Source); var row = rows.FirstOrDefault(r => r.Id == t.Source);
            var facts = Facts(t, p, row?.Used, row?.Label);
            if (facts.PerLimit is { } per)
                insights.Add(new("efficiency", t.Source, $"{t.Label} got about {Tokens((long)per)} tokens from each {facts.LimitName} limit this week."));
            if (facts.MonthCost is { } cost && cost >= 1 && facts.PlanPrice is { } price)
                insights.Add(new("value", t.Source, $"This month, {t.Label}'s tokens would cost about {Money(cost)} at API prices. Your {facts.PlanName} plan is {Money(price)} a month."));
        }
        // 7. Tokens from this PC (or the whole account for Codex).
        foreach (var t in tokens.Where(t => t.Week.Total > 0))
        {
            var text = $"{t.Label} used {Tokens(t.Week.Total)} tokens this week";
            if (t.Week.Cost is { } cost && cost >= .01) text += $" (about {Money(cost)} at API prices)";
            if (projects && t.Projects.FirstOrDefault() is { } project && t.Projects.Count > 1) text += $", most of them in {project.Name}";
            insights.Add(new("tokens", t.Source, text + "."));
        }

        // The headline weighs each provider by how much it was used, so a barely used one can't outvote your main one.
        var compared = rows.Where(r => r.Change is not null).ToList();
        var overall = compared.Count == 0 ? (double?)null : compared.Sum(r => r.Used ?? 0) / compared.Sum(r => r.Previous!.Value) - 1;
        var headline = overall is not { } weekChange
            ? rows.All(r => r.Previous is null) ? "Your first week of history" : "Your week"
            : weekChange switch { > .1 => "A busier week than the last", < -.1 => "A quieter week than the last", _ => "A steady week" };
        var active = activeDates.Count;
        var summary = $"{from.ToString("MMM d", CultureInfo.InvariantCulture)} – {today.ToString("MMM d", CultureInfo.InvariantCulture)} · {active} active day{(active == 1 ? "" : "s")}";
        var coverage = summaries.Count == 0 ? 1 : Math.Round(summaries.Average(x => x.S.Coverage(7)), 3);
        var note = coverage < .5 ? $"This PC saw {Math.Round(coverage * 100):0}% of this week, so which day and hour things happened is less certain. Totals are still complete." : null;
        var weekNotes = notes.Where(n => !n.Deleted && n.Date >= from && n.Date <= today).OrderBy(n => n.Date).ToArray();
        var ordered = insights.OrderBy(i => Array.IndexOf(Order, i.Kind)).Take(MaxInsights).ToArray();
        return new WeekReport(from, today, headline, summary, rows, ordered, coverage, note, weekNotes);
    }

    private static double? Sum(List<DayUsage> days) => days.Any(d => d.Used is not null) ? days.Sum(d => d.Used ?? 0) : null;
    private static int Recorded(List<DayUsage> days) => days.Count(d => d.Used is not null);
    private static double? Round(double? v) => v is { } x ? Math.Round(x, 4) : null;
    /// <summary>The limit's name as it reads in a sentence: "session", "weekly", "5-hour", "monthly".</summary>
    public static string Lower(string label)
    {
        var name = label.Trim() switch { "Current session" => "session", "All models" => "weekly", var other => other };
        foreach (var suffix in new[] { " window", " limit" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        name = name.Replace(" · ", " ");
        return name.Length == 0 ? "usage" : char.IsUpper(name[0]) && (name.Length == 1 || !char.IsUpper(name[1])) ? char.ToLowerInvariant(name[0]) + name[1..] : name;
    }
    private static string Times(int n) => n switch { 1 => "once", 2 => "twice", _ => $"{n} times" };
    public static string Tokens(long n) => n switch { >= 1_000_000_000 => $"{n / 1e9:0.##}B", >= 1_000_000 => $"{n / 1e6:0.#}M", >= 10_000 => $"{n / 1e3:0}K", >= 1000 => $"{n / 1e3:0.#}K", _ => n.ToString("N0", CultureInfo.InvariantCulture) };
    public static string Money(double usd) => usd >= 100 ? $"${usd.ToString("N0", CultureInfo.InvariantCulture)}" : usd == Math.Floor(usd) ? $"${usd.ToString("0", CultureInfo.InvariantCulture)}" : $"${usd.ToString("0.00", CultureInfo.InvariantCulture)}";
}
