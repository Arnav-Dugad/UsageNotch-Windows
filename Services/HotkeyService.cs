using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace UsageNotch.Services;

/// <summary>Registers system-wide chords such as "Ctrl+Alt+U" against a hidden message hook.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    private readonly Dictionary<int, Action> _actions = [];
    private HwndSource? _source;
    private IntPtr _handle = IntPtr.Zero;
    private int _nextId = 0x5A00;

    public void Attach(Window window)
    {
        if (_source is not null) return;
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(Hook);
    }

    /// <summary>Returns false when the chord is malformed or already owned by another app.</summary>
    public bool Register(string? chord, Action action)
    {
        if (_handle == IntPtr.Zero || !TryParse(chord, out var modifiers, out var virtualKey)) return false;
        var id = _nextId++;
        if (!RegisterHotKey(_handle, id, modifiers | ModNoRepeat, virtualKey)) return false;
        _actions[id] = action;
        return true;
    }

    public void Clear()
    {
        if (_handle == IntPtr.Zero) return;
        foreach (var id in _actions.Keys) UnregisterHotKey(_handle, id);
        _actions.Clear();
    }

    private IntPtr Hook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey || !_actions.TryGetValue(wParam.ToInt32(), out var action)) return IntPtr.Zero;
        handled = true;
        action();
        return IntPtr.Zero;
    }

    public static bool TryParse(string? chord, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(chord)) return false;
        foreach (var raw in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; continue;
                case "alt": modifiers |= ModAlt; continue;
                case "shift": modifiers |= ModShift; continue;
                case "win" or "windows": modifiers |= ModWin; continue;
            }
            var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None) return false;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        return modifiers != 0 && virtualKey != 0;
    }

    public static string Describe(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        parts.Add(name);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        Clear();
        _source?.RemoveHook(Hook);
        _source = null;
    }
}
