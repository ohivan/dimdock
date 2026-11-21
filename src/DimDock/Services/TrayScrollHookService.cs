using System.Runtime.InteropServices;
using System.Windows.Threading;
using DimDock.Interop;

namespace DimDock.Services;

public sealed class TrayScrollHookService : IDisposable
{
    private const int WheelDeltaUnit = 120;

    private static readonly HashSet<string> TaskbarClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland"
    };

    private readonly TrayIconService _trayIconService;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelMouseProc _hookProc;
    private readonly char[] _classNameBuffer = new char[128];
    private IntPtr _hookHandle;
    private int _accumulatedDelta;
    private bool _disposed;

    public TrayScrollHookService(TrayIconService trayIconService, Dispatcher dispatcher)
    {
        _trayIconService = trayIconService;
        _dispatcher = dispatcher;
        _hookProc = HookCallback;

        IntPtr moduleHandle = NativeMethods.GetModuleHandleW(null);
        _hookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _hookProc, moduleHandle, 0);
    }

    public event EventHandler<int>? TrayScrolled;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Fast path: immediately pass through all non-wheel events so cursor movement has zero overhead.
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
                        _dispatcher.BeginInvoke(() => TrayScrolled?.Invoke(this, notches));
                    }
                }

                // Consume the wheel event so the taskbar doesn't act on it.
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

        string className = new(_classNameBuffer, 0, len);
        return TaskbarClassNames.Contains(className);
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
