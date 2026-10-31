using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace UsageNotch.Services;

/// <summary>
/// Keeps the dock above other windows. Windows only orders topmost windows among themselves by who was raised last, so
/// another always-on-top app (or one that briefly makes itself topmost) can cover the dock. The guard re-raises the dock,
/// without activating it, whenever the foreground window changes, and every few seconds as a fallback.
/// </summary>
public sealed class TopmostGuard : IDisposable
{
    private const uint EventSystemForeground = 0x0003, EventObjectReorder = 0x8004, WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200, SwpNoSendChanging = 0x0400;
    private static readonly IntPtr HwndTopmost = new(-1);

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private readonly Func<IntPtr> _handle;
    private readonly Func<bool> _enabled;
    private readonly WinEventProc _callback; // Kept alive for as long as the hook exists.
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private IntPtr _foregroundHook, _reorderHook;

    public TopmostGuard(Func<IntPtr> handle, Func<bool> enabled)
    {
        _handle = handle; _enabled = enabled;
        _callback = (_, _, _, _, _, _, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Raise(); };
        _timer.Tick += (_, _) => Raise();
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        _reorderHook = SetWinEventHook(EventObjectReorder, EventObjectReorder, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        _timer.Start();
    }

    /// <summary>Moves the dock back to the top of the always-on-top band. Never activates or moves it.</summary>
    public void Raise()
    {
        if (!_enabled()) return;
        var hwnd = _handle();
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
    }

    public void Dispose()
    {
        _timer.Stop(); _debounce.Stop();
        if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_reorderHook != IntPtr.Zero) { UnhookWinEvent(_reorderHook); _reorderHook = IntPtr.Zero; }
    }
}
