using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UsageNotch.Models;
using UsageNotch.Services;

namespace UsageNotch.Providers;

public sealed class CodexSubscriptionProvider : IUsageProvider
{
    private const string Glyph = "\u2726";
    private readonly string? _home;
    public string Id { get; } = "codex";
    public string DisplayName { get; } = "Codex";

    public CodexSubscriptionProvider() { }

    /// <summary>An extra Codex account with its own CODEX_HOME folder, so its sign-in stays separate.</summary>
    public CodexSubscriptionProvider(ExtraAccount account) { Id = account.ProviderId; DisplayName = account.DisplayName; _home = account.Folder; }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var exe = FindCodex();
        if (exe is null)
            return NeedsAuth("Codex was not found. Open the Codex VS Code extension once, or install the Codex CLI, then refresh.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = Process.Start(CreateStartInfo(exe, _home));
        if (process is null) return Error("Could not start the Codex app-server.");

        try
        {
            await Send(process, new
            {
                id = 1,
                method = "initialize",
                @params = new { clientInfo = new { name = "usage-notch", title = "UsageNotch", version = "1.2.0" } }
            });
            using var initialize = await ReadResponse(process, 1, timeout.Token);
            if (initialize is null) return await ProcessFailure(process, "Codex app-server did not initialize.");
            if (TryGetRpcError(initialize.RootElement, out var initializeError))
                return Error($"Codex initialization failed: {initializeError}");

            await Send(process, new { method = "initialized", @params = new { } });
            await Send(process, new { id = 2, method = "account/rateLimits/read", @params = new { } });

            using var answer = await ReadResponse(process, 2, timeout.Token);
            if (answer is null) return await ProcessFailure(process, "Codex returned no rate-limit response.");
            if (TryGetRpcError(answer.RootElement, out var rpcError))
            {
                if (rpcError.Contains("login", StringComparison.OrdinalIgnoreCase) || rpcError.Contains("auth", StringComparison.OrdinalIgnoreCase))
                    return NeedsAuth("Open Codex in VS Code and sign in with your ChatGPT account, then refresh.");
                return Error($"Codex returned: {rpcError}");
            }

            var windows = ParseRateLimits(answer.RootElement);
            if (windows.Count == 0) return Error("Codex is connected, but this account returned no usage windows.");
            var (plan, status) = PlanAndStatus(answer.RootElement);
            string? accountName = null;
            // Ask the app-server for its account label; never decode or copy auth files.
            try
            {
                await Send(process, new { id = 3, method = "account/read", @params = new { refreshToken = false } });
                using var identityTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                identityTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var identity = await ReadResponse(process, 3, identityTimeout.Token);
                if (identity?.RootElement.TryGetProperty("result", out var result) == true &&
                    result.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object &&
                    account.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String)
                    accountName = new string((email.GetString() ?? "").Where(c => !char.IsControl(c)).Take(160).ToArray());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Identity must not hide valid usage. */ }
            AccountTokens? tokens = null;
            // OpenAI's own token totals for this account, across every device: more complete than this PC's transcripts.
            try
            {
                await Send(process, new { id = 4, method = "account/usage/read", @params = new { } });
                using var usageTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                usageTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                using var usage = await ReadResponse(process, 4, usageTimeout.Token);
                if (usage is not null) tokens = ParseTokenUsage(usage.RootElement);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Token totals must not hide valid usage. */ }
            return new ProviderSnapshot(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.Ok, windows, status,
                ManageUrl: "https://chatgpt.com/codex/settings/usage", FetchedAt: DateTimeOffset.Now, AccountName: string.IsNullOrWhiteSpace(accountName) ? null : accountName, Plan: plan, Tokens: tokens);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return await ProcessFailure(process, "Codex took too long to respond. Open Codex in VS Code once, then refresh.");
        }
        catch (Exception ex)
        {
            var message = CleanError(ex.Message);
            return Error(string.IsNullOrWhiteSpace(message) ? "Codex app-server could not be started." : message);
        }
        finally { TryStop(process); }
    }

