using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsageNotch.Models;
using UsageNotch.Services;
using UsageNotch.Services.Phone;

/// <summary>Phone link checks. Writes fixtures the Android unit tests decode, so both sides agree on every format.</summary>
internal static class PhoneLinkChecks
{
    public static int Run(string? fixtureDirectory)
    {
        var directory = Path.Combine(Path.GetTempPath(), "UsageNotch-Phone-Check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        fixtureDirectory ??= directory;
        try
        {
            RunAsync(directory, fixtureDirectory).GetAwaiter().GetResult();
            Console.WriteLine("PASS: all phone link checks");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); }

    private static async Task RunAsync(string directory, string fixtures)
    {
        // Identity, pairing file, pairing code and compact QR code.
        using var identity = new PhoneIdentity(Path.Combine(directory, "identity"), "Test PC");
        using (var reloaded = new PhoneIdentity(Path.Combine(directory, "identity"))) Check(reloaded.Token == identity.Token && reloaded.Pin == identity.Pin, "identity survives restart under DPAPI");
        var json = identity.Pairing("192.168.1.8");
        using (var doc = JsonDocument.Parse(json))
            Check(doc.RootElement.GetProperty("endpoint").GetString() == "https://192.168.1.8:43187" && !doc.RootElement.TryGetProperty("relay", out _), "pairing file without internet sync");
        var code = identity.PairingCode("192.168.1.8");
        Check(code.StartsWith("UN1.") && Encoding.UTF8.GetString(Base64Url.Decode(code[4..])) == identity.Pairing("192.168.1.8", indented: false), "pairing code is the compact pairing file");
        var compact = Base64Url.Decode(identity.CompactCode("192.168.1.8")[4..]);
        Check(compact[0] == 2 && compact[1] == 0 && compact[2..6].SequenceEqual(new byte[] { 192, 168, 1, 8 }) && (compact[6] << 8 | compact[7]) == 43187
              && Base64Url.Encode(compact[8..40]) == identity.Token && Convert.ToHexString(compact[40..72]).ToLowerInvariant() == identity.Pin, "compact QR code carries address, key and certificate hash");
        identity.SetSync(new SyncState("github_pat_test", "0123456789abcdef0123456789abcdef", "someone", SyncState.NewKey()));
        var url = identity.PairingUrl("192.168.1.8");
        Check(url.StartsWith(PhoneIdentity.PairPage + "#UN2.") && url.Length < 320 && !url.Contains("github_pat_test"), "pairing URL is short, keeps the code in the fragment and never includes the GitHub token");
        using (var qr = new QRCoder.QRCodeGenerator().CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M))
            Check(qr.ModuleMatrix.Count <= 73, $"QR code with internet sync stays easy to scan ({qr.ModuleMatrix.Count - 8} modules across)");
        Check(PairingQr.Render(url).PixelWidth > 200, "QR code renders");
        File.WriteAllText(Path.Combine(fixtures, "pairing-fixture.txt"), identity.CompactCode("192.168.1.8") + "\n" + identity.Pairing("192.168.1.8", indented: false) + "\n");
        var oldKey = identity.Sync!.Key; identity.Revoke();
        Check(identity.Sync!.Key != oldKey, "revoking rotates the internet sync key");
        Check(PhoneIdentity.Rank("Wi-Fi", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, true, IPAddress.Parse("192.168.1.8"))
              > PhoneIdentity.Rank("vEthernet (WSL (Hyper-V firewall))", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, false, IPAddress.Parse("172.25.240.1")), "Wi-Fi is offered before WSL/Hyper-V adapters");

        // Snapshot built from live readings plus recorded history.
        var history = new UsageHistory(Path.Combine(directory, "history.db"));
        var now = DateTimeOffset.UtcNow;
        ProviderSnapshot Claude(DateTimeOffset at, double session) => new("claude", "Claude", "✳", Fidelity.Official, SnapshotStatus.Ok,
            [new LimitWindow("five_hour", "Current session", session, null), new LimitWindow("seven_day", "All models", .02, now.AddHours(10)),
             new LimitWindow("extra_usage", "Usage credits", null, null, "Pay-as-you-go usage after plan limits", 3.5, null, "USD")],
            null, "https://claude.ai/settings/usage", at, "Sam Example");
        lock (history) { history.Record(Claude(now.AddMinutes(-20), 0), now.AddMinutes(-20)); history.Record(Claude(now.AddMinutes(-10), .01), now.AddMinutes(-10)); }
        var codex = new ProviderSnapshot("codex", "Codex", "✦", Fidelity.Official, SnapshotStatus.Stale,
            [new LimitWindow("codex-primary", "5-hour limit", 0, now.AddHours(4)), new LimitWindow("codex-secondary", "Weekly limit", .49, now.AddDays(5))], "Showing the last reading.", null, now.AddMinutes(-5));
        var gemini = new ProviderSnapshot("gemini", "Gemini", "◆", Fidelity.Official, SnapshotStatus.NeedsAuth, [], "Sign in to Gemini CLI.");
        var settings = new AppSettings { ShowAccountNames = true };
        var snapshot = PhoneSnapshot.FromLive([Claude(now, .01), codex, gemini], settings, history, null);
        var text = PhoneSnapshot.Serialize(snapshot);
        using (var doc = JsonDocument.Parse(text))
        {
            var root = doc.RootElement; var claude = root.GetProperty("providers")[0];
            Check(root.GetProperty("schema").GetInt32() == 1 && root.GetProperty("providers").EnumerateArray().All(p => p.GetProperty("windows").EnumerateArray().All(w => w.GetProperty("used").ValueKind == JsonValueKind.Number && w.TryGetProperty("at", out _))),
                "every window keeps the fields Android 1.1 requires");
            Check(claude.GetProperty("windows")[0].GetProperty("label").GetString() == "Current session" && claude.GetProperty("session").GetString() == "five_hour" && claude.GetProperty("weekly").GetString() == "seven_day", "desktop labels and dock roles are sent");
            Check(claude.GetProperty("extras")[0].GetProperty("id").GetString() == "extra_usage" && claude.GetProperty("extras")[0].GetProperty("usedAmount").GetDouble() == 3.5, "limits without a percentage travel separately");
            Check(claude.GetProperty("windows")[0].GetProperty("points").GetArrayLength() == 2, "24-hour history comes from the dock's own records");
            Check(claude.GetProperty("account").GetString() == "Sam Example" && claude.GetProperty("manageUrl").GetString() == "https://claude.ai/settings/usage", "account name and dashboard link included when the dock shows them");
            Check(root.GetProperty("providers")[2].GetProperty("statusText").GetString() == "Sign in to Gemini CLI." && root.GetProperty("providers")[2].GetProperty("windows").GetArrayLength() == 0, "providers needing sign-in are reported with their message");
        }
        Check(!PhoneSnapshot.Serialize(PhoneSnapshot.FromLive([Claude(now, .01)], new AppSettings { ShowAccountNames = false }, history, null)).Contains("Sam Example"), "account name stays private when the dock hides it");
        File.WriteAllText(Path.Combine(fixtures, "phone-snapshot-fixture.json"), text);

        // Protocol 2: negotiated features, shared settings, accounts and the detailed history only when asked for.
        var v2Settings = new AppSettings { ShowAccountNames = true, Accounts = [new ExtraAccount { Slug = "work", Kind = "claude", Label = "Work", Folder = directory }] };
        v2Settings.Budgets.Add(new BudgetSetting("claude", "five_hour", 60, 1080));
        v2Settings.Accents["claude"] = "#7A5CFF";
        SharedSettings.Capture(v2Settings, 1_700_000_000_000);
        var work = Claude(now, .01) with { Id = "claude:work", DisplayName = "Claude · Work" };
        var extras = new PhoneSnapshot.Extras(true, [], [new Incident("claude", "Elevated errors", "minor", "investigating", "https://status.claude.com/incidents/x", now)], true);
        var detailed = PhoneSnapshot.Serialize(PhoneSnapshot.FromLive([Claude(now, .01), work, codex], v2Settings, history, null, extras: extras));
        using (var doc = JsonDocument.Parse(detailed))
        {
            var root = doc.RootElement;
            Check(root.GetProperty("schema").GetInt32() == 1 && root.GetProperty("protocol").GetInt32() == 2 && root.GetProperty("features").EnumerateArray().Select(f => f.GetString()).Contains("settings"),
                "protocol 2 keeps schema 1 for older phones and lists its features");
            Check(root.GetProperty("shared").GetProperty("budget:claude|five_hour").GetProperty("v").GetProperty("limit").GetInt32() == 60
                && root.GetProperty("shared").GetProperty("accent:claude").GetProperty("v").GetString() == "#7A5CFF", "shared settings travel with every snapshot");
            var workJson = root.GetProperty("providers")[1];
            Check(workJson.GetProperty("id").GetString() == "claude:work" && workJson.GetProperty("kind").GetString() == "claude" && workJson.GetProperty("accountLabel").GetString() == "Work", "extra accounts carry their kind and label");
            Check(root.GetProperty("incidents")[0].GetProperty("impact").GetString() == "minor", "open incidents are included");
            var claudeHistory = root.GetProperty("providers")[0].GetProperty("history")[0];
            Check(claudeHistory.GetProperty("trend").GetArrayLength() >= 2 && claudeHistory.TryGetProperty("reliability", out _) && claudeHistory.TryGetProperty("sessions", out _), "a protocol 2 request gets the 7-day trend and insights");
        }
        Check(!PhoneSnapshot.Serialize(PhoneSnapshot.FromLive([Claude(now, .01)], v2Settings, history, null)).Contains("\"trend\":["), "older phones get no trend (same payload size as before)");
        File.WriteAllText(Path.Combine(fixtures, "phone-snapshot-v2-fixture.json"), detailed);

        // HTTPS server with a pinned certificate.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using (var server = new PhoneServer(identity, () => text))
        {
            await server.Start(port, loopbackOnly: true);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert != null && Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(identity.Pin, StringComparison.OrdinalIgnoreCase) };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };
            Check((await client.GetAsync("/v1/snapshot")).StatusCode == HttpStatusCode.Unauthorized, "unauthenticated requests rejected");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
            var response = await client.GetAsync("/v1/snapshot");
            Check(response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == text && response.Content.Headers.ContentType?.MediaType == "application/json", "pinned HTTPS serves the snapshot");
            Check((await client.GetAsync("/v1/snapshot?token=" + identity.Token)).StatusCode == HttpStatusCode.NotFound && (await client.PostAsync("/v1/snapshot", null)).StatusCode == HttpStatusCode.NotFound, "query credentials and other routes rejected");
            identity.Revoke();
            Check((await client.GetAsync("/v1/snapshot")).StatusCode == HttpStatusCode.Unauthorized, "revoked key rejected immediately");
        }

        // Protocol 2 routes: refresh on request, shared settings both ways, the detailed snapshot for phones that ask.
        var merged = new AppSettings();
        listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using (var server = new PhoneServer(identity, detailedRequest => detailedRequest ? detailed : text, () => Task.FromResult("done"), body =>
        {
            var shared = (System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject)?["shared"] as System.Text.Json.Nodes.JsonObject;
            if (shared is null) return null;
            SharedSettings.Merge(merged, shared, DateTimeOffset.UtcNow); SharedSettings.Apply(merged);
            return new System.Text.Json.Nodes.JsonObject { ["shared"] = SharedSettings.ToJson(merged) }.ToJsonString();
        }))
        {
            await server.Start(port, loopbackOnly: true);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert != null && Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant() == identity.Pin };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
            var refreshed = await client.PostAsync("/v1/refresh", null);
            Check(refreshed.IsSuccessStatusCode && refreshed.Headers.GetValues("X-UsageNotch-Refresh").Single() == "done", "POST /v1/refresh refreshes and reports done");
            var asked = new HttpRequestMessage(HttpMethod.Get, "/v1/snapshot"); asked.Headers.Add("X-UsageNotch-Protocol", "2");
            Check(await (await client.SendAsync(asked)).Content.ReadAsStringAsync() == detailed, "phones that send X-UsageNotch-Protocol: 2 get the detailed snapshot");
            var body = new StringContent("{\"shared\":{\"budget:codex|codex-primary\":{\"v\":{\"limit\":40,\"by\":null},\"at\":1700000000000},\"name:codex\":{\"v\":\"Work\",\"at\":1700000000000},\"evil\":{\"v\":1,\"at\":1}}}", Encoding.UTF8, "application/json");
            var put = await client.PutAsync("/v1/settings", body);
            using (var reply = JsonDocument.Parse(await put.Content.ReadAsStringAsync()))
                Check(put.IsSuccessStatusCode && reply.RootElement.GetProperty("shared").TryGetProperty("budget:codex|codex-primary", out _) && !reply.RootElement.GetProperty("shared").TryGetProperty("evil", out _)
                      && merged.Budgets.Single().Limit == 40 && merged.CodexDockLabel == "Work", "PUT /v1/settings merges valid entries, applies them and drops unknown keys");
            Check((await client.PutAsync("/v1/settings", new StringContent("not json", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.BadRequest
                  && (await client.PutAsync("/v1/settings", new StringContent(new string('x', PhoneServer.MaxSettingsBytes + 10), Encoding.UTF8, "application/json"))).StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
                  "invalid or oversized settings are refused");
        }

        // Service: start/stop, port conflicts, uploads.
        var fake = new FakeGitHub();
        await using (var phone = new PhoneLinkService(settings, history, Path.Combine(directory, "service"), Path.Combine(directory, "none.db"), new RelayPublisher(fake, new Uri("https://github.test/"))))
        {
            Check(await phone.StartAsync(port, loopbackOnly: true) && phone.Running && phone.ResumeOnLaunch, "phone sharing starts and remembers it was on");
            await using (var blocker = new PhoneLinkService(settings, history, Path.Combine(directory, "blocker"), Path.Combine(directory, "none.db")))
                Check(!await blocker.StartAsync(port, loopbackOnly: true) && (blocker.Status.Contains("port") || blocker.Status.Contains("UsageNotch Link")), "a busy port is explained instead of crashing");
            phone.Update([Claude(now, .01), codex]);
            await phone.EnableSyncAsync("ghp_" + new string('x', 36));
            Check(fake.Requests[^1].Method == "POST" && !fake.Requests[^1].Body.Contains("Claude") && phone.SyncEnabled, "internet sync creates a secret gist with ciphertext only");
            Check(await phone.UploadAsync(force: true) && fake.Requests[^1].Method == "PATCH", "readings upload when forced");
            Check(!await phone.UploadAsync(force: false), "uploads are limited to one a minute");
            var envelope = JsonDocument.Parse(fake.Requests[^1].Body).RootElement.GetProperty("files").GetProperty(RelayPublisher.FileName).GetProperty("content").GetString()!;
            using (var opened = JsonDocument.Parse(RelayCrypto.Open(phone.Identity!.Sync!.KeyBytes, envelope)))
                Check(opened.RootElement.GetProperty("providers").GetArrayLength() == 2, "uploaded snapshot decrypts to the current readings");
            fake.Status = HttpStatusCode.NotFound;
            Check(await phone.DisableSyncAsync() && !phone.SyncEnabled && fake.Requests[^1].Method == "DELETE", "turning sync off deletes the gist");
            await phone.StopAsync();
            Check(!phone.Running && !phone.ResumeOnLaunch, "phone sharing stops and stays off next launch");
        }

        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Check(RelayCrypto.Open(key, RelayCrypto.Seal(key, text)) == text, "relay envelope round-trips");

        // 30-day summaries: consumption counts only increases between continuous readings.
        var noon = new DateTimeOffset(now.ToLocalTime().Date.AddHours(12), now.ToLocalTime().Offset);
        var series = new List<UsagePoint>
        {
            new(noon.AddDays(-2), .10, null, "a"), new(noon.AddDays(-2).AddMinutes(10), .30, null, "a"),     // +0.20 two days ago
            new(noon.AddDays(-1), .40, null, "b"), new(noon.AddDays(-1).AddMinutes(10), .50, null, "b"),     // +0.10 after the overnight gap, +0.10 yesterday
            new(noon.AddDays(-1).AddHours(3), .90, null, "b"),                                                // 3-hour gap: +0.40 still counted, timing unknown
            new(noon, .05, null, "c"), new(noon.AddMinutes(5), .15, null, "c"), new(noon.AddMinutes(10), .12, null, "c"), // renewed: +0.05, then +0.10; a small drop adds nothing
        };
        var summary = PhoneSnapshot.Aggregate("five_hour", "Current session", series, noon.AddMinutes(20));
        double? DayUsed(int ago) => summary.Days[^(ago + 1)].Used;
        Check(summary.Days.Count == 30 && Math.Abs(DayUsed(2)!.Value - .2) < 1e-6 && Math.Abs(DayUsed(1)!.Value - .6) < 1e-6 && Math.Abs(DayUsed(0)!.Value - .15) < 1e-6 && DayUsed(3) == null,
            "daily consumption counts rises across gaps, ignores small drops, and leaves unobserved days empty");
        var cell = (int)noon.ToLocalTime().DayOfWeek * 24 + noon.ToLocalTime().Hour;
        Check(summary.Heat.Count == 168 && Math.Abs(summary.Heat[cell] - .1) < 1e-6 && summary.Observed[cell] == 1 && summary.Streak == 3, "heatmap cells, observed hours and the 3-day streak are correct");
        // The 90-day calendar reaches further back; the 30-day days and heatmap don't.
        var older = series.Prepend(new UsagePoint(noon.AddDays(-60).AddMinutes(10), .45, null, "o")).Prepend(new UsagePoint(noon.AddDays(-60), .05, null, "o")).ToList();
        var longer = PhoneSnapshot.Aggregate("five_hour", "Current session", older, noon.AddMinutes(20));
        var oldCell = (int)noon.AddDays(-60).ToLocalTime().DayOfWeek * 24 + noon.ToLocalTime().Hour;
        Check(longer.Calendar is { Count: 90 } calendar && Math.Abs(calendar[^61].Used!.Value - .4) < 1e-6 && calendar[^1].Date == summary.Days[^1].Date && calendar[^62].Used == null
            && Math.Abs(longer.Days[^3].Used!.Value - .3) < 1e-6 && longer.Heat.SequenceEqual(summary.Heat) && longer.Observed.SequenceEqual(summary.Observed) && (oldCell != cell || longer.Heat[cell] == summary.Heat[cell]),
            "the 90-day calendar includes older days; a renewal after a long gap counts its new usage; the heatmap is unchanged");

        // Forecast from the dock's own estimator, with the limit time when it comes before the reset.
        var climbing = Enumerable.Range(0, 7).Select(i => new UsagePoint(now.AddMinutes(-30 + i * 5), .50 + i * .05, now.AddHours(5), "p")).ToList();
        var forecast = PhoneSnapshot.ForecastFor(climbing, SnapshotStatus.Ok, now, settings)!;
        Check(forecast.LimitAt is { } limitAt && limitAt > now.ToUnixTimeMilliseconds() && limitAt < now.AddHours(5).ToUnixTimeMilliseconds() && forecast.Rate > 50, "forecast reports when a fast pace reaches the limit before the reset");

        // Paired phones learn internet sync settings over the local link; relay uploads never carry the key.
        var gh = new FakeGitHub();
        await using (var linked = new PhoneLinkService(settings, history, Path.Combine(directory, "linked"), Path.Combine(directory, "none.db"), new RelayPublisher(gh, new Uri("https://github.test/"))))
        {
            linked.EnsureIdentity(); linked.Update([Claude(now, .01)]);
            using (var off = JsonDocument.Parse(linked.BuildJson())) Check(off.RootElement.GetProperty("relayState").GetString() == "off" && !off.RootElement.TryGetProperty("relay", out _), "local snapshot says internet sync is off");
            await linked.EnableSyncAsync("ghp_" + new string('y', 36));
            using (var on = JsonDocument.Parse(linked.BuildJson()))
                Check(on.RootElement.GetProperty("relayState").GetString() == "on" && on.RootElement.GetProperty("relay").GetProperty("key").GetString() == linked.Identity!.Sync!.Key, "local snapshot hands the sync address and key to paired phones");
            await linked.UploadAsync(force: true);
            var uploaded = JsonDocument.Parse(gh.Requests[^1].Body).RootElement.GetProperty("files").GetProperty(RelayPublisher.FileName).GetProperty("content").GetString()!;
            var plain = RelayCrypto.Open(linked.Identity!.Sync!.KeyBytes, uploaded);
            Check(!plain.Contains(linked.Identity.Sync.Key) && !plain.Contains("relayState") && plain.Contains("\"history\""), "uploads include summaries but never the sync key");
        }
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public sealed record Seen(string Method, string Path, string Body);
        public List<Seen> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var status = request.Method == HttpMethod.Post ? HttpStatusCode.Created : Status;
            return new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent("{\"id\":\"0123456789abcdef0123456789abcdef\",\"owner\":{\"login\":\"someone\"}}") };
        }
    }
}
