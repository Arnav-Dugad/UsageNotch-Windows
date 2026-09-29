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
    public sealed record Window(string Id, string Label, double Used, long At, long? Reset, List<Point> Points,
        string? Detail = null, double? UsedAmount = null, double? LimitAmount = null, string? Unit = null);
    /// <summary>A reported limit without a percentage, such as credits with no monthly cap. Older apps ignore it.</summary>
    public sealed record Extra(string Id, string Label, string? Detail, long? Reset, double? UsedAmount, double? LimitAmount, string? Unit);
    public sealed record Provider(string Id, string Name, string Status, List<Window> Windows,
        string? StatusText = null, string? Account = null, long? UpdatedAt = null, string? ManageUrl = null,
        string? Session = null, string? Weekly = null, List<Extra>? Extras = null);
    public sealed record Snapshot(int Schema, long GeneratedAt, List<Provider> Providers, string? Source = null, string? AppVersion = null);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public const int MaxProviders = 16, MaxWindows = 32, MaxPoints = 512;

    /// <summary>Builds from the running app's latest provider readings, with 24 hours of recorded history per window.</summary>
    public static Snapshot FromLive(IReadOnlyList<ProviderSnapshot> live, AppSettings settings, UsageHistory history, string? database, int maxPoints = MaxPoints)
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
                List<Point> points;
                lock (history) points = history.Read(snapshot.Id, account, w.Id, DateTimeOffset.FromUnixTimeMilliseconds(at).AddHours(-24))
                    .Where(p => double.IsFinite(p.Used) && p.Used >= 0 && p.Used <= 1000).Select(p => new Point(p.At.ToUnixTimeMilliseconds(), p.Used, p.Period)).ToList();
                Thin(points, Math.Min(maxPoints, MaxPoints));
                windows.Add(new Window(w.Id, w.Label, used, at, reset, points, w.Detail, Finite(w.UsedAmount), Finite(w.LimitAmount), w.Unit));
            }
            providers.Add(new Provider(snapshot.Id, snapshot.DisplayName, snapshot.Status.ToString(), windows,
                string.IsNullOrWhiteSpace(snapshot.StatusText) ? null : snapshot.StatusText,
                settings.ShowAccountNames && !string.IsNullOrWhiteSpace(snapshot.AccountName) ? snapshot.AccountName : null,
                snapshot.FetchedAt?.ToUnixTimeMilliseconds(), ManageUrl(snapshot.ManageUrl),
                view.SessionWindow is { UsedFraction: not null } s ? s.Id : null, view.WeeklyWindow is { UsedFraction: not null } wk ? wk.Id : null,
                extras.Count == 0 ? null : extras));
        }
        return new Snapshot(1, now.ToUnixTimeMilliseconds(), providers, "windows-app", AppVersion());
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
