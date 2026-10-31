using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using UsageNotch.Models;

namespace UsageNotch.Services.Phone;

/// <summary>
/// What a paired phone receives. Schema 1 stays readable by Android 1.1 (every window has a numeric "used");
/// newer fields add the desktop's labels, amounts, status text and dock roles. Tokens, account identifiers
/// and history beyond 24 hours never leave the PC. The account display name is included only when the
/// dock itself shows account names.
/// </summary>
public static class PhoneSnapshot
{
    public sealed record Point(long At, double Used, string Period);
    /// <summary>The desktop's usage-pace estimate for a window. LimitAt is set when the limit would be reached before the reset.</summary>
    public sealed record Forecast(string Summary, double? Rate, double? Projected, long? LimitAt, string Confidence, double? LowRate = null, double? HighRate = null);
    public sealed record Window(string Id, string Label, double Used, long At, long? Reset, List<Point> Points,
        string? Detail = null, double? UsedAmount = null, double? LimitAmount = null, string? Unit = null, Forecast? Forecast = null);
    /// <summary>A reported limit without a percentage, such as credits with no monthly cap. Older apps ignore it.</summary>
    public sealed record Extra(string Id, string Label, string? Detail, long? Reset, double? UsedAmount, double? LimitAmount, string? Unit);
    /// <summary>Usage consumed on one local date, as a fraction of the limit. Null when this PC recorded nothing that day.</summary>
    public sealed record Day(string Date, double? Used);
    /// <summary>
    /// One window's history: 30 days of daily consumption, a weekday x hour heatmap of the last 30 days (index
    /// weekday*24+hour, Sunday first), how many distinct hours were observed in each cell, the current streak of days
    /// with usage, and 90 days of daily consumption for the calendar (a separate field, so Android 1.3 still reads Days).
    /// Consumption is the sum of increases between continuous readings; gaps and resets add nothing and are never guessed.
    /// </summary>
    public sealed record History(string Window, string Label, List<Day> Days, List<double> Heat, List<int> Observed, int Streak, List<Day>? Calendar = null,
        List<Point>? Trend = null, List<Unused>? Unused = null, List<Hit>? Hits = null, Reliability? Reliability = null, List<Event>? Events = null, List<Session>? Sessions = null);
    // Protocol 2 insights (docs/PHONE-PROTOCOL.md). Older phones ignore them.
    public sealed record Unused(long End, double Peak);
    public sealed record Hit(long At, long? Reset);
    public sealed record Reliability(int Periods, int Fixed, int Moved, double AverageShiftMinutes, int Confirmed, int Changed);
    public sealed record Event(long At, string Kind, string Text);
    public sealed record Session(long Start, long End, double Used, double Peak);
    public sealed record Provider(string Id, string Name, string Status, List<Window> Windows,
        string? StatusText = null, string? Account = null, long? UpdatedAt = null, string? ManageUrl = null,
        string? Session = null, string? Weekly = null, List<Extra>? Extras = null, List<History>? History = null,
        string? Kind = null, string? AccountLabel = null);
    public sealed record IncidentInfo(string Provider, string Title, string Impact, string Status, string? Url, long UpdatedAt);
    public sealed record Totals(long Input, long Output, long CacheRead, long CacheWrite, long Total, double? Cost);
    public sealed record TokenDay(string Date, long Total, double? Cost);
    public sealed record ShareInfo(string Name, long Total, double? Cost, double Share);
    public sealed record TokenSource(string Id, string Label, Totals Today, Totals Week, List<TokenDay> Days, List<ShareInfo> Projects, List<ShareInfo> Models);
    public sealed record TokenInfo(long UpdatedAt, List<TokenSource> Sources);
    /// <summary>What a snapshot carries beyond the dock: protocol 2's heavier fields, local tokens and status-page incidents.</summary>
    public sealed record Extras(bool Detailed, IReadOnlyList<TokenReport> Tokens, IReadOnlyList<Services.Incident> Incidents, bool ShareProjects, int TrendPoints = 400)
    {
        public static readonly Extras None = new(false, [], [], false);
    }
    public static readonly string[] Features = ["refresh", "settings", "trend", "insights", "tokens", "incidents", "accounts"];
    /// <summary>Internet sync details for a paired phone. Only ever sent over the pinned, authenticated local link.</summary>
    public sealed record RelayInfo(string Url, string Key);
    public sealed record Snapshot(int Schema, long GeneratedAt, List<Provider> Providers, string? Source = null, string? AppVersion = null,
        RelayInfo? Relay = null, string? RelayState = null, int? Protocol = null, string[]? Features = null,
        System.Text.Json.Nodes.JsonObject? Shared = null, List<IncidentInfo>? Incidents = null, TokenInfo? Tokens = null);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public const int MaxProviders = 16, MaxWindows = 32, MaxPoints = 512, HistoryDays = 30, CalendarDays = 90;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, History Value)> HistoryCache = new();

    /// <summary>Builds from the running app's latest provider readings, with 24 hours of recorded history per window and, when asked, 30-day summaries.</summary>
    public static Snapshot FromLive(IReadOnlyList<ProviderSnapshot> live, AppSettings settings, UsageHistory history, string? database, int maxPoints = MaxPoints, bool summaries = true, Extras? extras = null)
    {
        extras ??= Extras.None;
        var now = DateTimeOffset.UtcNow;
        var fallback = new Lazy<Snapshot?>(() => { try { return database == null ? null : FromDatabase(database, maxPoints); } catch { return null; } });
        var providers = new List<Provider>();
        foreach (var snapshot in live.Take(MaxProviders))
        {
            // Before the first reading after launch, show what was last recorded rather than an empty card.
            if (snapshot.Status == SnapshotStatus.Loading && snapshot.Windows.Count == 0)
            {
                if (fallback.Value?.Providers.FirstOrDefault(p => p.Id == snapshot.Id) is { } saved) providers.Add(saved with { Status = "Stale", Name = snapshot.DisplayName });
                continue;
            }
            var view = new ProviderViewModel(snapshot, settings);
            var at = (snapshot.FetchedAt ?? now).ToUnixTimeMilliseconds();
            string account;
            lock (history) account = history.AccountKey(snapshot);
            var windows = new List<Window>(); var unlimited = new List<Extra>();
            foreach (var w in snapshot.Windows.Take(MaxWindows))
            {
                var reset = w.ResetsAt?.ToUnixTimeMilliseconds();
                if (w.UsedFraction is not { } used || !double.IsFinite(used) || used < 0 || used > 1000)
                { unlimited.Add(new Extra(w.Id, w.Label, w.Detail, reset, Finite(w.UsedAmount), Finite(w.LimitAmount), w.Unit)); continue; }
                IReadOnlyList<UsagePoint> raw;
                lock (history) raw = history.Read(snapshot.Id, account, w.Id, DateTimeOffset.FromUnixTimeMilliseconds(at).AddHours(-24));
                var points = raw.Where(p => double.IsFinite(p.Used) && p.Used >= 0 && p.Used <= 1000).Select(p => new Point(p.At.ToUnixTimeMilliseconds(), p.Used, p.Period)).ToList();
                Thin(points, Math.Min(maxPoints, MaxPoints));
                windows.Add(new Window(w.Id, w.Label, used, at, reset, points, w.Detail, Finite(w.UsedAmount), Finite(w.LimitAmount), w.Unit,
                    summaries ? ForecastFor(raw, snapshot.Status, now, settings) : null));
            }
            var session = view.SessionWindow is { UsedFraction: not null } s ? s : null;
            var weekly = view.WeeklyWindow is { UsedFraction: not null } wk && wk.Id != session?.Id ? wk : null;
            List<History>? summary = null;
            if (summaries)
                summary = new[] { session, weekly }.Where(x => x != null).Select(x => HistoryFor(history, snapshot.Id, account, x!, now, extras)).Where(h => h != null).Select(h => h!).ToList();
            providers.Add(new Provider(snapshot.Id, snapshot.DisplayName, snapshot.Status.ToString(), windows,
                string.IsNullOrWhiteSpace(snapshot.StatusText) ? null : snapshot.StatusText,
                settings.ShowAccountNames && !string.IsNullOrWhiteSpace(snapshot.AccountName) ? snapshot.AccountName : null,
                snapshot.FetchedAt?.ToUnixTimeMilliseconds(), ManageUrl(snapshot.ManageUrl),
                session?.Id, weekly?.Id, unlimited.Count == 0 ? null : unlimited, summary is { Count: > 0 } ? summary : null,
                ProviderKind.Of(snapshot.Id), settings.Accounts.FirstOrDefault(a => a.ProviderId == snapshot.Id) is { } extra ? (string.IsNullOrWhiteSpace(extra.Label) ? extra.Slug : extra.Label.Trim()) : null));
        }
        return new Snapshot(1, now.ToUnixTimeMilliseconds(), providers, "windows-app", AppVersion(), Protocol: 2, Features: Features,
            Shared: SharedSettings.ToJson(settings),
            Incidents: extras.Incidents.Count == 0 ? null : extras.Incidents.Take(8).Select(i => new IncidentInfo(i.Provider, i.Title, i.Impact, i.Status, i.Url, i.UpdatedAt.ToUnixTimeMilliseconds())).ToList(),
            Tokens: extras.Tokens.Count == 0 ? null : new TokenInfo(now.ToUnixTimeMilliseconds(), extras.Tokens.Select(r => TokensFor(r, extras.ShareProjects)).ToList()));
    }

    private static TokenSource TokensFor(TokenReport r, bool projects)
    {
        static Totals T(TokenTotals t) => new(t.Input, t.Output, t.CacheRead, t.CacheWrite, t.Total, t.Cost is { } c ? Math.Round(c, 4) : null);
        static List<ShareInfo> S(IEnumerable<TokenShare> list) => list.Select(x => new ShareInfo(x.Name, x.Total, x.Cost is { } c ? Math.Round(c, 4) : null, Math.Round(x.Share, 4))).ToList();
        return new TokenSource(r.Source, r.Label, T(r.Today), T(r.Week),
            r.Days.Select(d => new TokenDay(d.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), d.Total, d.Cost is { } c ? Math.Round(c, 4) : null)).ToList(),
            projects ? S(r.Projects) : [], S(r.Models));
    }

    /// <summary>The dock's own forecast, with the time the limit would be reached when that comes before the reset.</summary>
    public static Forecast? ForecastFor(IReadOnlyList<UsagePoint> raw, SnapshotStatus status, DateTimeOffset now, AppSettings settings)
    {
        if (raw.Count == 0) return null;
        var f = UsageForecast.Calculate(raw, status, now, settings);
        long? limitAt = null;
        var last = raw[^1];
        if (f.PercentPerHour is > .0001 and var rate && last.Used < 1 && last.Reset is { } reset)
        {
            var eta = last.At.AddHours((1 - last.Used) / (rate / 100));
            if (eta < reset) limitAt = eta.ToUnixTimeMilliseconds();
        }
        // Low and high rates (percentage points an hour) are the 10th–90th percentile observed paces: the explorer's band.
        return new Forecast(f.Summary, f.PercentPerHour, f.ProjectedPercent, limitAt, f.Confidence, f.LowRate is { } low ? Math.Round(low * 100, 3) : null, f.HighRate is { } high ? Math.Round(high * 100, 3) : null);
    }

    private static History? HistoryFor(UsageHistory history, string provider, string account, LimitWindow window, DateTimeOffset now, Extras extras)
    {
        var key = provider + "|" + account + "|" + window.Id + "|" + extras.Detailed + extras.TrendPoints;
        if (HistoryCache.TryGetValue(key, out var cached) && now - cached.At < TimeSpan.FromMinutes(2)) return cached.Value with { Label = window.Label };
        IReadOnlyList<UsagePoint> points; IReadOnlyList<UsageEvent> events;
        lock (history)
        {
            points = history.Read(provider, account, window.Id, now.AddDays(-CalendarDays - 1));
            events = history.ReadEvents(provider, account, window.Id, now.AddDays(-30));
        }
        if (points.Count == 0) return null;
        var summary = HistoryInsights.Summarize(points, events, now);
        var value = new History(window.Id, window.Label, Days(summary.Days), summary.Heat.ToList(), summary.Observed.ToList(), summary.Streak, Days(summary.Calendar),
            Unused: summary.Unused.Select(u => new Unused(u.End.ToUnixTimeMilliseconds(), Math.Round(u.Peak, 4))).ToList(),
            Hits: summary.Hits.Select(h => new Hit(h.At.ToUnixTimeMilliseconds(), h.Reset?.ToUnixTimeMilliseconds())).ToList(),
            Reliability: new Reliability(summary.Reliability.Periods, summary.Reliability.Fixed, summary.Reliability.Moved, summary.Reliability.AverageShiftMinutes, summary.Reliability.Confirmed, summary.Reliability.Changed));
        if (extras.Detailed)
        {
            var week = points.Where(p => p.At >= now.AddDays(-7)).ToList();
            var trend = week.Where(p => double.IsFinite(p.Used) && p.Used is >= 0 and <= 1000).Select(p => new Point(p.At.ToUnixTimeMilliseconds(), Math.Round(p.Used, 4), p.Period)).ToList();
            Thin(trend, extras.TrendPoints);
            value = value with
            {
                Trend = trend,
                Events = events.Where(e => e.At >= now.AddDays(-7)).TakeLast(30).Select(e => new Event(e.At.ToUnixTimeMilliseconds(), e.Kind, e.Explanation.Length > 240 ? e.Explanation[..240] : e.Explanation)).ToList(),
                Sessions = UsageAnalytics.Sessions(week, events).Select(x => new Session(x.Start.ToUnixTimeMilliseconds(), x.End.ToUnixTimeMilliseconds(), Math.Round(x.Consumption, 2), Math.Round(x.PeakPace, 2))).ToList(),
            };
        }
        HistoryCache[key] = (now, value);
        return value;
    }

    private static List<Day> Days(IEnumerable<DayUsage> days) => days.Select(d => new Day(d.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), d.Used is { } u ? Math.Round(u, 4) : null)).ToList();

    /// <summary>Daily consumption (30 and 90 days), a 30-day weekday x hour heatmap and the streak from raw readings, in this PC's local time.</summary>
    public static History Aggregate(string window, string label, IReadOnlyList<UsagePoint> points, DateTimeOffset now)
    {
        var summary = HistoryInsights.Summarize(points, [], now);
        return new History(window, label, Days(summary.Days), summary.Heat.ToList(), summary.Observed.ToList(), summary.Streak, Days(summary.Calendar));
    }

    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
    private static string? ManageUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    private static string AppVersion() => typeof(PhoneSnapshot).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>Keeps the day's shape within a smaller payload: evenly spaced readings plus the latest one.</summary>
    public static void Thin(List<Point> points, int max)
    {
        if (max < 2 || points.Count <= max) return;
        var kept = Enumerable.Range(0, max - 1).Select(i => points[(int)((long)i * (points.Count - 1) / (max - 1))]).Append(points[^1]).Distinct().ToList();
        points.Clear(); points.AddRange(kept);
    }

    /// <summary>Latest recorded batch per provider, read-only, for the moments before the first live reading.</summary>
    public static Snapshot FromDatabase(string path, int maxPoints = MaxPoints)
    {
        if (!File.Exists(path)) throw new IOException("No history yet");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 3 }.ToString()); db.Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var providers = new List<Provider>(); var states = new List<(string Id, string Account, string Status)>();
        using (var cmd = db.CreateCommand()) { cmd.Transaction = transaction; cmd.CommandText = "SELECT provider,account,status FROM provider_state ORDER BY CASE provider WHEN 'claude' THEN 0 WHEN 'codex' THEN 1 ELSE 2 END,provider LIMIT 16";
            using var r = cmd.ExecuteReader(); while (r.Read()) states.Add((r.GetString(0), r.GetString(1), r.GetString(2))); }
        foreach (var state in states)
        {
            var windows = new List<Window>();
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT window,used,at,reset FROM observations WHERE provider=$p AND account=$a AND at=(SELECT MAX(at) FROM observations WHERE provider=$p AND account=$a) ORDER BY CASE WHEN window='five_hour' OR window LIKE '%-primary' THEN 0 WHEN window='seven_day' OR window LIKE '%-secondary' THEN 1 ELSE 2 END,window LIMIT 32";
                cmd.Parameters.AddWithValue("$p", state.Id); cmd.Parameters.AddWithValue("$a", state.Account);
                using var r = cmd.ExecuteReader(); while (r.Read()) { var used = r.GetDouble(1); if (double.IsFinite(used) && used >= 0 && used <= 1000) windows.Add(new Window(r.GetString(0), Label(state.Id, r.GetString(0)), used, r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), [])); }
            }
            foreach (var w in windows)
            {
                using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
                cmd.CommandText = "SELECT at,used,period FROM observations WHERE provider=$p AND account=$a AND window=$w AND at >= $since ORDER BY at DESC LIMIT 512";
                cmd.Parameters.AddWithValue("$p", state.Id); cmd.Parameters.AddWithValue("$a", state.Account); cmd.Parameters.AddWithValue("$w", w.Id); cmd.Parameters.AddWithValue("$since", w.At - 86400000);
                using var r = cmd.ExecuteReader(); while (r.Read()) { var value = r.GetDouble(1); if (double.IsFinite(value) && value >= 0 && value <= 1000) w.Points.Add(new Point(r.GetInt64(0), value, r.GetString(2))); }
                w.Points.Reverse(); Thin(w.Points, maxPoints);
            }
            providers.Add(new Provider(state.Id, Name(state.Id), state.Status, windows));
        }
        transaction.Commit();
        return new Snapshot(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), providers, "windows-app", AppVersion());
    }
    public static string Name(string id) => id switch { "claude" => "Claude", "codex" => "Codex", "gemini" => "Gemini", "cursor" => "Cursor", "openai_api" or "openai-api" => "OpenAI API", "anthropic_api" or "anthropic-api" => "Claude API", _ => id };
    public static string Label(string provider, string window) => window switch
    {
        "five_hour" => "Current session", "seven_day" => "All models", "seven_day_opus" => "Opus weekly", "seven_day_sonnet" => "Sonnet weekly", "extra_usage" => "Usage credits",
        "month_cost" => "This month's API spend", "on_demand" => "On demand",
        "codex-primary" when ProviderKind.Of(provider) == "codex" => "Primary window", "codex-secondary" when ProviderKind.Of(provider) == "codex" => "Secondary window",
        _ when window.EndsWith("-primary") => window[..^8].Replace('_', ' ') + " · primary",
        _ when window.EndsWith("-secondary") => window[..^10].Replace('_', ' ') + " · secondary",
        _ => window.Replace('_', ' ').Replace('-', ' ')
    };
    public static string Serialize(Snapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);
}
