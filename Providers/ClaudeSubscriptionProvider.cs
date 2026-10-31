using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using UsageNotch.Services;
using UsageNotch.Models;

namespace UsageNotch.Providers;

public sealed class ClaudeSubscriptionProvider : IUsageProvider
{
    private readonly HttpClient _http;
    private ProviderSnapshot? _lastGood;
    private DateTimeOffset _nextAttempt;
    private int _rateLimitCount;
    private readonly bool _usesDefaultCredentials;
    private readonly string _credentialsPath;
    private readonly string? _cachePath;
    private string? _accountKey;
    private string? _rejectedToken;
    private bool _cacheLoaded;
    private string? _accountName;
    private sealed record UsageCache(string AccountKey, ProviderSnapshot? Snapshot, DateTimeOffset RetryAfter, int RateLimits);
    private readonly string? _profilePath;
    public string Id { get; }
    public string DisplayName { get; }

    public ClaudeSubscriptionProvider(HttpClient http, string? credentialsPath = null, string? cachePath = null)
    {
        _http = http;
        Id = "claude"; DisplayName = "Claude";
        _usesDefaultCredentials = credentialsPath is null;
        _credentialsPath = credentialsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        _cachePath = cachePath ?? (credentialsPath is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageNotch", "claude-usage.cache") : null);
        if (_usesDefaultCredentials) _profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
    }

    /// <summary>
    /// An extra Claude Code account kept in its own configuration folder (Claude Code's CLAUDE_CONFIG_DIR). It has its own
    /// sign-in, cache and 429 cooldown, so accounts never share readings.
    /// </summary>
    public ClaudeSubscriptionProvider(HttpClient http, ExtraAccount account)
    {
        _http = http;
        Id = account.ProviderId; DisplayName = account.DisplayName;
        _credentialsPath = Path.Combine(account.Folder, ".credentials.json");
        _profilePath = Path.Combine(account.Folder, ".claude.json");
        _cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageNotch", $"claude-usage-{account.Slug}.cache");
    }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var path = _credentialsPath;
        if (!File.Exists(path)) { _lastGood = null; return NeedsAuth("Sign in to Claude Code in VS Code, then refresh this dock. No API key is needed."); }

        string? token;
        long? expiresAt = null;
        string? plan = null;
        try
        {
            using var creds = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            var oauth = creds.RootElement.GetProperty("claudeAiOauth");
            token = oauth.GetProperty("accessToken").GetString();
            if (oauth.TryGetProperty("expiresAt", out var expires) && expires.TryGetInt64(out var timestamp)) expiresAt = timestamp;
            plan = PlanName(oauth.TryGetProperty("subscriptionType", out var sub) ? sub.GetString() : null, oauth.TryGetProperty("rateLimitTier", out var tier) ? tier.GetString() : null);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Error("Claude Code is updating its sign-in file. The next check will read it again."); }
        if (string.IsNullOrWhiteSpace(token)) { _lastGood = null; return NeedsAuth("Claude Code is not signed in."); }
        var key = AccountKey(token);
        if (_accountKey != key) { _lastGood = null; _nextAttempt = default; _rateLimitCount = 0; _cacheLoaded = false; _accountKey = key; }
        if (!_cacheLoaded) LoadCache();
        if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            return NeedsAuth("Claude Code sign-in has expired. Open Claude Code in VS Code and sign in again (or use /login), then press Refresh. Your Claude desktop-app login is separate.");
        if (_rejectedToken == token) return NeedsAuth("Claude rejected this sign-in. Reconnect Claude Code in VS Code, then press Refresh. The dock will automatically read the renewed credentials.");
        if (DateTimeOffset.UtcNow < _nextAttempt) return RateLimited();
        if (_lastGood?.FetchedAt > DateTimeOffset.UtcNow.AddMinutes(-2)) return _lastGood with { AccountName = _accountName ?? _lastGood.AccountName };

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var res = await _http.SendAsync(req, ct);
            if ((int)res.StatusCode == 401) { _rejectedToken = token; return NeedsAuth("Claude rejected this sign-in. Reconnect Claude Code in VS Code, then press Refresh."); }
            if ((int)res.StatusCode == 403) return Error("Claude denied access to usage data (403). Check Claude's usage page; repeatedly signing in may not resolve an account-access restriction.");
            if ((int)res.StatusCode == 429)
            {
                _rateLimitCount = Math.Min(_rateLimitCount + 1, 5);
                var fallback = TimeSpan.FromMinutes(Math.Min(30, 2 * Math.Pow(2, _rateLimitCount - 1)));
                var delay = res.Headers.RetryAfter?.Delta
                    ?? (res.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? fallback;
                _nextAttempt = DateTimeOffset.UtcNow + (delay > TimeSpan.FromSeconds(30) ? delay : TimeSpan.FromSeconds(30));
                SaveCache();
                return RateLimited();
            }
            if (!res.IsSuccessStatusCode) return Error($"Claude usage endpoint returned {(int)res.StatusCode}.");

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var windows = new List<LimitWindow>();
            AddWindow(doc.RootElement, "five_hour", "Current session", windows);
            AddWindow(doc.RootElement, "seven_day", "All models", windows);
            AddWindow(doc.RootElement, "seven_day_opus", "Opus weekly", windows);
            AddWindow(doc.RootElement, "seven_day_sonnet", "Sonnet weekly", windows);
            // Every other limit the account reports (new model buckets, promotional credit grants…), so none is missed.
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (KnownWindows.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.Object || !property.Value.TryGetProperty("utilization", out _)) continue;
                AddWindow(doc.RootElement, property.Name, LabelFor(property.Name, property.Value), windows);
            }
            var breakdown = Breakdown(doc.RootElement);

            if (doc.RootElement.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object)
            {
                var enabled = extra.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.True;
                if (enabled)
                {
                    double? limitCents = GetDouble(extra, "monthly_limit");
                    double? usedCents = GetDouble(extra, "used_credits");
                    double? frac = null;
                    if (limitCents > 0 && usedCents.HasValue) frac = usedCents.Value / limitCents.Value;
                    windows.Add(new LimitWindow("extra_usage", "Usage credits", frac, null,
                        "Pay-as-you-go usage after plan limits", usedCents / 100.0, limitCents / 100.0, "USD"));
                }
            }

            if (windows.Count == 0) return Error("Claude returned no usage windows understood by this build.");
            _rateLimitCount = 0;
            _nextAttempt = default;
            _lastGood = new ProviderSnapshot(Id, DisplayName, "✳", Fidelity.Official, SnapshotStatus.Ok, windows,
                ManageUrl: "https://claude.ai/settings/usage", FetchedAt: DateTimeOffset.Now, AccountName: _accountName, Plan: plan, Breakdown: breakdown);
            _rejectedToken = null;
            SaveCache();
            return _lastGood;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Error("Claude request timed out."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Error(ex.Message); }
    }

