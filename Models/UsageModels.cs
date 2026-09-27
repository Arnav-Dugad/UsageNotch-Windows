using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using UsageNotch.Services;

namespace UsageNotch.Models;

public enum Fidelity { Official, Derived, Manual }
public enum SnapshotStatus { Loading, Ok, Stale, NeedsAuth, Unsupported, Error }

public sealed record LimitWindow(
    string Id,
    string Label,
    double? UsedFraction,
    DateTimeOffset? ResetsAt = null,
    string? Detail = null,
    double? UsedAmount = null,
    double? LimitAmount = null,
    string? Unit = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResetLocalText => ResetsAt is { } at ? $"Resets {at.ToLocalTime():ddd, MMM d · HH:mm:ss zzz} (local)" : "Exact reset not reported";
}

public sealed record ProviderSnapshot(
    string Id,
    string DisplayName,
    string Glyph,
    Fidelity Fidelity,
    SnapshotStatus Status,
    IReadOnlyList<LimitWindow> Windows,
    string? StatusText = null,
    string? ManageUrl = null,
    DateTimeOffset? FetchedAt = null,
    string? AccountName = null)
{
    public LimitWindow? Headline => Windows
        .Where(window => window.UsedFraction.HasValue)
        .OrderByDescending(window => window.UsedFraction!.Value)
        .FirstOrDefault() ?? Windows.FirstOrDefault();
}

public sealed class ProviderViewModel : INotifyPropertyChanged
{
    private ProviderSnapshot _snapshot;
    private readonly AppSettings _settings;

    public ProviderViewModel(ProviderSnapshot snapshot, AppSettings? settings = null)
    {
        _snapshot = snapshot;
        _settings = settings ?? new AppSettings();
    }

