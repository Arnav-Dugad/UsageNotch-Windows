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
    public sealed record Forecast(string Summary, double? Rate, double? Projected, long? LimitAt, string Confidence);
    public sealed record Window(string Id, string Label, double Used, long At, long? Reset, List<Point> Points,
        string? Detail = null, double? UsedAmount = null, double? LimitAmount = null, string? Unit = null, Forecast? Forecast = null);
    /// <summary>A reported limit without a percentage, such as credits with no monthly cap. Older apps ignore it.</summary>
    public sealed record Extra(string Id, string Label, string? Detail, long? Reset, double? UsedAmount, double? LimitAmount, string? Unit);
    /// <summary>Usage consumed on one local date, as a fraction of the limit. Null when this PC recorded nothing that day.</summary>
    public sealed record Day(string Date, double? Used);
    /// <summary>
    /// 30 days of one window: daily consumption, a weekday x hour heatmap (index weekday*24+hour, Sunday first) of
    /// consumption, how many distinct hours were observed in each cell, and the current streak of days with usage.
    /// Consumption is the sum of increases between continuous readings; gaps and resets add nothing and are never guessed.
    /// </summary>
    public sealed record History(string Window, string Label, List<Day> Days, List<double> Heat, List<int> Observed, int Streak);
    public sealed record Provider(string Id, string Name, string Status, List<Window> Windows,
        string? StatusText = null, string? Account = null, long? UpdatedAt = null, string? ManageUrl = null,
        string? Session = null, string? Weekly = null, List<Extra>? Extras = null, List<History>? History = null);
    /// <summary>Internet sync details for a paired phone. Only ever sent over the pinned, authenticated local link.</summary>
    public sealed record RelayInfo(string Url, string Key);
    public sealed record Snapshot(int Schema, long GeneratedAt, List<Provider> Providers, string? Source = null, string? AppVersion = null,
        RelayInfo? Relay = null, string? RelayState = null);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public const int MaxProviders = 16, MaxWindows = 32, MaxPoints = 512, HistoryDays = 30;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, History Value)> HistoryCache = new();

    /// <summary>Builds from the running app's latest provider readings, with 24 hours of recorded history per window and, when asked, 30-day summaries.</summary>
    public static Snapshot FromLive(IReadOnlyList<ProviderSnapshot> live, AppSettings settings, UsageHistory history, string? database, int maxPoints = MaxPoints, bool summaries = true)
    {
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
            var windows = new List<Window>(); var extras = new List<Extra>();
            foreach (var w in snapshot.Windows.Take(MaxWindows))
            {
                var reset = w.ResetsAt?.ToUnixTimeMilliseconds();
                if (w.UsedFraction is not { } used || !double.IsFinite(used) || used < 0 || used > 1000)
                { extras.Add(new Extra(w.Id, w.Label, w.Detail, reset, Finite(w.UsedAmount), Finite(w.LimitAmount), w.Unit)); continue; }
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
                summary = new[] { session, weekly }.Where(x => x != null).Select(x => HistoryFor(history, snapshot.Id, account, x!, now)).Where(h => h != null).Select(h => h!).ToList();
            providers.Add(new Provider(snapshot.Id, snapshot.DisplayName, snapshot.Status.ToString(), windows,
                string.IsNullOrWhiteSpace(snapshot.StatusText) ? null : snapshot.StatusText,
                settings.ShowAccountNames && !string.IsNullOrWhiteSpace(snapshot.AccountName) ? snapshot.AccountName : null,
                snapshot.FetchedAt?.ToUnixTimeMilliseconds(), ManageUrl(snapshot.ManageUrl),
                session?.Id, weekly?.Id, extras.Count == 0 ? null : extras, summary is { Count: > 0 } ? summary : null));
        }
        return new Snapshot(1, now.ToUnixTimeMilliseconds(), providers, "windows-app", AppVersion());
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
        return new Forecast(f.Summary, f.PercentPerHour, f.ProjectedPercent, limitAt, f.Confidence);
    }

    private static History? HistoryFor(UsageHistory history, string provider, string account, LimitWindow window, DateTimeOffset now)
    {
        var key = provider + "|" + account + "|" + window.Id;
        if (HistoryCache.TryGetValue(key, out var cached) && now - cached.At < TimeSpan.FromMinutes(10)) return cached.Value with { Label = window.Label };
        IReadOnlyList<UsagePoint> points;
        lock (history) points = history.Read(provider, account, window.Id, now.AddDays(-HistoryDays - 1));
        if (points.Count == 0) return null;
        var value = Aggregate(window.Id, window.Label, points, now);
        HistoryCache[key] = (now, value);
        return value;
    }

    /// <summary>Daily consumption, weekday x hour heatmap and streak from raw readings, in this PC's local time.</summary>
    public static History Aggregate(string window, string label, IReadOnlyList<UsagePoint> points, DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        var consumed = new Dictionary<DateTime, double>(); var observed = new HashSet<DateTime>();
        var heat = new double[168]; var hours = new int[168]; var seenHours = new HashSet<(DateTime, int)>();
        for (var i = 0; i < points.Count; i++)
        {
            var local = points[i].At.ToLocalTime(); var cell = (int)local.DayOfWeek * 24 + local.Hour;
            observed.Add(local.Date);
            if (seenHours.Add((local.Date, local.Hour))) hours[cell]++;
            if (i == 0 || !UsageAnalytics.Continuous(points[i - 1], points[i])) continue;
            var delta = Math.Max(0, points[i].Used - points[i - 1].Used);
            consumed[local.Date] = consumed.GetValueOrDefault(local.Date) + delta;
            heat[cell] += delta;
        }
        var days = Enumerable.Range(0, HistoryDays).Select(i => today.AddDays(i - HistoryDays + 1))
            .Select(d => new Day(d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), observed.Contains(d) ? Math.Round(consumed.GetValueOrDefault(d), 4) : null)).ToList();
        bool Used(DateTime d) => observed.Contains(d) && consumed.GetValueOrDefault(d) > .0001;
        var streak = 0; var cursor = Used(today) ? today : today.AddDays(-1);
        while (Used(cursor)) { streak++; cursor = cursor.AddDays(-1); }
        return new History(window, label, days, heat.Select(v => Math.Round(v, 4)).ToList(), hours.ToList(), streak);
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
        "codex-primary" when provider == "codex" => "Primary window", "codex-secondary" when provider == "codex" => "Secondary window",
        _ when window.EndsWith("-primary") => window[..^8].Replace('_', ' ') + " · primary",
        _ when window.EndsWith("-secondary") => window[..^10].Replace('_', ' ') + " · secondary",
        _ => window.Replace('_', ' ').Replace('-', ' ')
    };
    public static string Serialize(Snapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);
}
