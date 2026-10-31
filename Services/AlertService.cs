using System.Collections.ObjectModel;
using UsageNotch.Models;

namespace UsageNotch.Services;

public sealed record AlertNotice(string Title, string Message, bool Critical, string ProviderId = "",
    double? UsedFraction = null, DateTimeOffset? CreatedAt = null, bool Suppressed = false, bool IsPreview = false)
{
    public string TimeLabel => CreatedAt?.ToLocalTime().ToString("h:mm tt") ?? "Now";
    public string KindLabel => Suppressed ? "Quietly saved" : Critical ? "Urgent" : "Update";
}

/// <summary>What the alerts need beyond the readings: pace forecasts, best start times and history summaries, computed off the UI thread.</summary>
public sealed record AlertContext(
    IReadOnlyDictionary<string, PaceForecast?> Paces,
    IReadOnlyDictionary<string, StartHint?> Starts,
    IReadOnlyDictionary<string, WindowSummary?> Summaries)
{
    public static readonly AlertContext Empty = new(new Dictionary<string, PaceForecast?>(), new Dictionary<string, StartHint?>(), new Dictionary<string, WindowSummary?>());
    public static string Key(string provider, string window) => provider + "|" + window;
}

/// <summary>Live readings only; durable deduplication is never cleared by a provider outage.</summary>
public sealed class AlertService
{
    private readonly AppSettings _settings;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, string> _currentKeys = [];
    private readonly Dictionary<string, double> _lastFractions = [];
    private readonly HashSet<string> _seenSlots = [];
    public ObservableCollection<AlertNotice> History { get; } = [];
    public event Action<AlertNotice>? Raised;
    public AlertService(AppSettings settings, Func<DateTimeOffset>? now = null)
    { _settings = settings; _now = now ?? (() => DateTimeOffset.Now); }

    public void Reprime() => _seenSlots.Clear();
    public void Snooze(TimeSpan duration)
    {
        _settings.AlertsSnoozedUntil = _now() + duration;
        SettingsStore.Save(_settings);
    }
    public void Resume() { _settings.AlertsSnoozedUntil = null; SettingsStore.Save(_settings); }
    public bool IsQuiet(DateTimeOffset time)
    {
        if (!_settings.AlertsEnabled || _settings.AlertsSnoozedUntil > time) return true;
        if (!_settings.QuietHoursEnabled) return false;
        var hour = time.ToLocalTime().Hour;
        var start = Math.Clamp(_settings.QuietStartHour, 0, 23);
        var end = Math.Clamp(_settings.QuietEndHour, 0, 23);
        return start == end || (start < end ? hour >= start && hour < end : hour >= start || hour < end);
    }

    public void Preview()
    {
        var notice = new AlertNotice("Claude · getting close", "Current session · 82% used · 18% left · resets in 47 min",
            false, "claude", .82, _now(), IsPreview: true);
        // An explicit preview does not mutate thresholds or consume real usage.
        Raised?.Invoke(notice);
    }

