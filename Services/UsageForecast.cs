using UsageNotch.Models;

namespace UsageNotch.Services;

public sealed record PaceForecast(string Summary, string Explanation, double? PercentPerHour = null, double? ProjectedPercent = null,
    string Confidence = "Limited history", int Samples = 0, double Coverage = 0, double? Variability = null,
    double? LowRate = null, double? HighRate = null);

public static class UsageForecast
{
    public static PaceForecast Calculate(IReadOnlyList<UsagePoint> points, SnapshotStatus status, DateTimeOffset now, AppSettings? settings = null)
    {
        if (status != SnapshotStatus.Ok || points.Count == 0 || now - points[^1].At > TimeSpan.FromMinutes(15) || points[^1].At > now)
            return new("Forecast unavailable", "A fresh, successful provider reading is required. No estimate is made across a reset, account change, or sampling gap.");
        var last = points[^1];
        if (last.Reset is not { } reset) return new("Reset time not reported", "A provider reset time is required to estimate usage at reset.");
        if (reset <= now) return new("Awaiting reset confirmation", "The reported reset has passed. Waiting for fresh provider data.");
        var recent = new List<UsagePoint>();
        for (var i = points.Count - 1; i >= 0 && points[i].At >= last.At.AddHours(-3); i--)
            if (points[i].Period == last.Period) recent.Add(points[i]);
        recent.Reverse(); var sample = recent.ToArray();
        var coverage = sample.Length > 1 ? Math.Clamp((last.At - sample[0].At).TotalHours / 3, 0, 1) : 0;
        if (sample.Length < 3 || last.At - sample[0].At < TimeSpan.FromMinutes(10))
            return new("Learning your usage pace", $"{sample.Length} readings; {coverage:P0} of the 3-hour lookback covered. Needs at least 3 readings spanning 10 minutes in the same limit period. Only time while the app runs is observed.", Samples: sample.Length, Coverage: coverage);
        for (var i = 1; i < sample.Length; i++)
            if (sample[i].At - sample[i - 1].At > TimeSpan.FromMinutes(20) || sample[i].Used < sample[i - 1].Used - .0001)
                return new("Not enough continuous history", "Wait for continuous readings after a gap or reset.", Samples: sample.Length, Coverage: coverage);
        var rates = new List<double>(); var anchor = sample[0];
        foreach (var point in sample.Skip(1))
            if (point.At - anchor.At >= TimeSpan.FromMinutes(5))
            { rates.Add(Math.Max(0, (point.Used - anchor.Used) / (point.At - anchor.At).TotalHours)); anchor = point; }
        var rate = Math.Max(0, (last.Used - sample[0].Used) / (last.At - sample[0].At).TotalHours);
        var mean = rates.Count > 0 ? rates.Average() : rate;
        var variability = mean > .000001 && rates.Count > 1 ? Math.Sqrt(rates.Average(r => Math.Pow(r - mean, 2))) / mean : 0;
        var confidence = coverage < .5 || rates.Count < 6 ? "Limited history" : variability > .75 ? "Highly variable usage" : "Consistent pace";
        var sorted = rates.Order().ToArray();
        double Quantile(double q) => sorted.Length == 0 ? rate : sorted[(int)Math.Round((sorted.Length - 1) * q)];
        var low = Math.Min(rate, Quantile(.1)); var high = Math.Max(rate, Quantile(.9));
        var projected = (last.Used + rate * (reset - last.At).TotalHours) * 100;
        var explanation = $"{sample.Length} readings spanning {(last.At - sample[0].At).TotalMinutes:0} min; {coverage:P0} coverage of a 3-hour lookback. " +
            $"Observed pace {rate * 100:0.00} percentage points/hour. Variability {variability:0.00} (standard deviation / mean of ≥5-minute rates). " +
            "Limited history means less than 90 minutes or 6 rate samples; high variability means a ratio above 0.75. " +
            "The dashed line assumes the endpoint-average pace continues until the provider's reset. Shading uses the 10th–90th percentile observed rates: scenarios, not a probability or confidence interval. " +
            "Provider rounding, limit changes and future activity can change the outcome. No tokens or request counts are inferred.";
        var summary = last.Used >= 1 ? "Reported limit reached" : rate <= .000001 ? "No increase observed" : $"Estimated {projected:0.0}% used at reset";
        if (last.Used < 1 && rate > .000001)
        {
            var hours = (1 - last.Used) / rate;
            if (hours < (reset - last.At).TotalHours)
            {
                var eta = last.At.AddHours(hours);
                summary = eta <= now ? "Estimated limit reached by now" : $"Estimated limit: {TimeDisplay.Stamp(eta, settings)}";
            }
        }
        return new(summary, explanation, rate * 100, projected, confidence, sample.Length, coverage, variability, low, high);
    }
}