    public void RefreshAppearance() => OnPropertyChanged(null);
    public void RefreshClock()
    {
        OnPropertyChanged(nameof(ClockNow));
        OnPropertyChanged(nameof(ResetText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(LimitDescription));
    }

    public ProviderSnapshot Snapshot
    {
        get => _snapshot;
        set { _snapshot = value; OnPropertyChanged(null); }
    }

    public string Id => Snapshot.Id;
    public string DisplayName => Snapshot.DisplayName;
    public string Glyph => Snapshot.Glyph;
    public IReadOnlyList<LimitWindow> Windows => Snapshot.Windows;
    public SnapshotStatus Status => Snapshot.Status;
    public string? StatusText => Snapshot.StatusText;
    public string AccountLabel => !_settings.ShowAccountNames ? "" : Snapshot.AccountName ?? "Account name unavailable";
    public bool ShowRemaining => _settings.ShowRemaining;
    public double? RingFraction => UsedFraction is { } value ? ShowRemaining ? 1 - Math.Clamp(value, 0, 1) : value : null;
    public double SecondaryRingFraction => SecondaryKnown && ShowRemaining ? 1 - Math.Clamp(SecondaryFraction, 0, 1) : SecondaryFraction;

    public LimitWindow? SessionWindow => Id switch
    {
        "claude" => Windows.FirstOrDefault(window => window.Id == "five_hour"),
        "codex" => Windows.FirstOrDefault(window => window.Id is "codex-primary" or "default-primary")
            ?? Windows.FirstOrDefault(window => window.Id.EndsWith("-primary") && !window.Id.Contains("reserve")),
        _ => Windows.FirstOrDefault()
    };

    public LimitWindow? WeeklyWindow => Id switch
    {
        "claude" => Windows.FirstOrDefault(window => window.Id == "seven_day"),
        "codex" => Windows.FirstOrDefault(window => window.Id == SessionWindow?.Id.Replace("-primary", "-secondary")),
        _ => Windows.FirstOrDefault(window => window.Label != SessionWindow?.Label)
    };

    public LimitWindow? PrimaryWindow => _settings.DisplayMode switch
    {
        "Weekly" => WeeklyWindow ?? SessionWindow,
        "Highest" => Snapshot.Headline,
        _ => SessionWindow ?? Snapshot.Headline
    };

    public double? UsedFraction => PrimaryWindow?.UsedFraction;
    public bool Dual => _settings.DisplayMode == "Dual" && WeeklyWindow is not null;
    public double SecondaryFraction => WeeklyWindow?.UsedFraction ?? 0;
    public bool SecondaryKnown => WeeklyWindow?.UsedFraction.HasValue == true;

    // Appearance passthroughs so templates can bind without reaching into settings.
    public bool ShowPercentages => _settings.ShowPercentages && !_settings.CompactMode;
    public bool ShowProviderNames => _settings.ShowProviderNames && !_settings.CompactMode;
    public bool ReducedMotion => Motion.IsReduced(_settings);
    public bool ColoredLogos => _settings.ColoredLogos;
    public bool Compact => _settings.CompactMode;
    public double RingSize => _settings.CompactMode ? 38 : _settings.SleekMode ? 44 : 56;
    public double LogoSize => _settings.CompactMode ? 16 : _settings.SleekMode ? 19 : 24;
    public double CellWidth => _settings.CompactMode ? 58 : _settings.SleekMode ? 70 : 92;
    public double PercentFontSize => Dual || ShowRemaining ? 12 : _settings.SleekMode ? 14 : 18;
    public System.Windows.Thickness CellMargin => _settings.Edge == "Top"
        ? new(_settings.ProviderSpacing / 2, 0, _settings.ProviderSpacing / 2, 0)
        : new(0, _settings.ProviderSpacing / 2, 0, _settings.ProviderSpacing / 2);

    public bool IsStale => Status == SnapshotStatus.Stale || (Status != SnapshotStatus.Ok && Snapshot.FetchedAt.HasValue && Windows.Count > 0);
    public bool IsBusy => Status == SnapshotStatus.Loading;
    public bool HasWindows => Windows.Count > 0;
    public bool HasManageUrl => Uri.TryCreate(Snapshot.ManageUrl, UriKind.Absolute, out _);
    public bool ShowDashboard => (_settings.ShowDashboardButton || Status is SnapshotStatus.Unsupported or SnapshotStatus.Stale or SnapshotStatus.Error) && HasManageUrl;
    public string DashboardLabel => Id == "gemini" && Status == SnapshotStatus.Unsupported ? "Google migration guidance" : "Open dashboard";

    public string PercentText => Status == SnapshotStatus.Loading ? "···" : RingFraction is null ? "—" : $"{Math.Round(Math.Clamp(RingFraction.Value, 0, 1) * 100):0}%" + (ShowRemaining ? " left" : "");
    public double UsedPercent => Math.Clamp((UsedFraction ?? 0) * 100.0, 0, 100);

    /// <summary>Second line under the ring in orbit mode; stacked so it can never clip the dock.</summary>
    public string SecondaryLine => Dual && ShowPercentages
        ? (Id is "claude" or "codex" ? $"7d {DisplayPercent(WeeklyWindow?.UsedFraction)}" : $"2nd {DisplayPercent(WeeklyWindow?.UsedFraction)}")
        : string.Empty;

    public string HeadlineLabel => PrimaryWindow?.Label ?? Status switch
    {
        SnapshotStatus.Loading => "Checking your account",
        SnapshotStatus.NeedsAuth => "Sign-in required",
        SnapshotStatus.Unsupported => "Not available for this account",
        SnapshotStatus.Error => "Could not read usage",
        _ => "No limit reported"
    };

    public string ResetText => ResetTextConverter.Describe(PrimaryWindow?.ResetsAt);
    public string DockResetLines => string.Join("\n", Windows.Where(w => w.ResetsAt is not null).Take(2).Select(w =>
        $"{(w.Label.Contains("5-hour", StringComparison.OrdinalIgnoreCase) ? "5h" : w.Label.Contains("week", StringComparison.OrdinalIgnoreCase) ? "7d" : w.Label)} {w.ResetsAt!.Value.ToLocalTime():ddd HH:mm:ss}"));
    public string DockResetTooltip => string.Join("\n", Windows.Select(w => w.Label + ": " + w.ResetLocalText));
    public DateTimeOffset ClockNow => DateTimeOffset.Now;
    public string RemainingText => UsedFraction is { } fraction ? $"{Math.Max(0, Math.Round((1 - fraction) * 100)):0}% left" : "";

    public string StatusPill => _settings.ReviewSession ? "Preview" : Status switch
    {
        SnapshotStatus.Loading => "Checking",
        SnapshotStatus.Stale => "Saved",
        SnapshotStatus.NeedsAuth => "Sign in",
        SnapshotStatus.Unsupported => "Unavailable",
        SnapshotStatus.Error => "Error",
        _ => "Live"
    };

    public Brush StatusPillBrush => Palette.Frozen(Palette.Alpha(StatusColor, 0.16));
    public System.Windows.Media.Color SurfaceColor => DockTheme.Parse(_settings.DockColor);
    public Brush StatusPillForeground => Palette.Frozen(Palette.Readable(StatusColor, Palette.Lerp(SurfaceColor, StatusColor, .16)));
    public Brush AccentBrush => Palette.Frozen(Palette.Readable(Palette.For(Status, UsedFraction), Palette.Lerp(SurfaceColor, Palette.For(Status, UsedFraction), .18)));
    public Brush AccentSoftBrush => Palette.Frozen(Palette.Alpha(Palette.For(Status, UsedFraction), 0.18));

    private System.Windows.Media.Color StatusColor => Status switch
    {
        SnapshotStatus.Loading => Palette.Loading,
        SnapshotStatus.Stale => Palette.Warning,
        SnapshotStatus.NeedsAuth or SnapshotStatus.Unsupported => Palette.Warning,
        SnapshotStatus.Error => Palette.Danger,
        _ => UsedFraction is { } fraction ? Palette.Usage(fraction) : Palette.Unknown
    };

    public string LimitDescription
    {
        get
        {
            var head = DisplayName + " · " + (UsedFraction is { } fraction ? $"{Math.Round(fraction * 100):0}% used" : StatusPill);
            if (Dual) head += Id is "claude" or "codex" ? "\nOuter ring: 5-hour. Inner ring: 7-day." : "\nOuter ring: first bucket. Inner ring: second.";
            if (!string.IsNullOrWhiteSpace(ResetText)) head += "\n" + char.ToUpperInvariant(ResetText[0]) + ResetText[1..];
            return head;
        }
    }

    public string UpdatedText => Status == SnapshotStatus.Loading
        ? "Checking account…"
        : Snapshot.FetchedAt is { } fetched ? $"Updated {RelativeTime(fetched)}" : "Account status";

    public string FidelityMark => Snapshot.Fidelity == Fidelity.Official ? "" : "~";

    private static string Percent(double? value) => value.HasValue ? $"{Math.Round(value.Value * 100):0}%" : "—";
    private string DisplayPercent(double? value) => value is { } d ? Percent(ShowRemaining ? 1 - Math.Clamp(d, 0, 1) : d) + (ShowRemaining ? " left" : "") : "—";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string RelativeTime(DateTimeOffset time)
    {
        var elapsed = DateTimeOffset.Now - time;
        if (elapsed.TotalSeconds < 45) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{Math.Max(1, Math.Floor(elapsed.TotalMinutes)):0} min ago";
        if (elapsed.TotalHours < 12) return $"{Math.Floor(elapsed.TotalHours):0} h ago";
        return time.ToLocalTime().ToString("h:mm tt");
    }
}
