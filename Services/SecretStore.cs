using System.Security.Cryptography;
using System.Text;

namespace UsageNotch.Services;

public static class SecretStore
{
    public static string Protect(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(protectedBase64);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }
}
