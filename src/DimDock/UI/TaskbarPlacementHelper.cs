using System.Runtime.InteropServices;
using DimDock.Interop;
using DimDock.Services;

namespace DimDock.UI;

internal readonly record struct TaskbarVerticalBounds(
    int EffectiveTopPx,
    int EffectiveBottomPx,
    bool IsTaskbarAtTop);

internal readonly record struct TrayMonitorContext(
    POINT AnchorPx,
    RECT TrayRect,
    bool HasTrayRect,
    MONITORINFOEX MonitorInfo,
    IntPtr TaskbarHwnd,
    RECT TaskbarRect,
    bool HasTaskbarRect,
    TaskbarVerticalBounds VerticalBounds);

internal static class TaskbarPlacementHelper
{
    public static bool TryResolveTrayContext(TrayIconService trayIconService, out TrayMonitorContext context)
    {
        POINT anchorPx;
        bool hasTrayRect = trayIconService.TryGetIconRect(out RECT trayRect);

        if (hasTrayRect)
        {
            anchorPx = new POINT
            {
                X = (trayRect.Left + trayRect.Right) / 2,
                Y = trayRect.Top
            };
        }
        else
        {
            NativeMethods.GetCursorPos(out anchorPx);
            trayRect = new RECT
            {
                Left = anchorPx.X,
                Top = anchorPx.Y,
                Right = anchorPx.X,
                Bottom = anchorPx.Y
            };
        }

        IntPtr hMonitor = NativeMethods.MonitorFromPoint(anchorPx, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfoW(hMonitor, ref mi))
        {
            context = default;
            return false;
        }

        var (taskbarHwnd, taskbarRect) = FindTaskbarForMonitor(mi.rcMonitor);
        bool hasTaskbarRect = taskbarHwnd != IntPtr.Zero && taskbarRect.Width > 0 && taskbarRect.Height > 0;
        var verticalBounds = ComputeEffectiveVerticalBounds(mi.rcMonitor, mi.rcWork, taskbarRect, hasTaskbarRect);

        context = new TrayMonitorContext(
            AnchorPx: anchorPx,
            TrayRect: trayRect,
            HasTrayRect: hasTrayRect,
            MonitorInfo: mi,
            TaskbarHwnd: taskbarHwnd,
            TaskbarRect: taskbarRect,
            HasTaskbarRect: hasTaskbarRect,
            VerticalBounds: verticalBounds);
        return true;
    }

    public static double GetDpiScale(IntPtr hwnd)
    {
        uint dpi = hwnd != IntPtr.Zero ? NativeMethods.GetDpiForWindow(hwnd) : 96;
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    public static TaskbarVerticalBounds ComputeEffectiveVerticalBounds(
        RECT monitorRect,
        RECT workRect,
        RECT taskbarRect,
        bool hasTaskbarRect)
    {
        // Account for auto-hide taskbars: when auto-hide is enabled, rcWork equals rcMonitor,
        // so use the taskbar's live GetWindowRect top/bottom when it is popped up on screen.
        int effectiveBottomPx = workRect.Bottom;
        int effectiveTopPx = workRect.Top;
        bool isTaskbarAtTop = false;

        if (hasTaskbarRect)
        {
            int midY = (monitorRect.Top + monitorRect.Bottom) / 2;
            bool isHorizontal = taskbarRect.Width > taskbarRect.Height;

            if (isHorizontal && taskbarRect.Top > midY && taskbarRect.Top < monitorRect.Bottom - 4)
            {
                // Bottom taskbar (always-visible OR popped-up auto-hide)
                effectiveBottomPx = Math.Min(effectiveBottomPx, taskbarRect.Top);
            }
            else if (isHorizontal && taskbarRect.Bottom < midY && taskbarRect.Bottom > monitorRect.Top + 4)
            {
                // Top taskbar
                isTaskbarAtTop = true;
                effectiveTopPx = Math.Max(effectiveTopPx, taskbarRect.Bottom);
            }
        }

        return new TaskbarVerticalBounds(effectiveTopPx, effectiveBottomPx, isTaskbarAtTop);
    }

    public static (IntPtr Hwnd, RECT Rect) FindTaskbarForMonitor(RECT monitorRect)
    {
        IntPtr primary = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero && NativeMethods.GetWindowRect(primary, out RECT primaryRect) &&
            RectsOverlap(primaryRect, monitorRect))
        {
            return (primary, primaryRect);
        }

        IntPtr secondary = IntPtr.Zero;
        while ((secondary = NativeMethods.FindWindowExW(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (NativeMethods.GetWindowRect(secondary, out RECT secRect) && RectsOverlap(secRect, monitorRect))
            {
                return (secondary, secRect);
            }
        }

        return (primary, default);
    }

    public static bool RectsOverlap(RECT a, RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
