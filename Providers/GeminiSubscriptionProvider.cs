using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UsageNotch.Models;
using UsageNotch.Services;

namespace UsageNotch.Providers;

public sealed class GeminiSubscriptionProvider : IUsageProvider
{
    private const string Glyph = "\u25C6";
    private readonly HttpClient _http;
    private string? _cachedAccessToken;
    private DateTimeOffset _cachedAccessTokenExpiry;
    private string? _cachedProject;
    private readonly AppSettings _settings;
    private DateTime _credentialsWriteTime;
    private DateTimeOffset _retryAfter;

    public string Id => "gemini";
    public string DisplayName => "Gemini";

    public GeminiSubscriptionProvider(HttpClient http, AppSettings? settings = null)
    {
        _http = http;
        _settings = settings ?? new AppSettings();
    }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var credentialsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");
        if (!File.Exists(credentialsPath))
            return NeedsAuth("Google sign-in is incomplete. Open Settings → Connect Gemini. This uses your personal subscription and ignores conflicting Cloud credentials.");
        if (DateTimeOffset.UtcNow < _retryAfter)
            return Error("Google's quota service is rate-limited. Retrying after " + _retryAfter.ToLocalTime().ToString("h:mm tt") + ".");

        try
        {
            var accessToken = await GetAccessToken(credentialsPath, ct);
            var configuredProject = _settings.GeminiUseCloudProject ? ProjectFromEnvironment() : null;
            var metadata = new { ideType = "IDE_UNSPECIFIED", platform = "PLATFORM_UNSPECIFIED", pluginType = "GEMINI" };
            object loadBody = string.IsNullOrWhiteSpace(configuredProject)
                ? new { metadata }
                : new { cloudaicompanionProject = configuredProject, metadata };
            using var load = await Post("loadCodeAssist", loadBody, accessToken, ct);

            var root = load.RootElement;
            _cachedProject = GetString(root, "cloudaicompanionProject") ?? configuredProject;
            var tier = ReadTierName(root);
            if (string.IsNullOrWhiteSpace(_cachedProject) && IsUnsupportedClient(root))
                return new ProviderSnapshot(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.Unsupported, [],
                    "Google no longer supports this Gemini CLI connection for personal accounts and directs this account to Antigravity. Signing in again will not restore quota access here.",
                    "https://antigravity.google", DateTimeOffset.Now);
            if (string.IsNullOrWhiteSpace(_cachedProject))
                return NeedsAuth("Open Gemini CLI once and complete Google sign-in so it can finish account setup.");

            using var quota = await Post("retrieveUserQuota", new { project = _cachedProject }, accessToken, ct);
            var windows = ParseQuota(quota.RootElement);
            if (windows.Count == 0)
                return Error("Gemini connected, but Google returned no model quota buckets.");

            return new ProviderSnapshot(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.Ok, windows,
                string.IsNullOrWhiteSpace(tier) ? null : tier,
                "https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/quota-and-pricing.md",
                DateTimeOffset.Now);
        }
        catch (GeminiServiceException ex) when (ex.Reason == "SERVICE_DISABLED")
        {
            return NeedsAuth("Google reports that Cloud Code API is disabled for the credential's project. Use Settings → Connect Gemini to sign in with your personal Google AI Pro account. Do not enable billing just to use this monitor.");
        }
        catch (GeminiServiceException ex) when (ex.StatusCode is 400 or 401 or 403)
        {
            _cachedAccessToken = null;
            return NeedsAuth(ex.StatusCode == 403
                ? "Google denied quota access. Open Settings → Connect Gemini and use your subscription account. Workspace accounts may need an administrator."
                : "Gemini login expired or was rejected. Open Settings → Connect Gemini, then refresh.");
        }
        catch (GeminiServiceException ex) when (ex.StatusCode == 429)
        {
            _retryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
            return Error("Google's quota service is rate-limited. Retrying automatically in 5 minutes.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Error("Gemini usage request timed out."); }
        catch (Exception ex) { return Error(ex.Message); }
    }

    private async Task<string> GetAccessToken(string credentialsPath, CancellationToken ct)
    {
        var modified = File.GetLastWriteTimeUtc(credentialsPath);
        if (modified != _credentialsWriteTime)
        {
            _cachedAccessToken = null;
            _cachedProject = null;
            _credentialsWriteTime = modified;
        }
        if (!string.IsNullOrWhiteSpace(_cachedAccessToken) && _cachedAccessTokenExpiry > DateTimeOffset.UtcNow.AddMinutes(2))
            return _cachedAccessToken;

        using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(credentialsPath, ct));
        var root = credentials.RootElement;
        var accessToken = GetString(root, "access_token");
        var refreshToken = GetString(root, "refresh_token");
        var expiry = GetExpiry(root);

        if (!string.IsNullOrWhiteSpace(accessToken) && expiry > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            _cachedAccessToken = accessToken;
            _cachedAccessTokenExpiry = expiry;
            return accessToken;
        }
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("Gemini credentials are expired. Run `gemini` and sign in again.");

        var client = GeminiOAuthClient.Load();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = client.Id,
            ["client_secret"] = client.Secret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        });
        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        using var refreshed = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode)
            throw new GeminiServiceException((int)response.StatusCode, "Gemini login could not be refreshed.");

        _cachedAccessToken = GetString(refreshed.RootElement, "access_token")
            ?? throw new InvalidOperationException("Google returned no Gemini access token.");
        var expiresIn = GetDouble(refreshed.RootElement, "expires_in") ?? 3600;
        _cachedAccessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
        return _cachedAccessToken;
    }

    private async Task<JsonDocument> Post(string method, object body, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://cloudcode-pa.googleapis.com/v1internal:{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("UsageNotch/1.2");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var reason = "";
            try
            {
                using var error = JsonDocument.Parse(json);
                if (error.RootElement.TryGetProperty("error", out var bodyError) && bodyError.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                    foreach (var detail in details.EnumerateArray())
                        if (GetString(detail, "reason") is string errorReason) reason = errorReason;
            }
            catch (JsonException) { }
            throw new GeminiServiceException((int)response.StatusCode, $"Gemini usage endpoint returned {(int)response.StatusCode}.", reason);
        }
        return JsonDocument.Parse(json);
    }

    private static List<LimitWindow> ParseQuota(JsonElement root)
    {
        var windows = new List<LimitWindow>();
        if (!root.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) return windows;
        var index = 0;
        foreach (var bucket in buckets.EnumerateArray())
        {
            var remaining = GetDouble(bucket, "remainingFraction");
            var modelId = GetString(bucket, "modelId");
            if (!remaining.HasValue || string.IsNullOrWhiteSpace(modelId)) continue;
            var tokenType = GetString(bucket, "tokenType") ?? "REQUESTS";
            var remainingAmount = GetDouble(bucket, "remainingAmount");
            DateTimeOffset? reset = null;
            var resetText = GetString(bucket, "resetTime");
            if (DateTimeOffset.TryParse(resetText, out var parsed)) reset = parsed;

            double? total = null;
            double? usedAmount = null;
            if (remainingAmount.HasValue && remaining.Value > 0)
            {
                total = Math.Round(remainingAmount.Value / remaining.Value);
                usedAmount = Math.Max(0, total.Value - remainingAmount.Value);
            }
            var typeLabel = tokenType.Equals("REQUESTS", StringComparison.OrdinalIgnoreCase) ? "requests" : tokenType.Replace('_', ' ').ToLowerInvariant();
            var detail = remainingAmount.HasValue ? $"{remainingAmount:0} {typeLabel} left" : $"{remaining.Value:P0} remaining";
            windows.Add(new LimitWindow($"{modelId}-{tokenType}-{index++}", FriendlyModelName(modelId),
                Math.Clamp(1 - remaining.Value, 0, 1), reset, detail, usedAmount, total, typeLabel));
        }
        return windows;
    }

    private static string FriendlyModelName(string modelId)
    {
        var value = modelId.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) ? modelId[7..] : modelId;
        var words = value.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.Equals("preview", StringComparison.OrdinalIgnoreCase))
            .Select(word => word.Equals("pro", StringComparison.OrdinalIgnoreCase) || word.Equals("flash", StringComparison.OrdinalIgnoreCase)
                ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant() : word);
        return "Gemini " + string.Join(' ', words);
    }

    private static string? ReadTierName(JsonElement root)
    {
        foreach (var key in new[] { "paidTier", "currentTier" })
            if (root.TryGetProperty(key, out var tier) && tier.ValueKind == JsonValueKind.Object)
            {
                var name = GetString(tier, "name") ?? GetString(tier, "description");
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        return null;
    }

    private static bool IsUnsupportedClient(JsonElement root)
    {
        if (!root.TryGetProperty("ineligibleTiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array) return false;
        return tiers.EnumerateArray().Any(tier => GetString(tier, "reasonCode") == "UNSUPPORTED_CLIENT");
    }

    private static DateTimeOffset GetExpiry(JsonElement root)
    {
        if (root.TryGetProperty("expiry_date", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var milliseconds))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); } catch { }
        }
        return DateTimeOffset.MinValue;
    }

    private static string? ProjectFromEnvironment() =>
        Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT_ID");
    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? GetDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private ProviderSnapshot NeedsAuth(string text) => new(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.NeedsAuth, [], text);
    private ProviderSnapshot Error(string text) => new(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.Error, [], text);

    private sealed class GeminiServiceException(int statusCode, string message, string reason = "") : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Reason { get; } = reason;
    }
}
