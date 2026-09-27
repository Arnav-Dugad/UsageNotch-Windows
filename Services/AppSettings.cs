using System.Text.Json;

namespace UsageNotch.Services;

public sealed class AppSettings
{
    public bool AutomaticUpdates { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReviewSession { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LiveEdit { get; set; }

    // Placement
    public string Edge { get; set; } = "Right";
    public bool FloatingDock { get; set; }
    public double? DockLeft { get; set; }
    public double? DockTop { get; set; }
    public double VerticalPosition { get; set; } = 0.5;
    public double HorizontalPosition { get; set; } = 0.5;

    // Appearance
    public string DockColor { get; set; } = "#0B0B0E";
    public double UiScale { get; set; } = 1;
    public double ProviderSpacing { get; set; } = 18;
    public double DockOpacity { get; set; } = 1;
    public bool ShowPercentages { get; set; } = true;
    public bool ShowRemaining { get; set; }
    public bool ShowAccountNames { get; set; } = true;
    public bool SleekMode { get; set; } = true;
    public bool ShowProviderNames { get; set; }
    public bool ShowDockResetTimes { get; set; }
    public bool ShowExpandedResetTimes { get; set; } = true;
    public bool ShowSecondaryPercentage { get; set; } = true;
    public bool ShowUsageSuffix { get; set; } = true;
    public bool ShowWindowLabel { get; set; }
    public bool ShowStatusBadge { get; set; } = true;
    public bool Use24HourTime { get; set; }
    public bool ShowClockSeconds { get; set; }
    public bool DetailedStats { get; set; }
    public string ClaudeDockLabel { get; set; } = "";
    public string CodexDockLabel { get; set; } = "";
    public string GeminiDockLabel { get; set; } = "";
    public string CursorDockLabel { get; set; } = "";
    public bool ColoredLogos { get; set; } = true;
    public bool ShowDashboardButton { get; set; }
    public bool GlassSurface { get; set; } = true;
    public bool CompactMode { get; set; }
    public string DisplayMode { get; set; } = "Session";

    // Behaviour
    public bool AlwaysVisible { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public int HoverDelayMs { get; set; } = 70;
    public int PollSeconds { get; set; } = 60;
    public bool AutoHide { get; set; }
    public int AutoHideDelayMs { get; set; } = 900;
    public bool ClickThrough { get; set; }
    public bool StartWithWindows { get; set; }
    public bool SnapToEdges { get; set; } = true;
    public bool PredictiveDrag { get; set; } = true;

    // Hotkeys, stored as human-readable chords such as "Ctrl+Alt+U".
    public bool HotkeyEnabled { get; set; }
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+U";
    public string RefreshHotkey { get; set; } = "Ctrl+Alt+R";

    // Alerts
    public bool AlertsEnabled { get; set; } = true;
    public int AlertWarnPercent { get; set; } = 80;
    public int AlertCriticalPercent { get; set; } = 95;
    public bool AlertOnReset { get; set; } = true;
    public bool AlertSound { get; set; }
    public bool AlertCards { get; set; } = true;
    public bool QuietHoursEnabled { get; set; }
    public int QuietStartHour { get; set; } = 22;
    public int QuietEndHour { get; set; } = 8;
    public DateTimeOffset? AlertsSnoozedUntil { get; set; }
    public Dictionary<string, int> AlertState { get; set; } = new();

    // Providers
    public bool ClaudeEnabled { get; set; } = true;
    public bool CodexEnabled { get; set; } = true;
    public bool GeminiEnabled { get; set; } = true;
    public bool CursorEnabled { get; set; } = true;
    public bool OpenAiApiEnabled { get; set; }
    public bool AnthropicApiEnabled { get; set; }
    public bool GeminiUseCloudProject { get; set; }

    public double? OpenAiMonthlyBudgetUsd { get; set; }
    public double? AnthropicMonthlyBudgetUsd { get; set; }

    // DPAPI-protected base64 strings, never plaintext.
    public string? OpenAiAdminKeyProtected { get; set; }
    public string? AnthropicAdminKeyProtected { get; set; }

    /// <summary>Deep copy used to snapshot settings before a live-preview editing session.</summary>
    public AppSettings Clone()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();
        copy.ReviewSession = ReviewSession;
        return copy;
    }

    /// <summary>Restores every persisted value from a snapshot, in place, so live bindings keep working.</summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite) continue;
            if (property.Name == nameof(ReviewSession)) continue;
            property.SetValue(this, property.GetValue(other));
        }
    }
}