    public static List<LimitWindow> ParseRateLimits(JsonElement document)
    {
        var windows = new List<LimitWindow>();
        if (!document.TryGetProperty("result", out var result)) return windows;
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
            foreach (var property in byId.EnumerateObject()) AddRateLimit(property.Value, property.Name, windows);
        if (windows.Count == 0 && result.TryGetProperty("rateLimits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
            AddRateLimit(rateLimits, GetString(rateLimits, "limitId") ?? "default", windows);
        // Free full resets OpenAI grants now and then: worth knowing before you hit a limit.
        if (result.TryGetProperty("rateLimitResetCredits", out var resets) && resets.ValueKind == JsonValueKind.Object
            && resets.TryGetProperty("credits", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var available = list.EnumerateArray().Where(c => GetString(c, "status") == "available").ToList();
            if (available.Count > 0)
            {
                var first = available.MinBy(c => GetLong(c, "expiresAt") ?? long.MaxValue);
                var expires = GetLong(first, "expiresAt") is { } e ? DateTimeOffset.FromUnixTimeSeconds(e) : (DateTimeOffset?)null;
                windows.Add(new LimitWindow("codex-reset-credits", available.Count == 1 ? "Free limit reset" : $"{available.Count} free limit resets", null, expires,
                    (GetString(first, "title") ?? "Full reset") + (expires is { } at ? $" · use by {at.ToLocalTime():MMM d}" : "") + " · redeem it in Codex"));
            }
        }
        return windows;
    }

    /// <summary>The ChatGPT plan ("Plus", "Edu", "Business"…) and, when a limit or credits ran out, why.</summary>
    public static (string? Plan, string? Status) PlanAndStatus(JsonElement document)
    {
        if (!document.TryGetProperty("result", out var result) || !result.TryGetProperty("rateLimits", out var limits) || limits.ValueKind != JsonValueKind.Object) return (null, null);
        var plan = GetString(limits, "planType") switch
        {
            "free" => "Free", "go" => "Go", "plus" => "Plus", "pro" => "Pro", "prolite" => "Pro Lite", "team" => "Team",
            "business" or "self_serve_business_prolite" or "self_serve_business_usage_based" => "Business",
            "enterprise" or "ent26" or "enterprise_cbp_automation" or "enterprise_cbp_usage_based" => "Enterprise",
            "edu" => "Edu", "edu_plus" => "Edu Plus", "edu_pro" => "Edu Pro",
            _ => null
        };
        var status = GetString(limits, "rateLimitReachedType") switch
        {
            "rate_limit_reached" => "A usage limit is reached; Codex works again after it resets.",
            "workspace_owner_credits_depleted" => "Your workspace is out of credits. Add credits in ChatGPT to keep using Codex.",
            "workspace_member_credits_depleted" => "Your workspace is out of credits. Ask a workspace owner to add more.",
            "workspace_owner_usage_limit_reached" => "Your workspace reached its usage limit. You can raise it in ChatGPT's workspace settings.",
            "workspace_member_usage_limit_reached" => "Your workspace reached its usage limit. Ask a workspace owner, or request more usage in ChatGPT.",
            _ => limits.TryGetProperty("spendControlReached", out var spend) && spend.ValueKind == JsonValueKind.True ? "Your spending limit is reached." : null
        };
        return (plan, status);
    }

    private static void AddRateLimit(JsonElement rateLimit, string limitId, List<LimitWindow> output)
    {
        var limitName = GetString(rateLimit, "limitName");
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (!rateLimit.TryGetProperty(slot, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            var usedPercent = GetDouble(window, "usedPercent");
            var minutes = GetDouble(window, "windowDurationMins");
            var resetUnix = GetLong(window, "resetsAt");
            DateTimeOffset? reset = resetUnix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(resetUnix.Value) : null;
            var windowName = minutes switch
            {
                300 => "5-hour window",
                10080 => "Weekly window",
                >= 40000 => "Monthly window",
                _ when minutes.HasValue => $"{minutes:0}-minute window",
                _ => slot == "primary" ? "Primary limit" : "Secondary limit"
            };
            var label = string.IsNullOrWhiteSpace(limitName) || limitName.Equals("Codex", StringComparison.OrdinalIgnoreCase)
                ? windowName : $"{limitName} · {windowName}";
            output.Add(new LimitWindow($"{limitId}-{slot}", label,
                usedPercent.HasValue ? Math.Clamp(usedPercent.Value / 100.0, 0, 1) : null, reset));
        }
        // Edu, Business and Enterprise seats: a monthly allowance of credits ("0 of 150 credits used").
        if (rateLimit.TryGetProperty("individualLimit", out var individual) && individual.ValueKind == JsonValueKind.Object)
        {
            var limit = ParseAmount(individual, "limit"); var used = ParseAmount(individual, "used");
            var remaining = GetDouble(individual, "remainingPercent");
            double? fraction = remaining.HasValue ? Math.Clamp(1 - remaining.Value / 100.0, 0, 1) : limit > 0 && used.HasValue ? Math.Clamp(used.Value / limit.Value, 0, 1) : null;
            var resetsAt = GetLong(individual, "resetsAt");
            output.Add(new LimitWindow($"{limitId}-monthly", "Monthly limit", fraction, resetsAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value) : null,
                "Credits shared across Codex and other ChatGPT agents", used, limit, "credits"));
        }
        // Pay-as-you-go credits you bought: a balance, not a percentage.
        if (rateLimit.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object
            && (credits.TryGetProperty("hasCredits", out var has) && has.ValueKind == JsonValueKind.True || credits.TryGetProperty("unlimited", out var unl) && unl.ValueKind == JsonValueKind.True))
        {
            var unlimited = credits.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True;
            var balance = ParseAmount(credits, "balance");
            output.Add(new LimitWindow($"{limitId}-credits", "Credits", null, null, unlimited ? "Unlimited credits" : balance is { } b ? $"{b:0.##} credits left" : "Available to this account", null, null, null));
        }
    }

    public static ProcessStartInfo CreateStartInfo(string exe, string? home = null, string verb = "app-server --stdio")
    {
        string fileName;
        string arguments;
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            arguments = $"/d /s /c \"\"{exe}\" {verb}\"";
        }
        else
        {
            fileName = exe;
            arguments = verb;
        }
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            info.Environment["USERPROFILE"] = profile;
            info.Environment["HOME"] = profile;
            info.Environment["CODEX_HOME"] = home ?? Path.Combine(profile, ".codex");
        }
        return info;
    }

    private static async Task Send(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonDocument?> ReadResponse(Process process, int id, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null) return null;
            try
            {
                var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number &&
                    responseId.TryGetInt32(out var value) && value == id) return document;
                document.Dispose();
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static bool TryGetRpcError(JsonElement root, out string message)
    {
        message = "Unknown app-server error";
        if (!root.TryGetProperty("error", out var error)) return false;
        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String)
            message = value.GetString() ?? message;
        else message = error.ToString();
        return true;
    }

    private async Task<ProviderSnapshot> ProcessFailure(Process process, string fallback)
    {
        TryStop(process);
        try
        {
            var stderr = CleanError(await process.StandardError.ReadToEndAsync());
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                if (stderr.Contains("login", StringComparison.OrdinalIgnoreCase) || stderr.Contains("auth", StringComparison.OrdinalIgnoreCase))
                    return NeedsAuth("Open Codex in VS Code and sign in with your ChatGPT account, then refresh.");
                return Error(stderr);
            }
        }
        catch { }
        return Error(fallback);
    }

