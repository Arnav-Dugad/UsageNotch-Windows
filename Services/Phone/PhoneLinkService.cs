using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using UsageNotch.Models;

namespace UsageNotch.Services.Phone;

/// <summary>HTTPS endpoint for paired phones: one read-only route, a bearer key, a pinned certificate and tight limits.</summary>
public sealed class PhoneServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly PhoneIdentity _identity;
    private readonly Func<string> _snapshot;
    public PhoneServer(PhoneIdentity identity, Func<string> snapshot) { _identity = identity; _snapshot = snapshot; }
    public async Task Start(int port = PhoneIdentity.Port, bool loopbackOnly = false)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxRequestHeadersTotalSize = 8192; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.MaxConcurrentConnections = 16; options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            options.Listen(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port, listen => listen.UseHttps(_identity.Certificate));
        });
        _app = builder.Build();
        var limiter = new System.Threading.RateLimiting.FixedWindowRateLimiter(new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
        _app.Lifetime.ApplicationStopped.Register(limiter.Dispose);
        _app.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (context.Request.Path != "/v1/snapshot" || context.Request.Method != "GET" || context.Request.QueryString.HasValue) { context.Response.StatusCode = 404; return; }
            using var lease = limiter.AttemptAcquire(); if (!lease.IsAcquired) { context.Response.StatusCode = 429; return; }
            var header = context.Request.Headers.Authorization.ToString(); var expected = "Bearer " + _identity.Token;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(expected))) { context.Response.StatusCode = 401; return; }
            string json;
            try { json = _snapshot(); } catch { context.Response.StatusCode = 503; return; }
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(json, context.RequestAborted);
        });
        try { await _app.StartAsync(); }
        catch { await DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync() { if (_app != null) { var app = _app; _app = null; try { await app.StopAsync(); } catch { } await app.DisposeAsync(); } }
}

/// <summary>Preferences beside the DPAPI identity; shared with the standalone Link it replaces. Contains no secrets.</summary>
public sealed class LinkPreferences
{
    public bool ResumeLink { get; set; }
    public string? Address { get; set; }
    public bool TrayNoticeShown { get; set; }
    private static string File(string directory) => Path.Combine(directory, "link-settings.json");
    public static LinkPreferences Load(string directory) { try { return JsonSerializer.Deserialize<LinkPreferences>(System.IO.File.ReadAllText(File(directory))) ?? new(); } catch { return new(); } }
    public void Save(string directory) { try { Directory.CreateDirectory(directory); System.IO.File.WriteAllText(File(directory), JsonSerializer.Serialize(this)); } catch { } }
}

/// <summary>
/// The phone link, built into the app: serves readings to paired phones on the local network and, when the
/// owner turns it on, uploads end-to-end encrypted copies to their secret gist for use away from home.
/// </summary>
public sealed class PhoneLinkService : IAsyncDisposable
{
    public const string LegacyProcess = "UsageNotch.Link", LegacyRunValue = "UsageNotch Link";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _directory, _database;
    private readonly AppSettings _settings;
    private readonly UsageHistory _history;
    private readonly LinkPreferences _preferences;
    private readonly RelayPublisher _publisher;
    private readonly SemaphoreSlim _gate = new(1, 1), _uploadGate = new(1, 1);
    private volatile IReadOnlyList<ProviderSnapshot> _live = [];
    private PhoneIdentity? _identity;
    private PhoneServer? _server;
    private string? _uploadedFingerprint; private DateTimeOffset _uploadedAt; private SyncState? _rejected;
    private sealed record Served(DateTimeOffset At, string Json);
    private volatile Served? _served;
    private bool _disposed;

    public event Action? Changed;
    public bool Running => _server != null;
    public string Status { get; private set; } = "Phone sharing is off.";
    public string SyncStatus { get; private set; } = "";
    public PhoneIdentity? Identity => _identity;
    public bool SyncEnabled => _identity?.Sync != null;
    public bool ResumeOnLaunch => _preferences.ResumeLink;
    public bool TrayNoticeShown { get => _preferences.TrayNoticeShown; set { _preferences.TrayNoticeShown = value; _preferences.Save(_directory); } }
    public string? PreferredAddress
    {
        get => _preferences.Address;
        set { _preferences.Address = value; _preferences.Save(_directory); Changed?.Invoke(); }
    }

    public PhoneLinkService(AppSettings settings, UsageHistory history, string? directory = null, string? database = null, RelayPublisher? publisher = null)
    {
        _settings = settings; _history = history;
        _directory = directory ?? PhoneIdentity.DefaultDirectory;
        _database = database ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageNotch", "history.db");
        _preferences = LinkPreferences.Load(_directory);
        _publisher = publisher ?? new RelayPublisher();
        if (PhoneIdentity.Exists(_directory))
        {
            try { _identity = new PhoneIdentity(_directory); }
            catch { Status = "This PC's saved phone pairing couldn't be read. Revoke and pair again if this continues."; }
        }
    }

    public PhoneIdentity EnsureIdentity() => _identity ??= new PhoneIdentity(_directory);

    /// <summary>The address to pair with: the remembered one when still present, otherwise the best-ranked Wi-Fi/Ethernet address.</summary>
    public LinkAddress? Address(IReadOnlyList<LinkAddress>? candidates = null)
    {
        candidates ??= PhoneIdentity.Addresses();
        return candidates.FirstOrDefault(a => a.Address == _preferences.Address) ?? candidates.FirstOrDefault();
    }

    public static bool LegacyLinkRunning() { try { return Process.GetProcessesByName(LegacyProcess).Length > 0; } catch { return false; } }