    private static readonly HashSet<string> KnownWindows = ["five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", "extra_usage"];

    /// <summary>One limit: utilization (a precise percentage), its reset, a lock reason, and dollar amounts for credit grants.</summary>
    private static void AddWindow(JsonElement root, string key, string label, List<LimitWindow> output)
    {
        if (!root.TryGetProperty(key, out var item) || item.ValueKind != JsonValueKind.Object) return;
        var utilization = GetDouble(item, "utilization");
        DateTimeOffset? reset = null;
        if (item.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var parsed)) reset = parsed;
        var limitDollars = GetDouble(item, "limit_dollars"); var usedDollars = GetDouble(item, "used_dollars");
        var locked = item.TryGetProperty("locked_reason", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        output.Add(new LimitWindow(key, label, utilization.HasValue ? Math.Clamp(utilization.Value / 100.0, 0, 10) : null, reset,
            locked is { Length: > 0 } ? "Locked: " + locked.Replace('_', ' ') : null,
            limitDollars.HasValue ? usedDollars ?? 0 : null, limitDollars, limitDollars.HasValue ? "USD" : null));
    }

    /// <summary>A readable name for a limit Claude reports under an internal key.</summary>
    public static string LabelFor(string key, JsonElement item)
    {
        if (GetDouble(item, "limit_dollars") is { } dollars) return $"Usage credit · ${dollars:0.##}";
        return key switch
        {
            "seven_day_oauth_apps" => "Other apps weekly",
            "seven_day_cowork" => "Cowork weekly",
            _ when key.StartsWith("seven_day_") => "Weekly · " + Humanize(key["seven_day_".Length..]),
            _ => Humanize(key)
        };
    }

    private static string Humanize(string key) => key.Length == 0 ? "Limit" : char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');

