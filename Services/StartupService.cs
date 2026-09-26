using Microsoft.Win32;

namespace UsageNotch.Services;

/// <summary>Per-user "run at sign-in" registration. Never touches machine-wide keys.</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsageNotch";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch { return false; }
    }

    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;
            if (!enabled)
            {
                if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path)) return false;
            key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
            return true;
        }
        catch { return false; }
    }
}
