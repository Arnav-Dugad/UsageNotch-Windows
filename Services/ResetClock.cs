namespace UsageNotch.Services;

public static class ResetClock
{
    public static string Describe(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is not { } moment) return "Reset time not reported";
        if (moment <= now) return "Reset due · awaiting provider confirmation";
        var seconds = (long)Math.Ceiling((moment - now).TotalSeconds);
        var days = seconds / 86400;
        return "Resets in " + (days > 0 ? $"{days}d " : "") + $"{seconds / 3600 % 24:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }
}
