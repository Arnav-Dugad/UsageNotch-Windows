using System.Globalization;

namespace UsageNotch.Services;

public static class TimeDisplay
{
    public static string Clock(DateTimeOffset at, bool use24Hour = false, bool seconds = false) =>
        at.ToLocalTime().ToString(use24Hour ? (seconds ? "HH:mm:ss" : "HH:mm") : (seconds ? "h:mm:ss tt" : "h:mm tt"), CultureInfo.InvariantCulture);
    public static string Stamp(DateTimeOffset at, AppSettings? settings = null, bool exact = false) =>
        at.ToLocalTime().ToString("ddd, MMM d", CultureInfo.CurrentCulture) + " · " + Clock(at, settings?.Use24HourTime == true, exact || settings?.ShowClockSeconds == true)
        + (exact ? at.ToLocalTime().ToString(" 'UTC'zzz", CultureInfo.InvariantCulture) : "");
    public static string Reset(DateTimeOffset? at, AppSettings? settings = null)
    {
        if (at is not { } time) return "Reset time not reported";
        var date = time.ToLocalTime().Date; var today = DateTime.Now.Date;
        var day = date == today ? "today" : date == today.AddDays(1) ? "tomorrow" : time.ToLocalTime().ToString("ddd, MMM d", CultureInfo.CurrentCulture);
        return $"Resets {day} at {Clock(time, settings?.Use24HourTime == true, settings?.ShowClockSeconds == true)}";
    }
}
