using System.Globalization;
using System.Text.Json;
using UsageNotch.Models;
using UsageNotch.Services;

namespace UsageNotch.Providers;

public sealed class AnthropicApiProvider : IUsageProvider
{
    private readonly HttpClient _http; private readonly AppSettings _settings;
    public string Id => "anthropic_api"; public string DisplayName => "Claude API";
    public AnthropicApiProvider(HttpClient http, AppSettings settings) { _http = http; _settings = settings; }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var key = SecretStore.Unprotect(_settings.AnthropicAdminKeyProtected);
        if (string.IsNullOrWhiteSpace(key)) return NeedsAuth("Add an Anthropic Admin API key in UsageNotch Settings.");
        var start = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var end = DateTimeOffset.UtcNow.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var url = $"https://api.anthropic.com/v1/organizations/cost_report?starting_at={Uri.EscapeDataString(start)}&ending_at={Uri.EscapeDataString(end)}&limit=31";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-api-key", key);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.TryAddWithoutValidation("User-Agent", "UsageNotch/0.1.0");
        try
        {
            using var res = await _http.SendAsync(req, ct);
            if ((int)res.StatusCode is 401 or 403) return NeedsAuth("The Anthropic key is not an Admin API credential or is no longer valid.");
            if (!res.IsSuccessStatusCode) return Error($"Anthropic Cost API returned {(int)res.StatusCode}.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            decimal cents = 0;
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var bucket in data.EnumerateArray())
                    if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                        foreach (var row in results.EnumerateArray())
                            if (row.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.String && decimal.TryParse(amount.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) cents += d;
            var spend = (double)(cents / 100m); // Anthropic reports decimal strings in lowest currency units (cents).
            var budget = _settings.AnthropicMonthlyBudgetUsd;
            double? frac = budget > 0 ? spend / budget.Value : null;
            var detail = budget > 0 ? $"${spend:0.00} of ${budget:0.00} configured budget" : $"${spend:0.00} billed this month; set a budget to get a percentage ring";
            var windows = new[] { new LimitWindow("month_cost", "This month's API spend", frac, NextMonthUtc(), detail, spend, budget, "USD") };
            return new ProviderSnapshot(Id, DisplayName, "✳", Fidelity.Official, SnapshotStatus.Ok, windows,
                ManageUrl: "https://platform.claude.com/settings/usage", FetchedAt: DateTimeOffset.Now);
        }
        catch (Exception ex) { return Error(ex.Message); }
    }
    private static DateTimeOffset NextMonthUtc() { var n = DateTime.UtcNow.AddMonths(1); return new DateTimeOffset(n.Year, n.Month, 1, 0, 0, 0, TimeSpan.Zero); }
    private ProviderSnapshot NeedsAuth(string t) => new(Id, DisplayName, "✳", Fidelity.Official, SnapshotStatus.NeedsAuth, [], t);
    private ProviderSnapshot Error(string t) => new(Id, DisplayName, "✳", Fidelity.Official, SnapshotStatus.Error, [], t);
}
