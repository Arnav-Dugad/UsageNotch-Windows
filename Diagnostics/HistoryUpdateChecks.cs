using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using UsageNotch.Models;
using UsageNotch.Services;

internal static class HistoryUpdateChecks
{
    public static void Run()
    {
        Check(GeminiOAuthClient.Parse("const OAUTH_CLIENT_ID =\n 'fixture.apps.googleusercontent.com'; var OAUTH_CLIENT_SECRET = \"fixture-value\";")?.Id == "fixture.apps.googleusercontent.com", "Installed CLI OAuth parsing");
        Check(GeminiOAuthClient.Parse("OAUTH_CLIENT_ID = getValue();") is null, "Do not execute CLI source or invent OAuth values");
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
        Check(ResetClock.Describe(now.AddSeconds(1), now).EndsWith("00:00:01"), "Second countdown");
        Check(ResetClock.Describe(now.AddSeconds(86401), now).EndsWith("1d 00:00:01"), "Day boundary");
        Check(ResetClock.Describe(now, now).Contains("confirmation"), "Expired reset must not claim success");
        Check(ResetClock.Describe(null, now).Contains("not reported"), "Unknown reset");
        var directory = Path.Combine(Path.GetTempPath(), "UsageNotch-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "history.db");
        var history = new UsageHistory(path);
        ProviderSnapshot Snapshot(double fraction, DateTimeOffset at, DateTimeOffset? reset = null) => new("test", "Test", "", Fidelity.Official,
            SnapshotStatus.Ok, [new("session", "Session", fraction, reset ?? now.AddHours(2))], FetchedAt: at, AccountName: "test identity");
        var baseline = Snapshot(.4, now);
        history.Record(Snapshot(.2, now.AddMinutes(-20)), now);
        history.Record(Snapshot(.3, now.AddMinutes(-10)), now);
        history.Record(baseline, now);
        history.Record(baseline, now); // Cached or duplicated observations must not inflate sample count.
        history.Record(Snapshot(.5, now) with { Status = SnapshotStatus.Stale }, now);
        history.Record(Snapshot(double.NaN, now.AddSeconds(1)), now);
        history.Record(Snapshot(.5, now.AddMinutes(2)), now);
        var key = history.AccountKey(baseline);
        var points = history.Read("test", key, "session", now.AddDays(-1));
        // The first observation is older than 15 minutes at ingestion and is deliberately rejected.
        Check(points.Count == 2, "Stale, invalid, future and duplicate samples excluded");
        history.Record(Snapshot(.5, now.AddMinutes(10)), now.AddMinutes(10));
        points = history.Read("test", key, "session", now.AddDays(-1));
        var forecast = UsageForecast.Calculate(points, SnapshotStatus.Ok, now.AddMinutes(10));
        Check(forecast.PercentPerHour is { } rate && Math.Abs(rate - 60) < .001, "Percentage point rate");
        Check(forecast.Summary.StartsWith("Estimated limit"), "Limit before reset prediction");
        Check(UsageForecast.Calculate(points, SnapshotStatus.Error, now.AddMinutes(10)).PercentPerHour is null, "Error must suppress forecast");
        Check(UsageForecast.Calculate(points, SnapshotStatus.Ok, now.AddHours(1)).PercentPerHour is null, "Stale forecast suppressed");
        history.Record(Snapshot(.05, now.AddMinutes(11)), now.AddMinutes(11));
        points = history.Read("test", key, "session", now.AddDays(-1));
        Check(points[^1].Period != points[^2].Period, "Usage drop separates period");
        Check(UsageForecast.Calculate(points, SnapshotStatus.Ok, now.AddMinutes(11)).PercentPerHour is null, "New reset needs learning");
        history.Record(Snapshot(.06, now.AddMinutes(11).AddSeconds(30), now.AddHours(2).AddSeconds(-1)), now.AddMinutes(11).AddSeconds(30));
        points = history.Read("test", key, "session", now.AddDays(-1));
        Check(points[^1].Period == points[^2].Period, "Reset timestamp jitter must not restart learning");
        history.Record(Snapshot(.1, now.AddMinutes(12), now.AddHours(3)), now.AddMinutes(12));
        points = history.Read("test", key, "session", now.AddDays(-1));
        Check(points[^1].Period != points[^2].Period, "Reset timestamp separates period");
        history.Record(Snapshot(.2, now.AddMinutes(50), now.AddHours(3)), now.AddMinutes(50));
        points = history.Read("test", key, "session", now.AddDays(-1));
        Check(points[^1].Period != points[^2].Period, "Offline gap separates period");
        var reopened = new UsageHistory(path);
        Check(reopened.Read("test", reopened.AccountKey(baseline), "session", now.AddDays(-1)).Count == points.Count, "History survives restart");
        Check(reopened.Read("test", reopened.AccountKey(baseline with { AccountName = "second account" }), "session", now.AddDays(-1)).Count == 0, "Account histories isolated");
        var flat = Enumerable.Range(0, 3).Select(i => new UsagePoint(now.AddMinutes(-20 + i * 10), .4, now.AddHours(2), "flat")).ToArray();
        Check(UsageForecast.Calculate(flat, SnapshotStatus.Ok, now).Summary == "No increase observed", "Flat pace must not predict exhaustion");
        Check(UsageForecast.Calculate(flat.Select(p => p with { Reset = null }).ToArray(), SnapshotStatus.Ok, now).PercentPerHour is null, "Unknown reset suppresses projection");
        history.Record(Snapshot(.5, now.AddDays(31), now.AddDays(32)), now.AddDays(31));
        Check(history.Read("test", key, "session", now.AddDays(-1)).Count == 1, "30-day retention");
        Console.WriteLine("PASS: history persistence, identity separation, deduplication, retention, reset/gap segmentation, pace and countdown boundaries.");

        using var signer = new ECDsaCng(256);
        var publicKey = signer.Key.Export(CngKeyBlobFormat.EccPublicBlob);
        var payload = Path.Combine(directory, "payload.bin");
        File.WriteAllText(payload, "signed update fixture");
        var manifest = new UpdateManifest("2.1.0", $"https://github.com/{UpdateService.Repository}/releases/download/v2.1.0/UsageNotch.exe",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(payload))), new FileInfo(payload).Length);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var sig = Convert.ToBase64String(signer.SignData(bytes, HashAlgorithmName.SHA256));
        Check(UpdateService.VerifyManifest(bytes, sig, publicKey) == manifest, "Valid release signature");
        UpdateService.VerifyFile(payload, manifest);
        Reject(() => UpdateService.VerifyManifest(bytes, sig), "Wrong publisher key rejected");
        var tampered = bytes.ToArray(); tampered[5] ^= 1;
        Reject(() => UpdateService.VerifyManifest(tampered, sig, publicKey), "Tampered metadata rejected");
        File.AppendAllText(payload, "malicious change");
        Reject(() => UpdateService.VerifyFile(payload, manifest), "Tampered executable rejected");
        var unsafeManifest = JsonSerializer.SerializeToUtf8Bytes(manifest with { Url = "https://example.com/UsageNotch.exe" });
        Reject(() => UpdateService.VerifyManifest(unsafeManifest, Convert.ToBase64String(signer.SignData(unsafeManifest, HashAlgorithmName.SHA256)), publicKey), "Signed foreign URL rejected");
        unsafeManifest = JsonSerializer.SerializeToUtf8Bytes(manifest with { Size = 400_000_000 });
        Reject(() => UpdateService.VerifyManifest(unsafeManifest, Convert.ToBase64String(signer.SignData(unsafeManifest, HashAlgorithmName.SHA256)), publicKey), "Oversized release rejected");
        Console.WriteLine("PASS: valid signatures accepted; metadata/payload tampering, wrong signer, foreign URLs and oversized updates rejected.");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(payload); File.Delete(path); Directory.Delete(directory);
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (CryptographicException) { return; }
        throw new Exception(label);
    }
}
