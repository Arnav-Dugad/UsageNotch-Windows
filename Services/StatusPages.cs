using System.Text.Json;

namespace UsageNotch.Services;

public sealed record Incident(string Provider, string Title, string Impact, string Status, string? Url, DateTimeOffset UpdatedAt);
public sealed record ServiceHealth(string Provider, string Indicator, string Description, IReadOnlyList<Incident> Incidents, DateTimeOffset CheckedAt);

/// <summary>
/// Open incidents from the providers' public status pages (Statuspage JSON). Only these public pages are contacted, at most every
/// five minutes; nothing about you is sent. Gemini has no public status feed.
/// </summary>
public sealed class StatusPages
{
    private static readonly Dictionary<string, string> Pages = new()
    {
        ["claude"] = "https://status.claude.com/api/v2/",
        ["codex"] = "https://status.openai.com/api/v2/",
        ["cursor"] = "https://status.cursor.com/api/v2/",
    };
    private readonly HttpClient _http;
    private readonly Dictionary<string, ServiceHealth> _health = new();
    private DateTimeOffset _checked;
    public event Action? Changed;

    public StatusPages(HttpClient http) => _http = http;

    /// <summary>The page that covers a provider id (API spend shares its company's page).</summary>
    public static string? PageFor(string id) => ProviderKind.Of(id) switch { "claude" or "anthropic_api" => "claude", "codex" or "openai_api" => "codex", "cursor" => "cursor", _ => null };

    public ServiceHealth? For(string id) { lock (_health) return PageFor(id) is { } page ? _health.GetValueOrDefault(page) : null; }
    public IReadOnlyList<Incident> Active { get { lock (_health) return _health.Values.SelectMany(h => h.Incidents).OrderByDescending(i => i.UpdatedAt).ToArray(); } }

    public async Task RefreshAsync(AppSettings settings, IEnumerable<string> providerIds, bool force = false)
    {
        if (!settings.ShowIncidents) { lock (_health) _health.Clear(); return; }
        if (!force && DateTimeOffset.UtcNow - _checked < TimeSpan.FromMinutes(5)) return;
        _checked = DateTimeOffset.UtcNow;
        var pages = providerIds.Select(PageFor).Where(p => p is not null).Distinct().ToArray();
        var results = await Task.WhenAll(pages.Select(async page => (page!, await Fetch(page!))));
        lock (_health)
        {
            foreach (var key in _health.Keys.Except(pages!).ToArray()) _health.Remove(key);
            foreach (var (page, health) in results) if (health is not null) _health[page] = health;
        }
        Changed?.Invoke();
    }

    private async Task<ServiceHealth?> Fetch(string page)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var summary = JsonDocument.Parse(await GetBounded(Pages[page] + "summary.json", timeout.Token));
            var root = summary.RootElement;
            var indicator = root.TryGetProperty("status", out var status) && status.TryGetProperty("indicator", out var i) ? i.GetString() ?? "none" : "none";
            var description = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var incidents = new List<Incident>();
            if (root.TryGetProperty("incidents", out var list)) Collect(page, list, incidents, false);
            else
            {
                // Some pages leave incidents out of the summary; their incidents feed has them.
                using var feed = JsonDocument.Parse(await GetBounded(Pages[page] + "incidents.json", timeout.Token));
                if (feed.RootElement.TryGetProperty("incidents", out var all)) Collect(page, all, incidents, false);
            }
            if (root.TryGetProperty("scheduled_maintenances", out var maintenance)) Collect(page, maintenance, incidents, true);
            return new ServiceHealth(page, indicator, description, incidents.Take(4).ToArray(), DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    private async Task<string> GetBounded(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { Scheme: "https" }) throw new InvalidOperationException("Status pages must use HTTPS.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream(); var chunk = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0) { buffer.Write(chunk, 0, read); if (buffer.Length > 2_000_000) throw new InvalidOperationException("Status page too large."); }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Collect(string page, JsonElement list, List<Incident> into, bool maintenance)
    {
        if (list.ValueKind != JsonValueKind.Array) return;
        foreach (var item in list.EnumerateArray().Take(20))
        {
            var state = item.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            if (state is "resolved" or "postmortem" or "completed" or "scheduled") continue;
            var title = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (title.Length == 0) continue;
            var impact = maintenance ? "maintenance" : item.TryGetProperty("impact", out var im) ? im.GetString() ?? "minor" : "minor";
            var url = item.TryGetProperty("shortlink", out var l) && l.GetString() is { } link && link.StartsWith("https://") ? link : null;
            var updated = item.TryGetProperty("updated_at", out var u) && DateTimeOffset.TryParse(u.GetString(), out var at) ? at : DateTimeOffset.UtcNow;
            into.Add(new Incident(page, new string(title.Where(c => !char.IsControl(c)).Take(140).ToArray()), impact, state, url, updated));
        }
    }
}
