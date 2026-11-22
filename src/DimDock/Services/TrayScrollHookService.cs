using System.Runtime.InteropServices;
using DimDock.Interop;

namespace DimDock.Services;

public sealed class TrayScrollHookService : IDisposable
{
    private const int WheelDeltaUnit = 120;

    private static readonly string[] TaskbarClassNames =
    [
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland"
    ];

    private readonly TrayIconService _trayIconService;
    private readonly Action<Action> _beginInvoke;
    private readonly NativeMethods.LowLevelMouseProc _hookProc;
    private readonly char[] _classNameBuffer = new char[128];
    private IntPtr _hookHandle;
    private int _accumulatedDelta;
    private bool _disposed;

    public TrayScrollHookService(TrayIconService trayIconService, Action<Action> beginInvoke)
    {
        _trayIconService = trayIconService;
        _beginInvoke = beginInvoke;
        _hookProc = HookCallback;

        EnsureHookInstalled();
    }

    public event EventHandler<int>? TrayScrolled;

    public void EnsureHookInstalled()
    {
        if (_disposed || _hookHandle != IntPtr.Zero) return;

        IntPtr moduleHandle = NativeMethods.GetModuleHandleW(null);
        _hookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _hookProc, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            AppLogger.Warn($"Failed to install WH_MOUSE_LL tray scroll hook (Win32 error {Marshal.GetLastWin32Error()})");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || wParam != (IntPtr)NativeMethods.WM_MOUSEWHEEL)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        try
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (IsPointOverTaskbar(data.pt) &&
                _trayIconService.TryGetIconRect(out RECT iconRect) &&
                iconRect.Contains(data.pt))
            {
                short wheelDelta = unchecked((short)((data.mouseData >> 16) & 0xFFFF));
                if (wheelDelta != 0)
                {
                    _accumulatedDelta += wheelDelta;
                    int notches = _accumulatedDelta / WheelDeltaUnit;
                    if (notches != 0)
                    {
                        _accumulatedDelta -= notches * WheelDeltaUnit;
                        _beginInvoke(() => TrayScrolled?.Invoke(this, notches));
                    }
                }

                return (IntPtr)1;
            }
        }
        catch
        {
            // Never let an exception escape a WH_MOUSE_LL hook.
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool IsPointOverTaskbar(POINT pt)
    {
        IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;

        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        int len = NativeMethods.GetClassNameW(root, _classNameBuffer, _classNameBuffer.Length);
        if (len <= 0) return false;

        return IsTaskbarClassName(_classNameBuffer.AsSpan(0, len));
    }

    internal static bool IsTaskbarClassName(ReadOnlySpan<char> className)
    {
        foreach (string candidate in TaskbarClassNames)
        {
            if (className.Equals(candidate.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
