using System.Collections.ObjectModel;
using UsageNotch.Models;

namespace UsageNotch.Services;

public sealed record AlertNotice(string Title, string Message, bool Critical, string ProviderId = "",
    double? UsedFraction = null, DateTimeOffset? CreatedAt = null, bool Suppressed = false, bool IsPreview = false)
{
    public string TimeLabel => CreatedAt?.ToLocalTime().ToString("h:mm tt") ?? "Now";
    public string KindLabel => Suppressed ? "Quietly saved" : Critical ? "Urgent" : "Update";
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

    public void Evaluate(IReadOnlyList<ProviderViewModel> items)
    {
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
        // Keep bounded history, but never discard outage state merely because a provider is missing.
        if (_settings.AlertState.Count > 512)
            foreach (var key in _settings.AlertState.Keys.Where(k => !_currentKeys.ContainsValue(k)).Take(_settings.AlertState.Count - 512).ToArray())
                _settings.AlertState.Remove(key);
        if (changed) SettingsStore.Save(_settings);
    }

    private void Publish(AlertNotice notice)
    {
        var time = _now();
        notice = notice with { CreatedAt = time, Suppressed = IsQuiet(time) };
        History.Insert(0, notice);
        while (History.Count > 40) History.RemoveAt(History.Count - 1);
        if (!notice.Suppressed) Raised?.Invoke(notice);
    }
}
