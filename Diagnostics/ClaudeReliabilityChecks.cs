using System.Net;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using UsageNotch.Models;
using UsageNotch.Providers;

internal static class ClaudeReliabilityChecks
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Count;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            var response = new HttpResponseMessage(Status)
            { Content = new StringContent("""{"five_hour":{"utilization":73},"seven_day":{"utilization":7}}""") };
            if (Status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }
    }

    public static void Run()
    {
        var folder = Directory.CreateTempSubdirectory("UsageNotch-check-").FullName;
        var credentials = Path.Combine(folder, "credentials.json");
        var cache = Path.Combine(folder, "usage.cache");
        void Credentials(string token, bool expired = false) => File.WriteAllText(credentials,
            JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = token,
                expiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1).ToUnixTimeMilliseconds() } }));
        ProviderSnapshot Fetch(ClaudeSubscriptionProvider p) => p.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            using var handler = new Handler();
            using var http = new HttpClient(handler);
            Credentials("fake-account-one", expired: true);
            var provider = new ClaudeSubscriptionProvider(http, credentials, cache);
            if (Fetch(provider).Status != SnapshotStatus.NeedsAuth || handler.Count != 0) throw new Exception("Expired credentials sent a request.");
            Credentials("fake-account-one");
            var live = Fetch(provider);
            if (live.Status != SnapshotStatus.Ok || handler.Count != 1) throw new Exception("Renewed credentials failed to recover.");
            if (File.ReadAllText(cache).Contains("fake-account-one") || File.ReadAllText(cache).Contains("five_hour")) throw new Exception("Usage cache is not encrypted.");
            var restart = new ClaudeSubscriptionProvider(http, credentials, cache);
            if (Fetch(restart).Windows[0].UsedFraction != .73 || handler.Count != 1) throw new Exception("Recent cached reading failed after restart.");
            var aged = live with { FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-3) };
            typeof(ClaudeSubscriptionProvider).GetField("_lastGood", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(provider, aged);
            handler.Status = HttpStatusCode.TooManyRequests;
            if (Fetch(provider).Status != SnapshotStatus.Stale) throw new Exception("429 failed to preserve usage.");
            restart = new ClaudeSubscriptionProvider(http, credentials, cache);
            var saved = Fetch(restart);
            if (handler.Count != 2 || saved.Status != SnapshotStatus.Stale || saved.FetchedAt != aged.FetchedAt || !saved.StatusText!.Contains("not live"))
                throw new Exception("Restart lost cooldown or changed cached timestamp.");
            Credentials("fake-account-one", expired: true);
            var expired = Fetch(restart);
            if (expired.Status != SnapshotStatus.NeedsAuth || expired.Windows.Count != 2 || handler.Count != 2)
                throw new Exception("Expiry during cooldown did not retain marked saved usage.");
            Credentials("fake-account-two");
            var changed = Fetch(restart);
            if (changed.Windows.Count != 0) throw new Exception("A different account inherited cached usage.");

            File.WriteAllText(cache, "corrupt-cache");
            handler.Status = HttpStatusCode.Unauthorized;
            var rejected = new ClaudeSubscriptionProvider(http, credentials, cache);
            Fetch(rejected);
            var count = handler.Count;
            if (Fetch(rejected).Status != SnapshotStatus.NeedsAuth || handler.Count != count) throw new Exception("Repeated rejected token was not suppressed.");
            Credentials("fake-account-three");
            handler.Status = HttpStatusCode.OK;
            if (Fetch(rejected).Status != SnapshotStatus.Ok || handler.Count != count + 1) throw new Exception("401 failed to recover with a new token.");
            Console.WriteLine("PASS: Claude expired/renewed sign-in, encrypted restart cache, persistent 429 cooldown, account isolation, corrupt cache and 401 recovery.");
        }
        finally
        {
            foreach (var file in new[] { credentials, cache, cache + ".tmp" }) if (File.Exists(file)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
