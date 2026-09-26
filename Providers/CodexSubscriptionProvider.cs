using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UsageNotch.Models;

namespace UsageNotch.Providers;

public sealed class CodexSubscriptionProvider : IUsageProvider
{
    private const string Glyph = "\u2726";
    public string Id => "codex";
    public string DisplayName => "Codex";

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var exe = FindCodex();
        if (exe is null)
            return NeedsAuth("Codex was not found. Open the Codex VS Code extension once, or install the Codex CLI, then refresh.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = Process.Start(CreateStartInfo(exe));
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
            return new ProviderSnapshot(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.Ok, windows,
                ManageUrl: "https://chatgpt.com/codex/settings/usage", FetchedAt: DateTimeOffset.Now, AccountName: string.IsNullOrWhiteSpace(accountName) ? null : accountName);
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

    private static List<LimitWindow> ParseRateLimits(JsonElement document)
    {
        var windows = new List<LimitWindow>();
        if (!document.TryGetProperty("result", out var result)) return windows;
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
            foreach (var property in byId.EnumerateObject()) AddRateLimit(property.Value, property.Name, windows);
        if (windows.Count == 0 && result.TryGetProperty("rateLimits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
            AddRateLimit(rateLimits, GetString(rateLimits, "limitId") ?? "default", windows);
        return windows;
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
    }

    private static ProcessStartInfo CreateStartInfo(string exe)
    {
        string fileName;
        string arguments;
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            arguments = $"/d /s /c \"\"{exe}\" app-server --stdio\"";
        }
        else
        {
            fileName = exe;
            arguments = "app-server --stdio";
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
            info.Environment["CODEX_HOME"] = Path.Combine(profile, ".codex");
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

    private static string? FindCodex()
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

    private ProviderSnapshot NeedsAuth(string text) => new(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.NeedsAuth, [], text);
    private ProviderSnapshot Error(string text) => new(Id, DisplayName, Glyph, Fidelity.Official, SnapshotStatus.Error, [], text);
}