    public string BuildJson(int maxPoints = PhoneSnapshot.MaxPoints) => PhoneSnapshot.Serialize(PhoneSnapshot.FromLive(_live, _settings, _history, _database, maxPoints));

    private string Serve()
    {
        // Several phones refreshing together share one build; readings change at most once a poll interval.
        if (_served is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(3)) return cached.Json;
        var json = BuildJson();
        _served = new Served(DateTimeOffset.UtcNow, json);
        return json;
    }

    public async Task<bool> StartAsync(int port = PhoneIdentity.Port, bool loopbackOnly = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (_server != null || _disposed) return _server != null;
            var server = new PhoneServer(EnsureIdentity(), Serve);
            try { await server.Start(port, loopbackOnly); }
            catch
            {
                Status = LegacyLinkRunning()
                    ? "UsageNotch Link is still running. Phone sharing is now built into UsageNotch: choose Switch to built-in to take over with the same pairing."
                    : $"Couldn't start phone sharing: another program is using port {port}. Close it and try again.";
                Changed?.Invoke();
                return false;
            }
            _server = server;
            _preferences.ResumeLink = true; _preferences.Save(_directory);
            Status = "Sharing with paired phones on this network.";
            Changed?.Invoke();
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_server != null) { await _server.DisposeAsync(); _server = null; }
            _preferences.ResumeLink = false; _preferences.Save(_directory);
            Status = "Phone sharing is off. Paired phones keep their saved readings.";
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Ends the standalone Link (same Windows user, same identity file) and removes its startup entry, then starts the built-in link.</summary>
    public async Task<bool> SwitchFromLegacyLinkAsync()
    {
        foreach (var process in Process.GetProcessesByName(LegacyProcess))
        {
            try { process.Kill(); process.WaitForExit(5000); } catch { } finally { process.Dispose(); }
        }
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true); key?.DeleteValue(LegacyRunValue, false); } catch { }
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await StartAsync()) return true;
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>Called after every refresh with the dock's latest readings.</summary>
    public void Update(IReadOnlyList<ProviderSnapshot> live)
    {
        _live = live; _served = null;
        if (SyncEnabled) _ = UploadAsync(force: false);
    }

    public async Task EnableSyncAsync(string token)
    {
        var identity = EnsureIdentity();
        string plain;
        try { plain = BuildJson(192); } catch { plain = PhoneSnapshot.Serialize(new PhoneSnapshot.Snapshot(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], "windows-app")); }
        var state = await _publisher.Create(token, s => RelayCrypto.Seal(s.KeyBytes, plain));
        identity.SetSync(state); _rejected = null; _uploadedFingerprint = null;
        SyncStatus = "Internet sync is on. Pair your phone again (scan the new code) so it gets the encryption key.";
        Changed?.Invoke();
    }

    public async Task<bool> DisableSyncAsync()
    {
        if (_identity?.Sync is not { } sync) return true;
        var deleted = true;
        try { await _publisher.Delete(sync); } catch { deleted = false; }
        _identity.SetSync(null);
        SyncStatus = deleted ? "Internet sync is off and the gist was deleted." : "Internet sync is off. The gist couldn't be deleted automatically; you can delete it at gist.github.com.";
        Changed?.Invoke();
        return deleted;
    }

    public async Task RevokeAsync()
    {
        EnsureIdentity().Revoke();
        _served = null;
        Status = Running ? "All previous pairings are revoked. Scan the new code to pair again." : "All previous pairings are revoked.";
        Changed?.Invoke();
        if (SyncEnabled) await UploadAsync(force: true); // Re-encrypt the latest reading with the new key.
    }

    /// <summary>Uploads when readings change (percentages, resets or status) and at least every 15 minutes, never more than once a minute.</summary>
    public async Task<bool> UploadAsync(bool force)
    {
        if (!await _uploadGate.WaitAsync(0)) return false;
        try
        {
            if (_identity?.Sync is not { } sync || (!force && sync == _rejected)) return false;
            var now = DateTimeOffset.UtcNow;
            if (!force && now - _uploadedAt < TimeSpan.FromMinutes(1)) return false;
            var snapshot = await Task.Run(() => PhoneSnapshot.FromLive(_live, _settings, _history, _database, 192));
            var fingerprint = sync.Key + "|" + string.Join(";", snapshot.Providers.Select(p => p.Id + ":" + p.Status + ":" + string.Join(",", p.Windows.Select(w => $"{w.Id}={w.Used:0.####}/{w.Reset}"))));
            if (!force && fingerprint == _uploadedFingerprint && now - _uploadedAt < TimeSpan.FromMinutes(15)) return false;
            await _publisher.Push(sync, RelayCrypto.Seal(sync.KeyBytes, PhoneSnapshot.Serialize(snapshot)));
            _uploadedFingerprint = fingerprint; _uploadedAt = now; _rejected = null;
            SyncStatus = $"Internet sync is on. Last encrypted upload {TimeDisplay.Clock(now, _settings.Use24HourTime)}.";
            Changed?.Invoke();
            return true;
        }
        catch (RelayRejectedException e) { _rejected = _identity?.Sync; SyncStatus = e.Message; Changed?.Invoke(); return false; }
        catch (Exception) { SyncStatus = "Internet sync is on. The last upload failed (offline?); it retries after the next reading."; Changed?.Invoke(); return false; }
        finally { _uploadGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_server != null) { await _server.DisposeAsync(); _server = null; }
        _publisher.Dispose();
        _identity?.Dispose();
    }
}
