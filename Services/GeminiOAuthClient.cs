using System.Text.RegularExpressions;

namespace UsageNotch.Services;

/// <summary>Uses the installed CLI's public-client configuration without redistributing it.</summary>
public static class GeminiOAuthClient
{
    private static (string Id, string Secret)? _cached;
    public static (string Id, string Secret) Load()
    {
        if (_cached is { } cached) return cached;
        var prefixes = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in prefixes)
        {
            var package = Path.Combine(prefix, "node_modules", "@google", "gemini-cli");
            if (!File.Exists(Path.Combine(package, "package.json"))) continue;
            var candidates = new List<string>
            {
                Path.Combine(package, "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js"),
                Path.Combine(prefix, "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js")
            };
            var bundle = Path.Combine(package, "bundle");
            if (Directory.Exists(bundle)) candidates.AddRange(Directory.EnumerateFiles(bundle, "*.js").Take(100));
            foreach (var file in candidates)
            {
                try
                {
                    if (!File.Exists(file) || new FileInfo(file).Length > 50_000_000) continue;
                    if (Parse(File.ReadAllText(file)) is { } result) { _cached = result; return result; }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { }
            }
        }
        throw new InvalidOperationException("Gemini CLI's OAuth configuration was not found. Install or update the official Gemini CLI, then connect again in Settings.");
    }

    public static (string Id, string Secret)? Parse(string source)
    {
        string Read(string name) => Regex.Match(source, "\\b" + name + "(?:_[0-9]+)?\\s*=\\s*['\"]([^'\"\\r\\n]{1,200})['\"]",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Groups[1].Value;
        var id = Read("OAUTH_CLIENT_ID");
        var secret = Read("OAUTH_CLIENT_SECRET");
        return id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) && secret.Length > 0 ? (id, secret) : null;
    }
}
