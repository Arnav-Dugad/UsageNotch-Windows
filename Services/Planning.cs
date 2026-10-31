using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UsageNotch.Models;

namespace UsageNotch.Services;

/// <summary>Provider ids are the kind ("claude") or the kind plus an account slug ("claude:work").</summary>
public static class ProviderKind
{
    public static string Of(string id) { var colon = id.IndexOf(':'); return colon < 0 ? id : id[..colon]; }
    public static string? Slug(string id) { var colon = id.IndexOf(':'); return colon < 0 ? null : id[(colon + 1)..]; }
    /// <summary>The design-token key: the API providers use dashes there, as on Android.</summary>
    public static string BrandKey(string id) => Of(id) switch { "anthropic_api" => "anthropic-api", "openai_api" => "openai-api", var kind => kind };
    public static bool HasWeekly(string id) => Of(id) is "claude" or "codex";
}

/// <summary>An extra Claude Code or Codex sign-in kept in its own folder (CLAUDE_CONFIG_DIR / CODEX_HOME).</summary>
public sealed class ExtraAccount
{
    public string Slug { get; set; } = "";
    public string Kind { get; set; } = "claude";
    public string Label { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public string ProviderId => Kind + ":" + Slug;
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => (Kind == "codex" ? "Codex" : "Claude") + " · " + (string.IsNullOrWhiteSpace(Label) ? Slug : Label.Trim());

    public static readonly Regex SlugPattern = new("^[a-z0-9-]{1,16}$", RegexOptions.CultureInvariant);
    public static string MakeSlug(string label, IEnumerable<string> taken)
    {
        var basis = new string(label.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        basis = string.IsNullOrEmpty(basis) ? "account" : basis[..Math.Min(12, basis.Length)].Trim('-');
        var used = taken.ToHashSet(); var slug = basis; var n = 2;
        while (used.Contains(slug)) slug = $"{basis}-{n++}";
        return slug;
    }
    /// <summary>The folder a Claude account needs (a .credentials.json) or a Codex account needs (auth.json).</summary>
    public bool SignedIn => File.Exists(Path.Combine(Folder, Kind == "codex" ? "auth.json" : ".credentials.json"));
}

public sealed record BudgetSetting(string Provider, string Window, int Limit, int? ByMinute)
{
    public string Label(bool use24) => $"{Limit}%" + (ByMinute is { } m ? " by " + Clock(m, use24) : "");
    public static string Clock(int minutes, bool use24)
    {
        var h = minutes / 60 % 24; var m = minutes % 60;
        return use24 ? $"{h:00}:{m:00}" : $"{(h % 12 == 0 ? 12 : h % 12)}{(m == 0 ? "" : $":{m:00}")} {(h < 12 ? "AM" : "PM")}";
    }
}

/// <summary>Budgets, as on Android: keep a limit under a percentage, optionally by a time of day.</summary>
public static class Budgets
{
    public enum State { Within, Near, Pace, Over }
    public sealed record Status(State State, string Text);

    public static BudgetSetting? Of(AppSettings settings, string provider, string window) =>
        settings.Budgets.FirstOrDefault(b => b.Provider == provider && b.Window == window);

    public static void Set(AppSettings settings, BudgetSetting? budget, string provider, string window)
    {
        settings.Budgets.RemoveAll(b => b.Provider == provider && b.Window == window);
        if (budget is not null) settings.Budgets.Add(budget with { Provider = provider, Window = window, Limit = Math.Clamp(budget.Limit, 1, 100) });
    }

    /// <summary>
    /// Where a window stands against its budget. Pace uses the forecast's percentage points per hour to project usage at the
    /// budget's time, or at the reset when the budget has no time.
    /// </summary>
    public static Status Evaluate(BudgetSetting b, LimitWindow w, PaceForecast? forecast, DateTimeOffset now, bool use24)
    {
        if (w.UsedFraction is not { } fraction) return new(State.Within, "Budget " + b.Label(use24));
        if (w.ResetsAt is { } reset && reset <= now) return new(State.Within, "Renewed · budget " + b.Label(use24));
        var used = fraction * 100;
        var local = now.ToLocalTime();
        DateTimeOffset? deadline = b.ByMinute is { } minutes ? new DateTimeOffset(local.Date, local.Offset).AddMinutes(minutes) : null;
        if (deadline <= now) deadline = null;
        double? rate = forecast?.PercentPerHour is > .0001 and var r ? r : null;
        double? projected = rate is null ? null
            : deadline is { } d ? used + rate.Value * (d - now).TotalHours
            : b.ByMinute is null ? forecast?.ProjectedPercent : null;
        var by = deadline is not null ? " by " + BudgetSetting.Clock(b.ByMinute!.Value, use24) : b.ByMinute is null ? " by the reset" : "";
        if (used > b.Limit) return new(State.Over, $"Over your {b.Limit}% budget");
        if (projected > b.Limit) return new(State.Pace, $"On pace for {Math.Min(999, Math.Round(projected.Value)):0}%{by}");
        if (used >= b.Limit - 10) return new(State.Near, $"Near your {b.Limit}% budget");
        return new(State.Within, $"Within your {b.Label(use24)} budget");
    }
}

public sealed record StartHint(DateTimeOffset StartAt, DateTimeOffset RenewAt, int BusyFrom, int BusyTo, bool Now, bool Tomorrow);

/// <summary>
/// When to start a session so it renews in the middle of your usual busy hours. Busy hours come from the 30-day
/// weekday × hour history; only windows up to 12 hours long qualify. The same rules as Android's BestStart.
/// </summary>
public static class BestStart
{
    public static StartHint? Hint(LimitWindow session, TimeSpan? period, IReadOnlyList<double> heat, IReadOnlyList<int> observed, DateTimeOffset now)
    {
        if (period is not { } length || length > TimeSpan.FromHours(12) || length <= TimeSpan.Zero || heat.Count != 168) return null;
        var today = now.ToLocalTime().Date;
        var earliest = session.ResetsAt is { } reset && reset > now ? reset : now;
        for (var offset = 0; offset <= 1; offset++)
        {
            var day = today.AddDays(offset);
            if (BusyBlock(heat, observed, (int)day.DayOfWeek) is not var (from, to)) continue;
            var dayStart = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
            var busyStart = dayStart.AddHours(from); var busyEnd = dayStart.AddHours(to);
            if (busyEnd <= now) continue;
            var ideal = busyStart + (busyEnd - busyStart) / 2 - length;
            var start = new[] { ideal, earliest, now }.Max();
            if (start + length >= busyEnd) continue; // It would renew after the busy hours: no benefit.
            return new StartHint(start, start + length, from, to, start - now < TimeSpan.FromMinutes(5), offset == 1);
        }
        return null;
    }

    /// <summary>The busiest run of hours (at least half of the day's peak) on a weekday (0 = Sunday), as [from, to).</summary>
    public static (int From, int To)? BusyBlock(IReadOnlyList<double> heat, IReadOnlyList<int> observed, int weekday)
    {
        var row = Enumerable.Range(0, 24).Select(h => observed.ElementAtOrDefault(weekday * 24 + h) > 0 ? heat[weekday * 24 + h] : 0).ToArray();
        var peak = row.Max();
        if (peak <= .005) return null;
        (int, int, double)? best = null; var start = -1;
        for (var h = 0; h <= 24; h++)
        {
            var busy = h < 24 && row[h] >= peak / 2;
            if (busy && start < 0) start = h;
            if (!busy && start >= 0) { var total = row[start..h].Sum(); if (best is null || total > best.Value.Item3) best = (start, h, total); start = -1; }
        }
        return best is { } b ? (b.Item1, b.Item2) : null;
    }

    public static string Hours(int from, int to, bool use24) => BudgetSetting.Clock(from * 60, use24) + "–" + BudgetSetting.Clock(to % 24 * 60, use24);

    /// <summary>A window's length, from its id or label, for windows that renew on a fixed schedule.</summary>
    public static TimeSpan? PeriodOf(LimitWindow w) => w.Id switch
    {
        "five_hour" => TimeSpan.FromHours(5),
        "seven_day" or "seven_day_opus" or "seven_day_sonnet" => TimeSpan.FromDays(7),
        _ when w.Id.EndsWith("-primary") || w.Label.Contains("5-hour", StringComparison.OrdinalIgnoreCase) || w.Label.Contains("5 hour", StringComparison.OrdinalIgnoreCase) => TimeSpan.FromHours(5),
        _ when w.Id.EndsWith("-secondary") || w.Label.Contains("week", StringComparison.OrdinalIgnoreCase) => TimeSpan.FromDays(7),
        _ => null
    };
}

/// <summary>Per-provider accent colours: the brand colour until you pick another. Rings keep the usage ramp.</summary>
public static class Accents
{
    public static System.Windows.Media.Color Default(string id) => FromArgb(DesignTokens.Brand.GetValueOrDefault(ProviderKind.BrandKey(id), DesignTokens.Brand["other"]));
    public static System.Windows.Media.Color Of(AppSettings settings, string id) =>
        settings.Accents.TryGetValue(id, out var hex) && TryParse(hex, out var color) ? color : Default(id);
    public static System.Windows.Media.Color FromArgb(uint argb) => System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    public static string Hex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    public static bool TryParse(string? hex, out System.Windows.Media.Color color)
    {
        color = default;
        if (hex is null || !Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")) return false;
        color = System.Windows.Media.Color.FromRgb(Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
        return true;
    }
}

public sealed class SharedEntry
{
    public JsonNode? V { get; set; }
    public long At { get; set; }
}

/// <summary>
/// The last-writer-wins settings map shared with phones (docs/PHONE-PROTOCOL.md → Shared settings). AppSettings fields
/// stay the working copy; Capture stamps the fields that changed, Merge takes newer remote entries and Apply writes the
/// map back into the fields.
/// </summary>
public static class SharedSettings
{
    public const int MaxEntries = 200;
    private static readonly Regex Key = new(@"^(budget:[a-z0-9_:-]{1,40}\|[A-Za-z0-9_.:-]{1,100}|accent:[a-z0-9_:-]{1,40}|name:[a-z0-9_:-]{1,40}|order|alerts:thresholds|alerts:quiet)$", RegexOptions.CultureInvariant);
    private static readonly string[] PrimaryNames = ["claude", "codex", "gemini", "cursor"];

    /// <summary>What the fields say now. Defaults are left out, so untouched settings never outvote a phone.</summary>
    public static Dictionary<string, JsonNode?> Desired(AppSettings s)
    {
        var map = new Dictionary<string, JsonNode?>();
        foreach (var b in s.Budgets) map[$"budget:{b.Provider}|{b.Window}"] = new JsonObject { ["limit"] = b.Limit, ["by"] = b.ByMinute };
        foreach (var (id, hex) in s.Accents) if (Accents.TryParse(hex, out _)) map["accent:" + id] = hex.ToUpperInvariant();
        foreach (var id in PrimaryNames) if (NameOf(s, id) is { Length: > 0 } name) map["name:" + id] = name;
        foreach (var (id, name) in s.DockNames) if (!PrimaryNames.Contains(id) && !string.IsNullOrWhiteSpace(name)) map["name:" + id] = Clean(name);
        if (s.ProviderOrder.Count > 0) map["order"] = new JsonArray(s.ProviderOrder.Take(16).Select(id => (JsonNode?)id).ToArray());
        if (s.AlertWarnPercent != 80 || s.AlertCriticalPercent != 95) map["alerts:thresholds"] = new JsonObject { ["warn"] = s.AlertWarnPercent, ["critical"] = s.AlertCriticalPercent };
        if (s.QuietHoursEnabled || s.QuietStartHour != 22 || s.QuietEndHour != 8) map["alerts:quiet"] = new JsonObject { ["on"] = s.QuietHoursEnabled, ["start"] = s.QuietStartHour, ["end"] = s.QuietEndHour };
        return map;
    }

    /// <summary>Stamps every field that differs from the map with <paramref name="at"/>. Returns true when something changed.</summary>
    public static bool Capture(AppSettings s, long at)
    {
        var desired = Desired(s); var changed = false;
        foreach (var key in desired.Keys.Union(s.Shared.Keys).ToArray())
        {
            desired.TryGetValue(key, out var want);
            s.Shared.TryGetValue(key, out var have);
            if (have is not null && JsonNode.DeepEquals(have.V, want)) continue;
            if (have is null && want is null) continue;
            s.Shared[key] = new SharedEntry { V = want?.DeepClone(), At = Math.Max(at, (have?.At ?? 0) + 1) };
            changed = true;
        }
        return changed;
    }

    /// <summary>Takes each remote entry that is newer than ours (ties keep ours). Invalid entries are skipped.</summary>
    public static bool Merge(AppSettings s, JsonObject? remote, DateTimeOffset now)
    {
        if (remote is null) return false;
        // Normalise to parsed JSON so numbers read the same whether they came from the network or from code.
        if (JsonNode.Parse(remote.ToJsonString()) is not JsonObject parsed) return false;
        remote = parsed;
        var changed = false; var ceiling = now.AddDays(1).ToUnixTimeMilliseconds();
        foreach (var (key, node) in remote)
        {
            if (!Key.IsMatch(key) || node is not JsonObject entry) continue;
            if (entry["at"] is not JsonValue atValue || !atValue.TryGetValue<long>(out var at) || at <= 0 || at > ceiling) continue;
            var value = entry["v"];
            if (!Valid(key, value)) continue;
            if (s.Shared.TryGetValue(key, out var mine) && mine.At >= at) continue;
            if (!s.Shared.ContainsKey(key) && s.Shared.Count >= MaxEntries) continue;
            s.Shared[key] = new SharedEntry { V = value?.DeepClone(), At = at };
            changed = true;
        }
        return changed;
    }

    /// <summary>Writes the map into the working fields.</summary>
    public static void Apply(AppSettings s)
    {
        s.Budgets = s.Shared.Where(e => e.Key.StartsWith("budget:") && e.Value.V is JsonObject).Select(e =>
        {
            var (provider, window) = SplitBudget(e.Key);
            var o = (JsonObject)e.Value.V!;
            return new BudgetSetting(provider, window, Math.Clamp((int?)o["limit"] ?? 60, 1, 100), (int?)o["by"] is { } by && by is >= 0 and < 1440 ? by : null);
        }).ToList();
        s.Accents = s.Shared.Where(e => e.Key.StartsWith("accent:") && e.Value.V is JsonValue v && Accents.TryParse((string?)v, out _))
            .ToDictionary(e => e.Key[7..], e => ((string)e.Value.V!).ToUpperInvariant());
        foreach (var id in PrimaryNames) SetName(s, id, s.Shared.TryGetValue("name:" + id, out var n) && n.V is JsonValue nv ? Clean((string?)nv ?? "") : "");
        s.DockNames = s.Shared.Where(e => e.Key.StartsWith("name:") && !PrimaryNames.Contains(e.Key[5..]) && e.Value.V is JsonValue)
            .ToDictionary(e => e.Key[5..], e => Clean((string?)e.Value.V ?? ""));
        s.ProviderOrder = s.Shared.TryGetValue("order", out var order) && order.V is JsonArray list ? list.Select(x => (string?)x ?? "").Where(x => x.Length > 0).Distinct().Take(16).ToList() : [];
        if (s.Shared.TryGetValue("alerts:thresholds", out var t) && t.V is JsonObject th)
        { s.AlertWarnPercent = Math.Clamp((int?)th["warn"] ?? 80, 1, 100); s.AlertCriticalPercent = Math.Clamp((int?)th["critical"] ?? 95, s.AlertWarnPercent, 100); }
        else if (s.Shared.ContainsKey("alerts:thresholds")) { s.AlertWarnPercent = 80; s.AlertCriticalPercent = 95; }
        if (s.Shared.TryGetValue("alerts:quiet", out var q) && q.V is JsonObject quiet)
        { s.QuietHoursEnabled = (bool?)quiet["on"] ?? false; s.QuietStartHour = Math.Clamp((int?)quiet["start"] ?? 22, 0, 23); s.QuietEndHour = Math.Clamp((int?)quiet["end"] ?? 8, 0, 23); }
        else if (s.Shared.ContainsKey("alerts:quiet")) { s.QuietHoursEnabled = false; s.QuietStartHour = 22; s.QuietEndHour = 8; }
    }

    public static JsonObject ToJson(AppSettings s)
    {
        var o = new JsonObject();
        foreach (var (key, entry) in s.Shared.OrderBy(e => e.Key, StringComparer.Ordinal)) o[key] = new JsonObject { ["v"] = entry.V?.DeepClone(), ["at"] = entry.At };
        return o;
    }

    private static (string Provider, string Window) SplitBudget(string key)
    {
        var body = key[7..]; var bar = body.IndexOf('|');
        return (body[..bar], body[(bar + 1)..]);
    }

    private static bool Valid(string key, JsonNode? v)
    {
        if (v is null) return true;
        try
        {
            if (key.StartsWith("budget:")) return v is JsonObject o && (int?)o["limit"] is >= 1 and <= 100 && (o["by"] is null || (int?)o["by"] is >= 0 and < 1440);
            if (key.StartsWith("accent:")) return v is JsonValue && Accents.TryParse((string?)v, out _);
            if (key.StartsWith("name:")) return v is JsonValue && ((string?)v)?.Length <= 18;
            if (key == "order") return v is JsonArray a && a.Count <= 16 && a.All(x => x is JsonValue && ((string?)x)?.Length is > 0 and <= 60);
            if (key == "alerts:thresholds") return v is JsonObject o && (int?)o["warn"] is >= 1 and <= 100 && (int?)o["critical"] is >= 1 and <= 100;
            if (key == "alerts:quiet") return v is JsonObject o && o["on"] is JsonValue && (int?)o["start"] is >= 0 and <= 23 && (int?)o["end"] is >= 0 and <= 23;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return false; }
        return false;
    }

    private static string Clean(string name) => new(name.Trim().Where(c => !char.IsControl(c)).Take(18).ToArray());
    private static string NameOf(AppSettings s, string id) => Clean(id switch { "claude" => s.ClaudeDockLabel, "codex" => s.CodexDockLabel, "gemini" => s.GeminiDockLabel, _ => s.CursorDockLabel });
    private static void SetName(AppSettings s, string id, string value)
    {
        switch (id) { case "claude": s.ClaudeDockLabel = value; break; case "codex": s.CodexDockLabel = value; break; case "gemini": s.GeminiDockLabel = value; break; default: s.CursorDockLabel = value; break; }
    }
}