    public void Evaluate(IReadOnlyList<ProviderViewModel> items, AlertContext? context = null)
    {
        context ??= AlertContext.Empty;
        var warn = Math.Clamp(_settings.AlertWarnPercent, 1, 100) / 100d;
        var critical = Math.Max(warn, Math.Clamp(_settings.AlertCriticalPercent, 1, 100) / 100d);
        var changed = false;
        foreach (var item in items)
        {
            if (item.Status != SnapshotStatus.Ok) continue;
            foreach (var window in item.Windows)
            {
                if (window.UsedFraction is not { } raw || !double.IsFinite(raw)) continue;
                var fraction = Math.Clamp(raw, 0, 1);
                // Account identity separates warnings without persisting names or email addresses.
                var identity = string.IsNullOrWhiteSpace(item.Snapshot.AccountName) ? "" :
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.Snapshot.AccountName)))[..16];
                var slot = item.Id + "|" + identity + "|" + window.Id;
                var key = slot + "|" + (window.ResetsAt?.ToUnixTimeSeconds() ?? 0);
                var seen = !_seenSlots.Add(slot);
                var previousFraction = _lastFractions.GetValueOrDefault(slot, fraction);
                if (_currentKeys.TryGetValue(slot, out var previousKey) && previousKey != key)
                {
                    // A changed reset estimate alone is not a reset or a new warning period.
                    var reset = long.TryParse(previousKey[(previousKey.LastIndexOf('|') + 1)..], out var unix) ? unix : 0;
                    var rolledOver = fraction < previousFraction - .1 ||
                        (reset > 0 && reset <= _now().ToUnixTimeSeconds() && window.ResetsAt?.ToUnixTimeSeconds() > reset);
                    if (!rolledOver) key = previousKey;
                    else
                    {
                        var previousLevel = _settings.AlertState.GetValueOrDefault(previousKey);
                        _settings.AlertState.Remove(previousKey);
                        changed = true;
                        if (seen && previousLevel > 0 && _settings.AlertOnReset && fraction < warn)
                            Publish(new AlertNotice(item.DisplayName + " · limit reset", window.Label + " is available again.",
                                false, item.Id, fraction));
                    }
                }
                else if (seen && window.ResetsAt is null && fraction < warn && previousFraction >= warn)
                {
                    _settings.AlertState.Remove(key);
                    changed = true;
                    // No reset timestamp: re-arm safely, but do not claim a confirmed reset.
                }
                _currentKeys[slot] = key;
                _lastFractions[slot] = fraction;
                var level = fraction >= critical ? 2 : fraction >= warn ? 1 : 0;
                if (level <= _settings.AlertState.GetValueOrDefault(key)) continue;
                _settings.AlertState[key] = level;
                changed = true;
                if (seen)
                    Publish(new AlertNotice(item.DisplayName + (level == 2 ? " · almost out" : " · getting close"),
                        window.Label + $" · {Math.Round(fraction * 100):0}% used · {Math.Round((1 - fraction) * 100):0}% left" +
                        (string.IsNullOrEmpty(ResetTextConverter.Describe(window.ResetsAt)) ? "" : " · " + ResetTextConverter.Describe(window.ResetsAt)),
                        level == 2, item.Id, fraction));
            }
        }
        if (Planning(items, context)) changed = true;
        // Keep bounded history, but never discard outage state merely because a provider is missing.
        if (_settings.AlertState.Count > 512)
            foreach (var key in _settings.AlertState.Keys.Where(k => !_currentKeys.ContainsValue(k)).Take(_settings.AlertState.Count - 512).ToArray())
                _settings.AlertState.Remove(key);
        if (changed) SettingsStore.Save(_settings);
    }

    /// <summary>Once-only alerts: budgets, pace, the reminder before a reset, a good moment to start and the weekly recap.</summary>
    private bool Planning(IReadOnlyList<ProviderViewModel> items, AlertContext context)
    {
        var now = _now(); var changed = false; var use24 = _settings.Use24HourTime;
        var today = now.ToLocalTime().Date.ToString("yyyy-MM-dd");
        bool Once(string key)
        {
            if (_settings.AlertState.ContainsKey(key)) return false;
            _settings.AlertState[key] = 1; changed = true; return true;
        }
        var critical = Math.Clamp(_settings.AlertCriticalPercent, 1, 100) / 100d;
        foreach (var item in items.Where(i => i.Status == SnapshotStatus.Ok))
        {
            foreach (var window in item.Windows)
            {
                if (window.UsedFraction is not { } used || !double.IsFinite(used)) continue;
                var period = $"{item.Id}|{window.Id}|{window.ResetsAt?.ToUnixTimeSeconds() ?? 0}";
                var pace = context.Paces.GetValueOrDefault(AlertContext.Key(item.Id, window.Id));
                var reset = window.ResetsAt;
                if (reset is { } r && r <= now) continue;
                if (_settings.AlertBudgets && Budgets.Of(_settings, item.Id, window.Id) is { } budget)
                {
                    var status = Budgets.Evaluate(budget, window, pace, now, use24);
                    var basis = $"plan|{period}|budget-{budget.Limit}-{budget.ByMinute?.ToString() ?? "reset"}";
                    if (status.State == Budgets.State.Over && Once(basis + "|over"))
                        Publish(new AlertNotice($"{item.DisplayName} · over your budget", $"{window.Label} is {Math.Round(used * 100):0}% used; your budget is {budget.Label(use24)}.", true, item.Id, used));
                    else if (status.State == Budgets.State.Pace && !_settings.AlertState.ContainsKey(basis + "|over") && Once(basis + "|pace|" + today))
                        Publish(new AlertNotice($"{item.DisplayName} · budget at risk", $"{window.Label}: {status.Text.ToLowerInvariant()}. Budget {budget.Label(use24)}.", false, item.Id, used));
                }
                if (_settings.AlertPace && pace?.PercentPerHour is > .0001 and var rate && used < critical && reset is { } resetAt)
                {
                    var hours = (1 - used) * 100 / rate; var eta = now.AddHours(hours);
                    if (eta < resetAt && hours <= 2 && Once($"plan|{period}|pace"))
                        Publish(new AlertNotice($"{item.DisplayName} · runs out in about {Duration(TimeSpan.FromHours(hours))}",
                            $"At this pace {window.Label} reaches its limit around {TimeDisplay.Clock(eta, use24)}, before it resets at {TimeDisplay.Clock(resetAt, use24)}.", false, item.Id, used));
                }
                if (_settings.AlertPreReset && reset is { } soon && used >= .5)
                {
                    var minutes = Math.Clamp(_settings.PreResetMinutes, 1, 120);
                    if (soon - now <= TimeSpan.FromMinutes(minutes) && Once($"plan|{period}|soon"))
                        Publish(new AlertNotice($"{item.DisplayName} renews in {Math.Max(1, Math.Ceiling((soon - now).TotalMinutes)):0} min",
                            $"{window.Label} is {Math.Round(used * 100):0}% used and renews at {TimeDisplay.Clock(soon, use24)}.", false, item.Id, used));
                }
            }
            if (_settings.AlertGoodMoment && item.SessionWindow is { } session && context.Starts.GetValueOrDefault(item.Id) is { Now: true } hint
                && (session.UsedFraction ?? 0) < .02 && Once($"plan|{item.Id}|start|{today}"))
                Publish(new AlertNotice($"{item.DisplayName} · a good moment to start",
                    $"Start a session now and it renews around {TimeDisplay.Clock(hint.RenewAt, use24)}, in the middle of your busy hours ({BestStart.Hours(hint.BusyFrom, hint.BusyTo, use24)}).", false, item.Id, session.UsedFraction));
        }
        if (_settings.WeeklyRecap && Recap(items, context, now) is { } recap && (_settings.LastRecapAt is null || now - _settings.LastRecapAt > TimeSpan.FromDays(5)))
        {
            _settings.LastRecapAt = now; changed = true;
            Publish(recap);
        }
        return changed;
    }

    /// <summary>The Sunday-evening recap: each provider's week, the busiest day and the longest streak. Null outside Sunday 6 PM–midnight.</summary>
    public static AlertNotice? Recap(IReadOnlyList<ProviderViewModel> items, AlertContext context, DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        if (local.DayOfWeek != DayOfWeek.Sunday || local.Hour < 18) return null;
        var lines = new List<string>(); DayUsage? busiest = null; string? busiestName = null; var streak = 0;
        foreach (var item in items)
        {
            if (item.SessionWindow is not { } w || context.Summaries.GetValueOrDefault(AlertContext.Key(item.Id, w.Id)) is not { } summary) continue;
            if (summary.Total(7) is not { } total) continue;
            lines.Add($"{item.DisplayName} {Math.Round(total * 100):0}% of {w.Label.ToLowerInvariant()}s");
            if (summary.Busiest(7) is { } day && (busiest is null || day.Used > busiest.Used)) { busiest = day; busiestName = item.DisplayName; }
            streak = Math.Max(streak, summary.Streak);
        }
        if (lines.Count == 0) return null;
        var body = string.Join(" · ", lines) + (busiest is not null ? $". Busiest: {busiest.Date:dddd} ({busiestName})" : "") + (streak > 1 ? $". Streak: {streak} days." : ".");
        return new AlertNotice("Your week in AI usage", body, false, items.FirstOrDefault()?.Id ?? "", null);
    }

    private static string Duration(TimeSpan span) => span.TotalMinutes < 60 ? $"{Math.Max(1, Math.Round(span.TotalMinutes)):0} min" : $"{(int)span.TotalHours} h {span.Minutes} min";

    private void Publish(AlertNotice notice)
    {
        var time = _now();
        notice = notice with { CreatedAt = time, Suppressed = IsQuiet(time) };
        History.Insert(0, notice);
        while (History.Count > 40) History.RemoveAt(History.Count - 1);
        if (!notice.Suppressed) Raised?.Invoke(notice);
    }
}
