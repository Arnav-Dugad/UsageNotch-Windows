using System.Net.Http.Headers;
using System.Text.Json;
using UsageNotch.Models;
using UsageNotch.Services;

namespace UsageNotch.Providers;

public sealed class OpenAiApiProvider : IUsageProvider
{
    private readonly HttpClient _http; private readonly AppSettings _settings;
    public string Id => "openai_api"; public string DisplayName => "OpenAI API";
    public OpenAiApiProvider(HttpClient http, AppSettings settings) { _http = http; _settings = settings; }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var key = SecretStore.Unprotect(_settings.OpenAiAdminKeyProtected);
        if (string.IsNullOrWhiteSpace(key)) return NeedsAuth("Add an OpenAI Admin API key in UsageNotch Settings.");
        var start = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var url = $"https://api.openai.com/v1/organization/costs?start_time={start}&bucket_width=1d&limit=31";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var res = await _http.SendAsync(req, ct);
            if ((int)res.StatusCode is 401 or 403) return NeedsAuth("The OpenAI key is not an Admin key or is no longer valid.");
            if (!res.IsSuccessStatusCode) return Error($"OpenAI Costs API returned {(int)res.StatusCode}.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            double spend = 0;
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var bucket in data.EnumerateArray())
                    if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                        foreach (var row in results.EnumerateArray())
                            if (row.TryGetProperty("amount", out var amount) && amount.TryGetProperty("value", out var value) && value.TryGetDouble(out var d)) spend += d;

            var budget = _settings.OpenAiMonthlyBudgetUsd;
            double? frac = budget > 0 ? spend / budget.Value : null;
            var detail = budget > 0 ? $"${spend:0.00} of ${budget:0.00} configured budget" : $"${spend:0.00} spent this month; set a budget to get a percentage ring";
            var windows = new[] { new LimitWindow("month_cost", "This month's API spend", frac, NextMonthUtc(), detail, spend, budget, "USD") };
            return new ProviderSnapshot(Id, DisplayName, "◎", Fidelity.Official, SnapshotStatus.Ok, windows,
                ManageUrl: "https://platform.openai.com/usage", FetchedAt: DateTimeOffset.Now);
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    private static DateTimeOffset NextMonthUtc() { var n = DateTime.UtcNow.AddMonths(1); return new DateTimeOffset(n.Year, n.Month, 1, 0, 0, 0, TimeSpan.Zero); }
    private ProviderSnapshot NeedsAuth(string t) => new(Id, DisplayName, "◎", Fidelity.Official, SnapshotStatus.NeedsAuth, [], t);
    private ProviderSnapshot Error(string t) => new(Id, DisplayName, "◎", Fidelity.Official, SnapshotStatus.Error, [], t);
}
