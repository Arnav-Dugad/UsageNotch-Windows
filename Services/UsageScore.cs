using UsageNotch.Models;

namespace UsageNotch.Services;

public sealed record ScoreEntry(string Id, string Name, string Window, string Label, double Used, double? Previous);

/// <summary>
/// One number for all your providers: the average of how much of each provider's weekly limit you've used (its main limit
/// when it has no weekly one), 0–100. Previous is the same average over each provider's last finished weekly period, and only
/// when every provider has one, so the two numbers always compare like with like.
/// </summary>
public sealed record UsageScore(int Value, int? Previous, IReadOnlyList<ScoreEntry> Providers)
{
    public static UsageScore? Compute(IReadOnlyList<ProviderViewModel> items, Func<ProviderViewModel, LimitWindow, WindowSummary?> summaryFor)
    {
        var entries = new List<ScoreEntry>();
        foreach (var p in items.Where(i => i.Status is SnapshotStatus.Ok or SnapshotStatus.Stale))
        {
            var weekly = p.WeeklyWindow is { UsedFraction: not null } wk && wk.Id != p.SessionWindow?.Id ? wk : null;
            var w = weekly ?? p.PrimaryWindow;
            if (w?.UsedFraction is not { } used || !double.IsFinite(used)) continue;
            double? previous = null;
            if (weekly is not null && summaryFor(p, weekly) is { Unused.Count: > 0 } s) previous = Math.Clamp(s.Unused[^1].Peak, 0, 1);
            entries.Add(new ScoreEntry(p.Id, p.DisplayName, w.Id, w.Label, Math.Round(Math.Clamp(used, 0, 1), 4), previous));
        }
        if (entries.Count == 0) return null;
        static int Percent(double v) => (int)Math.Round(Math.Round(v * 100, 6), MidpointRounding.AwayFromZero);
        var value = Percent(entries.Average(e => e.Used));
        int? before = entries.All(e => e.Previous is not null) ? Percent(entries.Average(e => e.Previous!.Value)) : null;
        return new UsageScore(value, before, entries);
    }

    /// <summary>"Busy", "Moderate", "Light": a word for the number, as on the phone.</summary>
    public static string Word(int value) => value switch { >= 80 => "Near your limits", >= 50 => "Busy", >= 20 => "Moderate", _ => "Light" };
}

/// <summary>Plans' US list prices a month, for comparing with the API-price equivalent of your tokens. Unknown plans return null.</summary>
public static class PlanPrices
{
    public static double? For(string kind, string? plan) => (kind, plan?.Trim()) switch
    {
        ("claude", "Pro") => 20,
        ("claude", "Max 5×" or "Max 5x") => 100,
        ("claude", "Max 20×" or "Max 20x") => 200,
        _ => null
    };
}

public sealed record DayNote(DateTime Date, string Text, long At)
{
    public const int MaxLength = 140;
    /// <summary>One line, no control characters, at most 140 characters.</summary>
    public static string Clean(string? text)
    {
        var line = new string((text ?? "").Select(c => c is '\r' or '\n' or '\t' ? ' ' : c).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return line.Length > MaxLength ? line[..MaxLength].TrimEnd() : line;
    }
    public bool Deleted => Text.Length == 0;
}

public sealed record PlanChange(DateTimeOffset At, string Plan);
public sealed record UnusualDay(DateTime Date, double Ratio, double Typical);
