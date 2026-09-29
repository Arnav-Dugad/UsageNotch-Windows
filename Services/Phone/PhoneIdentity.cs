using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageNotch.Services.Phone;

/// <summary>Optional internet sync: a secret gist in the owner's GitHub account holding AES-GCM ciphertext.</summary>
public sealed record SyncState(string GitHubToken, string GistId, string Owner, string Key)
{
    public string RawUrl => $"https://gist.githubusercontent.com/{Owner}/{GistId}/raw/{RelayPublisher.FileName}";
    public byte[] KeyBytes => Base64Url.Decode(Key);
    public static string NewKey() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
}

public static class Base64Url
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
}

public sealed record LinkAddress(string Address, string Adapter, int Rank)
{
    public override string ToString() => $"{Address}  ·  {Adapter}";
}

/// <summary>
/// The PC's pairing identity: a random bearer key and a self-signed certificate phones pin exactly.
/// Stored with DPAPI in the same folder the standalone UsageNotch Link used, so existing pairings keep working.
/// </summary>
public sealed class PhoneIdentity : IDisposable
{
    public const int Port = 43187;
    public const string PairPage = "https://arnav-dugad.github.io/UsageNotch-Windows/pair/";
    private readonly string _file;
    public string Token { get; private set; }
    public SyncState? Sync { get; private set; }
    public X509Certificate2 Certificate { get; }
    private sealed record Stored(string Token, string Pfx, SyncState? Sync = null);

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageNotch", "AndroidLink");
    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, "identity.dpapi"));

    private readonly string? _name;
    public PhoneIdentity(string directory, string? name = null)
    {
        _name = name;
        Directory.CreateDirectory(directory); _file = Path.Combine(directory, "identity.dpapi");
        if (File.Exists(_file))
        {
            var data = JsonSerializer.Deserialize<Stored>(ProtectedData.Unprotect(File.ReadAllBytes(_file), null, DataProtectionScope.CurrentUser))!;
            Token = data.Token; Sync = data.Sync;
            Certificate = new X509Certificate2(Convert.FromBase64String(data.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        else
        {
            Token = NewToken(); using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=UsageNotch Link", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
            // Schannel requires a persisted user key handle; ephemeral-only keys fail server handshakes on Windows.
            Certificate = new X509Certificate2(created.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            Save();
        }
    }

    private static string NewToken() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
    public string Pin => Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();
    public string Name => new((_name ?? Environment.MachineName).Where(c => !char.IsControl(c)).Take(40).ToArray());

    /// <summary>Invalidates every exported pairing: a new LAN key, and a new sync key so old pairings cannot decrypt new uploads.</summary>
    public void Revoke() { Token = NewToken(); if (Sync != null) Sync = Sync with { Key = SyncState.NewKey() }; Save(); }
    public void SetSync(SyncState? sync) { Sync = sync; Save(); }

    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Stored(Token, Convert.ToBase64String(Certificate.Export(X509ContentType.Pfx)), Sync));
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); var temporary = _file + ".tmp";
        File.WriteAllBytes(temporary, encrypted); File.Move(temporary, _file, true); CryptographicOperations.ZeroMemory(bytes);
    }

    /// <summary>The pairing file (JSON). Understood by every Android version.</summary>
    public string Pairing(string address, bool indented = true) => JsonSerializer.Serialize(new {
        schema = 1, name = Name, endpoint = $"https://{address}:{Port}", token = Token, certificateSha256 = Pin,
        relay = Sync == null ? null : new { url = Sync.RawUrl, key = Sync.Key }
    }, new JsonSerializerOptions { WriteIndented = indented, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    /// <summary>One copyable line holding the pairing file. Understood by Android 1.1 and newer.</summary>
    public string PairingCode(string address) => "UN1." + Base64Url.Encode(Encoding.UTF8.GetBytes(Pairing(address, indented: false)));

    /// <summary>
    /// Compact binary pairing for QR codes (Android 1.2+), about a third the size of the JSON so the code stays easy to scan:
    /// version 2, flags, IPv4, port, 32-byte key, 32-byte certificate hash, [32-byte sync key, owner, gist id], name.
    /// </summary>
    public string CompactCode(string address)
    {
        var ip = IPAddress.Parse(address).GetAddressBytes(); if (ip.Length != 4) throw new ArgumentException("IPv4 only", nameof(address));
        using var data = new MemoryStream();
        data.WriteByte(2); data.WriteByte((byte)(Sync != null ? 1 : 0));
        data.Write(ip); data.WriteByte(Port >> 8); data.WriteByte(Port & 0xFF);
        data.Write(Base64Url.Decode(Token)); data.Write(Convert.FromHexString(Pin));
        if (Sync != null) { data.Write(Sync.KeyBytes); Short(data, Sync.Owner); Short(data, Sync.GistId); }
        Short(data, Name);
        return "UN2." + Base64Url.Encode(data.ToArray());
    }
    private static void Short(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text); if (bytes.Length > 60) bytes = bytes[..60];
        stream.WriteByte((byte)bytes.Length); stream.Write(bytes);
    }
    /// <summary>What the QR code holds: the app scans it directly; a phone camera opens a page that hands it to the app. The code stays in the URL fragment, which browsers never send to the server.</summary>
    public string PairingUrl(string address) => PairPage + "#" + CompactCode(address);

    public void Dispose() => Certificate.Dispose();

    /// <summary>Private IPv4 addresses a phone could reach, best first: Wi-Fi/Ethernet with a router, then private mesh VPNs, then virtual adapters (WSL, Hyper-V, VMs).</summary>
    public static IReadOnlyList<LinkAddress> Addresses() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => { var ip = n.GetIPProperties(); var gateway = ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            return ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a.Address)).Select(a => new LinkAddress(a.Address.ToString(), n.Name, Rank(n.Name + " " + n.Description, n.NetworkInterfaceType, gateway, a.Address))); })
        .GroupBy(a => a.Address).Select(g => g.OrderByDescending(a => a.Rank).First()).OrderByDescending(a => a.Rank).ThenBy(a => a.Address, StringComparer.Ordinal).ToList();
    public static int Rank(string name, NetworkInterfaceType type, bool gateway, IPAddress address)
    {
        var text = name.ToLowerInvariant();
        if (new[] { "vethernet", "hyper-v", "wsl", "virtualbox", "vmware", "docker", "vbox", "loopback" }.Any(text.Contains)) return 0;
        var score = gateway ? 100 : 10;
        if (type is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet) score += 20;
        if (address.GetAddressBytes()[0] == 100 || text.Contains("tailscale") || text.Contains("zerotier")) score += 30;
        return score;
    }
    public static bool IsPrivate(IPAddress ip) { var b = ip.GetAddressBytes(); return b.Length == 4 && (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 100 && b[1] is >= 64 and <= 127)); }
}
