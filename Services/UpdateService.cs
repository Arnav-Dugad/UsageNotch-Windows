using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace UsageNotch.Services;

public sealed record UpdateManifest(string Version, string Url, string Sha256, long Size);

/// <summary>Trust comes from the embedded signing key, never an unsigned checksum or TLS alone.</summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "Arnav-Dugad/UsageNotch-Windows";
    public static string StageDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageNotch", "Updates");
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _stop = new();
    public string Status { get; private set; } = "No update check yet.";
    public bool Ready { get; private set; }
    public bool Busy { get; private set; }
    public double? Progress { get; private set; }
    public event Action? Changed;
    private void Report(string value) { Status = value; Changed?.Invoke(); }

    public async Task RunAsync(AppSettings settings)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                if (settings.AutomaticUpdates && !settings.LiveEdit) await CheckAsync();
                await Task.Delay(TimeSpan.FromHours(6), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task CheckAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            Busy = true; Progress = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var ct = timeout.Token;
            Report("Checking signed releases…");
            var root = $"https://github.com/{Repository}/releases/latest/download/";
            var manifestBytes = await DownloadAsync(new Uri(root + "update.json"), 16384, ct);
            var signature = System.Text.Encoding.UTF8.GetString(await DownloadAsync(new Uri(root + "update.sig"), 1024, ct));
            var manifest = VerifyManifest(manifestBytes, signature);
            if (Version.Parse(manifest.Version) <= CurrentVersion) { Report("You’re up to date. Last checked " + DateTime.Now.ToString("HH:mm")); return; }
            Report($"Downloading verified release {manifest.Version}…");
            Directory.CreateDirectory(StageDirectory);
            var download = Path.Combine(StageDirectory, Guid.NewGuid().ToString("N") + ".download");
            try
            {
                await DownloadFileAsync(new Uri(manifest.Url), download, manifest.Size, ct);
                Progress = null; Report("Download complete · verifying publisher signature, size and SHA-256…");
                await Task.Run(() => VerifyFile(download, manifest), ct);
                File.Move(download, Path.Combine(StageDirectory, "UsageNotch.exe"), true);
                File.WriteAllBytes(Path.Combine(StageDirectory, "update.json"), manifestBytes);
                File.WriteAllText(Path.Combine(StageDirectory, "update.sig"), signature);
                Ready = true;
                Report($"Version {manifest.Version} verified and ready. Restart to update, or it will apply on your next launch.");
            }
            finally { if (File.Exists(download)) File.Delete(download); }
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { Report("No public update release found. Private GitHub releases require publication before automatic delivery is available."); }
        catch (OperationCanceledException) { Report("Update check timed out or was cancelled. Try again later."); }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException or ArgumentException)
        { Report("Update could not be verified or downloaded. Your current app is unchanged."); }
        finally { Busy = false; Progress = null; Changed?.Invoke(); _gate.Release(); }
    }

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static UpdateManifest VerifyManifest(byte[] bytes, string signature, byte[]? publicKey = null)
    {
        if (bytes.Length > 16384) throw new CryptographicException("Manifest too large.");
        if (publicKey is null)
        {
            using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("UsageNotch.Assets.update-public-key.blob") ?? throw new CryptographicException("Missing trust root.");
            using var memory = new MemoryStream(); stream.CopyTo(memory); publicKey = memory.ToArray();
        }
        using var key = CngKey.Import(publicKey, CngKeyBlobFormat.EccPublicBlob);
        using var verifier = new ECDsaCng(key);
        if (!verifier.VerifyData(bytes, Convert.FromBase64String(signature), HashAlgorithmName.SHA256)) throw new CryptographicException("Invalid release signature.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes) ?? throw new CryptographicException("Missing manifest.");
        if (!Version.TryParse(manifest.Version, out var version) || version.Build < 0 || version.Revision > 0 || manifest.Version != version.ToString(3)
            || manifest.Size is < 1 or > 300_000_000 || manifest.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(manifest.Sha256, "\\A[0-9a-fA-F]{64}\\z")
            || manifest.Url != $"https://github.com/{Repository}/releases/download/v{manifest.Version}/UsageNotch.exe")
            throw new CryptographicException("Invalid release metadata.");
        return manifest;
    }

    public static void VerifyFile(string path, UpdateManifest manifest)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != manifest.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(manifest.Sha256)))
            throw new CryptographicException("Release hash mismatch.");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
                !(uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com"))
                throw new CryptographicException("Untrusted update redirect.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("UsageNotch/2.0");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { uri = new Uri(uri, location); response.Dispose(); continue; }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Too many redirects.");
    }
    private async Task<byte[]> DownloadAsync(Uri uri, int maximum, CancellationToken ct)
    {
        using var response = await GetAsync(uri, ct);
        using var source = await response.Content.ReadAsStreamAsync(ct);
        using var target = new MemoryStream();
        await CopyBounded(source, target, maximum, ct);
        return target.ToArray();
    }
    private async Task DownloadFileAsync(Uri uri, string path, long size, CancellationToken ct)
    {
        using var response = await GetAsync(uri, ct);
        using var source = await response.Content.ReadAsStreamAsync(ct);
        using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var watch = Stopwatch.StartNew(); long lastReport = -1;
        await CopyBounded(source, target, size, ct, total =>
        {
            if (watch.ElapsedMilliseconds - lastReport < 120 && total != size) return;
            lastReport = watch.ElapsedMilliseconds;
            Progress = Math.Clamp(total * 100d / size, 0, 100);
            var speed = total / Math.Max(.1, watch.Elapsed.TotalSeconds) / 1048576;
            Report($"Downloading update · {Progress:0}% · {total / 1048576d:0.0} / {size / 1048576d:0.0} MB · {speed:0.0} MB/s");
        });
    }
    private static async Task CopyBounded(Stream source, Stream target, long maximum, CancellationToken ct, Action<long>? progress = null)
    {
        var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, ct); if (count == 0) return;
            total += count; if (total > maximum) throw new CryptographicException("Download exceeds signed size.");
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
            progress?.Invoke(total);
        }
    }

    public bool TryInstallOnRestart()
    {
        try
        {
            var manifestPath = Path.Combine(StageDirectory, "update.json");
            if (!File.Exists(manifestPath)) return false;
            var manifest = VerifyManifest(File.ReadAllBytes(manifestPath), File.ReadAllText(Path.Combine(StageDirectory, "update.sig")));
            if (Version.Parse(manifest.Version) <= CurrentVersion) return false;
            VerifyFile(Path.Combine(StageDirectory, "UsageNotch.exe"), manifest);
            var target = Environment.ProcessPath!;
            if (!Path.GetFileName(target).Equals("UsageNotch.exe", StringComparison.OrdinalIgnoreCase)) return false;
            // The helper is a copy of this already-trusted app. It re-verifies the candidate after the parent exits.
            var helper = Path.Combine(StageDirectory, "Installer-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(target, helper);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--apply-update", Environment.ProcessId.ToString(), target }) start.ArgumentList.Add(arg);
            _ = Process.Start(start) ?? throw new IOException("Could not start update helper.");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or FormatException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
        { Report("The staged update could not be applied. Your current app is unchanged."); return false; }
    }

    public static async Task<bool> ApplyFromArgumentsAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--apply-update") return false;
        var app = System.Windows.Application.Current;
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var progress = new UsageNotch.Controls.UpdateProgressWindow(); progress.Show();
        await Task.Run(() => ApplyFromArguments(args, message => progress.Dispatcher.InvokeAsync(() => progress.SetStage(message))));
        progress.Close(); return true;
    }
    public static bool ApplyFromArguments(string[] args, Action<string>? report = null)
    {
        if (args.Length == 0 || args[0] != "--apply-update") return false;
        try
        {
            if (args.Length != 3 || !int.TryParse(args[1], out var pid)) throw new IOException("Invalid update arguments.");
            var target = Path.GetFullPath(args[2]);
            if (!Path.GetFileName(target).Equals("UsageNotch.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) throw new IOException("Invalid update target.");
            report?.Invoke("Waiting for UsageNotch to close…");
            try { using var parent = Process.GetProcessById(pid); if (!parent.WaitForExit(30000)) throw new IOException("App did not close in time."); } catch (ArgumentException) { }
            report?.Invoke("Verifying the publisher's signed release…");
            var manifest = VerifyManifest(File.ReadAllBytes(Path.Combine(StageDirectory, "update.json")), File.ReadAllText(Path.Combine(StageDirectory, "update.sig")));
            var oldVersion = Version.Parse(FileVersionInfo.GetVersionInfo(target).FileVersion!);
            if (Version.Parse(manifest.Version) <= oldVersion) return true;
            var candidate = Path.Combine(StageDirectory, "UsageNotch.exe");
            var adjacent = target + "." + Guid.NewGuid().ToString("N") + ".new";
            var backup = target + ".previous";
            // Copy first, verify that exact copy, then atomically replace on the target volume.
            report?.Invoke("Preparing the new version · preserving your current app…");
            File.Copy(candidate, adjacent);
            try
            {
                VerifyFile(adjacent, manifest);
                report?.Invoke("Installing verified files · keeping a rollback copy…");
                File.Replace(adjacent, target, backup, true);
                report?.Invoke("Update installed · restarting UsageNotch…");
                try { _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = false }) ?? throw new IOException("Could not restart app."); }
                catch { File.Replace(backup, target, null); Process.Start(new ProcessStartInfo(target) { UseShellExecute = false }); throw; }
            }
            finally { if (File.Exists(adjacent)) File.Delete(adjacent); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show("The update could not be installed. Your previous app and settings have been kept. Open UsageNotch again to continue.", "UsageNotch update");
        }
        return true;
    }
    public void Dispose() { _stop.Cancel(); _http.Dispose(); }
}
