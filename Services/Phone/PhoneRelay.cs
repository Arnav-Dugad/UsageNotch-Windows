using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsageNotch.Services.Phone;

/// <summary>AES-256-GCM envelope shared with Android: {"v":1,"nonce":base64,"data":base64(ciphertext||tag)}.</summary>
public static class RelayCrypto
{
    public const string AssociatedData = "UsageNotch relay v1";
    public static string Seal(byte[] key, string plaintext, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext); var cipher = new byte[plain.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(AssociatedData));
        return JsonSerializer.Serialize(new { v = 1, nonce = Convert.ToBase64String(nonce), data = Convert.ToBase64String([.. cipher, .. tag]) });
    }
    public static string Open(byte[] key, string envelope)
    {
        using var doc = JsonDocument.Parse(envelope);
        if (doc.RootElement.GetProperty("v").GetInt32() != 1) throw new InvalidDataException("Unknown envelope version");
        var nonce = Convert.FromBase64String(doc.RootElement.GetProperty("nonce").GetString()!); var data = Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString()!);
        var plain = new byte[data.Length - 16];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes(AssociatedData));
        return Encoding.UTF8.GetString(plain);
    }
}

public sealed class RelayRejectedException(string message) : Exception(message);

/// <summary>Talks to the GitHub Gists API with a token the owner supplies. The gist only ever receives ciphertext.</summary>
public sealed class RelayPublisher : IDisposable
{
    public const string FileName = "usagenotch-sync.json";
    private static readonly Regex GistId = new("^[0-9a-f]{20,40}$"), Login = new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$");
    private readonly HttpClient _http;
    public RelayPublisher(HttpMessageHandler? handler = null, Uri? api = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = api ?? new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("UsageNotch/2.3");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        if (body != null) request.Content = JsonContent.Create(body);
        return request;
    }
    private static void Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw (int)response.StatusCode switch
        {
            401 => new RelayRejectedException("GitHub rejected the token. Create a new one and turn internet sync on again."),
            403 when response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0" => new HttpRequestException("GitHub rate limit reached. Sync retries automatically."),
            403 or 404 when response.RequestMessage?.Method != HttpMethod.Post => new RelayRejectedException("The sync gist is gone or the token can no longer write gists. Turn internet sync off, then on again."),
            403 or 404 => new RelayRejectedException("This token cannot create gists. Give it the Gists: Read and write permission."),
            422 => new RelayRejectedException("GitHub refused the sync upload."),
            _ => new HttpRequestException($"GitHub returned {(int)response.StatusCode}. Sync retries automatically.")
        };
    }
    /// <summary>Creates the secret gist with a first encrypted upload and returns the settings to store with DPAPI.</summary>
    public async Task<SyncState> Create(string token, Func<SyncState, string> seal, CancellationToken ct = default)
    {
        token = token.Trim();
        if (token.Length is < 20 or > 400 || token.Any(char.IsWhiteSpace)) throw new RelayRejectedException("That doesn't look like a GitHub token.");
        var state = new SyncState(token, "", "", SyncState.NewKey());
        using var response = await _http.SendAsync(Request(HttpMethod.Post, "gists", token, new
        {
            description = "UsageNotch encrypted sync (only your paired phones can read this)", @public = false,
            files = new Dictionary<string, object> { [FileName] = new { content = seal(state) } }
        }), ct);
        Ensure(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var id = doc.RootElement.GetProperty("id").GetString() ?? ""; var owner = doc.RootElement.GetProperty("owner").GetProperty("login").GetString() ?? "";
        if (!GistId.IsMatch(id) || !Login.IsMatch(owner)) throw new RelayRejectedException("GitHub returned an unexpected gist address.");
        return state with { GistId = id, Owner = owner };
    }
    public async Task Push(SyncState state, string envelope, CancellationToken ct = default)
    {
        using var response = await _http.SendAsync(Request(HttpMethod.Patch, "gists/" + state.GistId, state.GitHubToken, new { files = new Dictionary<string, object> { [FileName] = new { content = envelope } } }), ct);
        Ensure(response);
    }
    /// <summary>Best effort: removes the gist and its revision history. A missing gist counts as deleted.</summary>
    public async Task Delete(SyncState state, CancellationToken ct = default)
    {
        using var response = await _http.SendAsync(Request(HttpMethod.Delete, "gists/" + state.GistId, state.GitHubToken), ct);
        if (response.StatusCode != HttpStatusCode.NotFound) Ensure(response);
    }
    public void Dispose() => _http.Dispose();
}