    private static void TryStop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    public static string? FindCodex()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(appData, "npm", "codex.cmd"));
        candidates.Add(Path.Combine(localData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
        candidates.Add(Path.Combine(localData, "Programs", "OpenAI", "Codex", "codex.exe"));
        AddExtensionCandidates(Path.Combine(profile, ".vscode", "extensions"), candidates);
        AddExtensionCandidates(Path.Combine(profile, ".vscode-insiders", "extensions"), candidates);
        AddExtensionCandidates(Path.Combine(profile, ".cursor", "extensions"), candidates);
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
        foreach (var command in new[] { "codex.cmd", "codex.exe", "codex" })
            if (CommandExists(command)) return command;
        return null;
    }

    private static void AddExtensionCandidates(string root, List<string> candidates)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root, "openai.chatgpt-*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                candidates.Add(Path.Combine(directory, "bin", "windows-x86_64", "codex.exe"));
                candidates.Add(Path.Combine(directory, "bin", "windows-arm64", "codex.exe"));
            }
        }
        catch { }
    }

    private static bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("where.exe", command)
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            process?.WaitForExit(1500);
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string CleanError(string text)
    {
        var line = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(value => !value.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(line) ? "" : line.Trim();
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? GetDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    private static long? GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    /// <summary>account/usage/read: lifetime and peak tokens, streaks and daily buckets (dates are the server's days).</summary>
    public static AccountTokens? ParseTokenUsage(JsonElement document)
    {
        if (!document.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return null;
        long? Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : null;
        var summary = result.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var days = new List<AccountTokenDay>();
        if (result.TryGetProperty("dailyUsageBuckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
            foreach (var b in buckets.EnumerateArray().TakeLast(400))
                if (b.ValueKind == JsonValueKind.Object && GetString(b, "startDate") is { } date && DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) && Long(b, "tokens") is { } t)
                    days.Add(new AccountTokenDay(d, t));
        if (summary.ValueKind != JsonValueKind.Object && days.Count == 0) return null;
        return summary.ValueKind == JsonValueKind.Object
            ? new AccountTokens(Long(summary, "lifetimeTokens"), Long(summary, "peakDailyTokens"), (int?)Long(summary, "currentStreakDays"), (int?)Long(summary, "longestStreakDays"), days)
            : new AccountTokens(null, null, null, null, days);
    }

    private static double? ParseAmount(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)) return d;
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private ProviderSnapshot NeedsAuth(string text) => new(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.NeedsAuth, [], text);
    private ProviderSnapshot Error(string text) => new(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.Error, [], text);
}