    /// <summary>Where this week's usage went (Claude Code, chat, …), as Claude reports it.</summary>
    private static IReadOnlyList<UsageShare>? Breakdown(JsonElement root)
    {
        if (!root.TryGetProperty("seven_day_breakdown", out var b) || b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;
        var list = rows.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => new UsageShare(x.TryGetProperty("display_name", out var n) && n.ValueKind == JsonValueKind.String ? new string((n.GetString() ?? "").Where(c => !char.IsControl(c)).Take(40).ToArray()) : "Other", GetDouble(x, "percent") ?? 0))
            .Where(x => x.Percent > 0).Take(8).ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>"Max 5×" from Claude Code's subscription type and rate-limit tier; null when unknown.</summary>
    public static string? PlanName(string? subscription, string? tier)
    {
        var multiplier = tier is null ? null : System.Text.RegularExpressions.Regex.Match(tier, @"(\d+)x") is { Success: true } m ? m.Groups[1].Value + "×" : null;
        return subscription?.ToLowerInvariant() switch
        {
            "max" => "Max" + (multiplier is null ? "" : " " + multiplier),
            "pro" => "Pro", "team" => "Team", "enterprise" => "Enterprise", "free" => "Free",
            { Length: > 0 } other => char.ToUpperInvariant(other[0]) + other[1..],
            _ => null
        };
    }

    private static double? GetDouble(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : null;

    private string AccountKey(string token)
    {
        string identity = token;
        _accountName = null;
        // Prefer Claude Code's account ID, so normal token renewal does not discard
        // that account's last reading. Fall back to a token hash, never another user.
        if (_profilePath is { } profile)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(profile));
                if (doc.RootElement.TryGetProperty("oauthAccount", out var account) && account.ValueKind == JsonValueKind.Object)
                {
                    if (account.TryGetProperty("accountUuid", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())) identity = id.GetString()!;
                    foreach (var field in new[] { "displayName", "emailAddress" })
                        if (account.TryGetProperty(field, out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                        { _accountName = new string(name.GetString()!.Where(c => !char.IsControl(c)).Take(160).ToArray()); break; }
                }
            }
            catch { }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private void LoadCache()
    {
        _cacheLoaded = true;
        if (_cachePath is null) return;
        try
        {
            var plaintext = SecretStore.Unprotect(File.ReadAllText(_cachePath));
            var cache = plaintext is null ? null : JsonSerializer.Deserialize<UsageCache>(plaintext);
            if (cache is null || cache.AccountKey != _accountKey) return;
            if (cache.Snapshot?.FetchedAt > DateTimeOffset.UtcNow.AddHours(-24) && cache.Snapshot.FetchedAt <= DateTimeOffset.UtcNow) _lastGood = cache.Snapshot;
            if (cache.RetryAfter < DateTimeOffset.UtcNow.AddDays(1)) _nextAttempt = cache.RetryAfter;
            _rateLimitCount = Math.Clamp(cache.RateLimits, 0, 5);
        }
        catch { }
    }

    private void SaveCache()
    {
        if (_cachePath is null || _accountKey is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var encrypted = SecretStore.Protect(JsonSerializer.Serialize(new UsageCache(_accountKey, _lastGood, _nextAttempt, _rateLimitCount)));
            var temporary = _cachePath + ".tmp";
            File.WriteAllText(temporary, encrypted);
            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch { /* Usage is still useful when local caching is unavailable. */ }
    }

    private ProviderSnapshot NeedsAuth(string text) => new(Id, DisplayName, "✳", Fidelity.Official, SnapshotStatus.NeedsAuth,
        _lastGood?.Windows ?? [], _lastGood is null ? text : $"{text}\nSaved reading from {_lastGood.FetchedAt?.ToLocalTime():g}; not live.",
        "https://claude.ai/settings/usage", _lastGood?.FetchedAt, _lastGood?.AccountName ?? _accountName);
    private ProviderSnapshot RateLimited() => Error($"Claude is rate-limiting usage checks (429), not necessarily your messages. Next check after {_nextAttempt.ToLocalTime():t}. No need to sign in again.");
    private ProviderSnapshot Error(string text) => _lastGood is { } cached
        ? cached with { Status = SnapshotStatus.Stale, StatusText = $"{text}\nSaved reading from {cached.FetchedAt?.ToLocalTime():g}; not live." }
        : new(Id, DisplayName, "✳", Fidelity.Official, SnapshotStatus.Error, [], text, "https://claude.ai/settings/usage");
}
