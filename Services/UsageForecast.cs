using UsageNotch.Models;

namespace UsageNotch.Services;

public sealed record PaceForecast(string Summary, string Explanation, double? PercentPerHour = null, double? ProjectedPercent = null);

public static class UsageForecast
{
    public static PaceForecast Calculate(IReadOnlyList<UsagePoint> points, SnapshotStatus status, DateTimeOffset now)
    {
        const string caveat = "Estimate from observed usage; assumes the same pace continues. Provider limits and future activity can change.";
        if (status != SnapshotStatus.Ok || points.Count == 0 || now - points[^1].At > TimeSpan.FromMinutes(15) || points[^1].At > now)
            return new("Forecast unavailable", "A fresh, successful provider reading is required.");
        var last = points[^1];
        if (last.Reset is not { } reset) return new("Reset time not reported", "A reset time is required to estimate usage at reset.");
        if (reset <= now) return new("Awaiting reset confirmation", "The reported reset has passed. Waiting for fresh provider data.");
        var sample = points.Where(p => p.Period == last.Period && p.At >= last.At.AddHours(-3)).ToArray();
        if (sample.Length < 3 || last.At - sample[0].At < TimeSpan.FromMinutes(10))
            return new("Learning your usage pace", "Needs at least 3 readings spanning 10 minutes in the same limit period. Only time while the app is running is observed.");
        for (var i = 1; i < sample.Length; i++)
            if (sample[i].At - sample[i - 1].At > TimeSpan.FromMinutes(20) || sample[i].Used < sample[i - 1].Used - .0001)
                return new("Not enough continuous history", "Wait for a continuous set of readings after a gap or reset.");
        var rate = Math.Max(0, (last.Used - sample[0].Used) / (last.At - sample[0].At).TotalHours);
        var projected = (last.Used + rate * (reset - last.At).TotalHours) * 100;
        var explanation = $"{rate * 100:0.0} percentage points/hour over {(last.At - sample[0].At).TotalMinutes:0} minutes. " + caveat;
        if (last.Used >= 1) return new("Reported limit reached", explanation, rate * 100, projected);
        if (rate <= .000001) return new("No increase observed", explanation, 0, projected);
        var hoursToLimit = (1 - last.Used) / rate;
        var exhaustion = hoursToLimit < (reset - last.At).TotalHours ? last.At.AddHours(hoursToLimit) : (DateTimeOffset?)null;
        return new(exhaustion is { } eta ? eta <= now ? "Estimated limit reached by now" : $"Estimated limit: {eta.ToLocalTime():ddd HH:mm}"
            : $"Estimated {projected:0.0}% used at reset", explanation, rate * 100, projected);
    }
}
