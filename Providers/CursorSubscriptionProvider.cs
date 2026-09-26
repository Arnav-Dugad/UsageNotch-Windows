using Microsoft.Data.Sqlite;
using System.Text.Json;
using UsageNotch.Models;

namespace UsageNotch.Providers;

public sealed class CursorSubscriptionProvider : IUsageProvider
{
    private readonly HttpClient _http;
    public string Id => "cursor";
    public string DisplayName => "Cursor";
    public CursorSubscriptionProvider(HttpClient http) => _http = http;

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");
        if (!File.Exists(dbPath)) return NeedsAuth("Cursor is not installed or has not created its local state database.");
        string? token = null, accountId = null;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
            await using var db = new SqliteConnection(cs);
            await db.OpenAsync(ct);
            token = await ReadValue(db, "cursorAuth/accessToken", ct);
            accountId = await ReadValue(db, "cursorAuth/stripeMembershipAuthId", ct);
        }
        catch (Exception ex) { return Error("Could not read Cursor state: " + ex.Message); }
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(accountId)) return NeedsAuth("Sign in to Cursor first.");

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
        req.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={accountId}::{token}");
        req.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var res = await _http.SendAsync(req, ct);
            if ((int)res.StatusCode is 401 or 403) return NeedsAuth("Cursor session expired; reopen Cursor and sign in.");
            if (!res.IsSuccessStatusCode) return Error($"Cursor usage endpoint returned {(int)res.StatusCode}.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            DateTimeOffset? reset = null;
            if (root.TryGetProperty("billingCycleEnd", out var end) && end.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(end.GetString(), out var dt)) reset = dt;
            var windows = new List<LimitWindow>();
            if (root.TryGetProperty("individualUsage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
                {
                    AddPercent(plan, "totalPercentUsed", "included", "Included usage", reset, windows);
                    AddPercent(plan, "apiPercentUsed", "cursor_api", "API usage", reset, windows, onlyPositive: true);
                }
                if (usage.TryGetProperty("onDemand", out var od) && od.ValueKind == JsonValueKind.Object)
                {
                    var enabled = od.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                    var used = GetDouble(od, "used"); var limit = GetDouble(od, "limit");
                    if (enabled && used.HasValue && limit > 0)
                        windows.Add(new LimitWindow("on_demand", "On demand", used.Value / limit.Value, reset, null, used, limit, "USD"));
                }
            }
            if (windows.Count == 0) return Error("Cursor returned no metered usage for this account.");
            return new ProviderSnapshot(Id, DisplayName, "⌾", Fidelity.Official, SnapshotStatus.Ok, windows,
                ManageUrl: "https://cursor.com/dashboard", FetchedAt: DateTimeOffset.Now);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Error("Cursor request timed out."); }
        catch (Exception ex) { return Error(ex.Message); }
    }

    private static async Task<string?> ReadValue(SqliteConnection db, string key, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1";
        cmd.Parameters.AddWithValue("$key", key);
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    private static void AddPercent(JsonElement obj, string key, string id, string label, DateTimeOffset? reset, List<LimitWindow> output, bool onlyPositive = false)
    {
        var pct = GetDouble(obj, key);
        if (!pct.HasValue || (onlyPositive && pct.Value <= 0)) return;
        output.Add(new LimitWindow(id, label, pct.Value / 100.0, reset));
    }
    private static double? GetDouble(JsonElement obj, string name) => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : null;
    private ProviderSnapshot NeedsAuth(string text) => new(Id, DisplayName, "⌾", Fidelity.Official, SnapshotStatus.NeedsAuth, [], text);
    private ProviderSnapshot Error(string text) => new(Id, DisplayName, "⌾", Fidelity.Derived, SnapshotStatus.Error, [], text);
}
