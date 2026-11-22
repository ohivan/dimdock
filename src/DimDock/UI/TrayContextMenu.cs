using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DimDock.Interop;
using DimDock.Models;
using DimDock.Services;

namespace DimDock.UI;

public sealed class TrayContextMenu
{
    private static readonly int[] StepOptions = [1, 2, 5, 10];
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly BrightnessController _controller;
    private readonly StartupService _startupService;
    private readonly TrayIconService _trayIconService;
    private readonly Action _onRefresh;
    private readonly Action _onLinkChanged;
    private readonly Action _onExit;
    private ContextMenu? _activeMenu;

    public TrayContextMenu(
        BrightnessController controller,
        StartupService startupService,
        TrayIconService trayIconService,
        Action onRefresh,
        Action onLinkChanged,
        Action onExit)
    {
        _controller = controller;
        _startupService = startupService;
        _trayIconService = trayIconService;
        _onRefresh = onRefresh;
        _onLinkChanged = onLinkChanged;
        _onExit = onExit;
    }

    public void ShowAtCursor()
    {
        if (_activeMenu is not null)
        {
            _activeMenu.IsOpen = false;
        }

        var menu = BuildMenu();
        _activeMenu = menu;

        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = PlaceAboveTrayIcon;

        // Bringing the hidden tray host window to foreground lets clicking outside dismiss the ContextMenu reliably.
        NativeMethods.SetForegroundWindow(_trayIconService.HostHwnd);
        menu.IsOpen = true;
    }

    private CustomPopupPlacement[] PlaceAboveTrayIcon(Size popupSize, Size targetSize, Point offset)
    {
        POINT anchorPx;
        RECT trayRect;
        bool hasTrayRect = _trayIconService.TryGetIconRect(out trayRect);

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
        }

        IntPtr hMonitor = NativeMethods.MonitorFromPoint(anchorPx, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfoW(hMonitor, ref mi))
        {
            return [new CustomPopupPlacement(new Point(anchorPx.X, anchorPx.Y - popupSize.Height), PopupPrimaryAxis.Horizontal)];
        }

        uint dpi = _trayIconService.HostHwnd != IntPtr.Zero
            ? NativeMethods.GetDpiForWindow(_trayIconService.HostHwnd)
            : 96;
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        double marginPx = 12.0 * scale;

        var (taskbarHwnd, taskbarRect) = FindTaskbarForMonitor(mi.rcMonitor);
        bool hasTaskbarRect = taskbarHwnd != IntPtr.Zero && taskbarRect.Width > 0 && taskbarRect.Height > 0;

        int effectiveBottomPx = mi.rcWork.Bottom;
        int effectiveTopPx = mi.rcWork.Top;
        bool isTaskbarAtTop = false;

        if (hasTaskbarRect)
        {
            int midY = (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2;
            bool isHorizontal = taskbarRect.Width > taskbarRect.Height;

            if (isHorizontal && taskbarRect.Top > midY && taskbarRect.Top < mi.rcMonitor.Bottom - 4)
            {
                effectiveBottomPx = Math.Min(effectiveBottomPx, taskbarRect.Top);
            }
            else if (isHorizontal && taskbarRect.Bottom < midY && taskbarRect.Bottom > mi.rcMonitor.Top + 4)
            {
                isTaskbarAtTop = true;
                effectiveTopPx = Math.Max(effectiveTopPx, taskbarRect.Bottom);
            }
        }

        // Center horizontally on the tray icon and clamp 12px inside the screen edges (matching native tray menus).
        double x = anchorPx.X - (popupSize.Width / 2.0);
        double minX = mi.rcWork.Left + marginPx;
        double maxX = Math.Max(minX, mi.rcWork.Right - popupSize.Width - marginPx);
        x = Math.Clamp(x, minX, maxX);

        double y = isTaskbarAtTop
            ? effectiveTopPx + marginPx
            : effectiveBottomPx - popupSize.Height - marginPx;

        // If the icon is inside the overflow dock and the menu would overlap the icon, anchor cleanly above it.
        if (hasTrayRect && hasTaskbarRect && !isTaskbarAtTop && trayRect.Bottom <= taskbarRect.Top)
        {
            y = Math.Max(mi.rcWork.Top + marginPx, trayRect.Top - popupSize.Height - marginPx);
        }

        return [new CustomPopupPlacement(new Point(Math.Round(x), Math.Round(y)), PopupPrimaryAxis.Horizontal)];
    }

    private static (IntPtr Hwnd, RECT Rect) FindTaskbarForMonitor(RECT monitorRect)
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

    private static bool RectsOverlap(RECT a, RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static TextBlock CreateGlyphIcon(string glyph) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        // Start with Windows
        var startupItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = _startupService.IsEnabled
        };
        startupItem.Click += (_, _) =>
        {
            bool target = !_startupService.IsEnabled;
            _startupService.SetEnabled(target);
        };
        menu.Items.Add(startupItem);

        // Link All Monitors
        var linkItem = new MenuItem
        {
            Header = "Link All Monitors",
            IsCheckable = true,
            IsChecked = _controller.Settings.LinkMonitors,
            IsEnabled = _controller.Monitors.Count > 1
        };
        linkItem.Click += (_, _) =>
        {
            _controller.SetLinkMonitors(!_controller.Settings.LinkMonitors);
            _onLinkChanged();
        };
        menu.Items.Add(linkItem);

        menu.Items.Add(new Separator());

        // Tray Scroll Target submenu
        var targetMenu = new MenuItem
        {
            Header = "Tray Scroll Target",
            Icon = CreateGlyphIcon("\uE7F4")
        };
        string currentTarget = _controller.Settings.TrayScrollTarget;

        var allItem = new MenuItem
        {
            Header = "All Monitors",
            IsCheckable = true,
            IsChecked = string.Equals(currentTarget, AppSettings.AllMonitorsTarget, StringComparison.OrdinalIgnoreCase)
        };
        allItem.Click += (_, _) => _controller.SetTrayScrollTarget(AppSettings.AllMonitorsTarget);
        targetMenu.Items.Add(allItem);

        if (_controller.Monitors.Count > 0)
        {
            targetMenu.Items.Add(new Separator());
            foreach (var monitor in _controller.Monitors)
            {
                string id = monitor.Id;
                var monitorItem = new MenuItem
                {
                    Header = monitor.DisplayName,
                    IsCheckable = true,
                    IsChecked = string.Equals(currentTarget, id, StringComparison.OrdinalIgnoreCase)
                };
                monitorItem.Click += (_, _) => _controller.SetTrayScrollTarget(id);
                targetMenu.Items.Add(monitorItem);
            }
        }
        menu.Items.Add(targetMenu);

        // Scroll Step submenu
        var stepMenu = new MenuItem
        {
            Header = "Scroll Step",
            Icon = CreateGlyphIcon("\uE9E9")
        };
        foreach (int step in StepOptions)
        {
            int capturedStep = step;
            var stepItem = new MenuItem
            {
                Header = $"{capturedStep}%",
                IsCheckable = true,
                IsChecked = _controller.Settings.ScrollStep == capturedStep
            };
            stepItem.Click += (_, _) => _controller.SetScrollStep(capturedStep);
            stepMenu.Items.Add(stepItem);
        }
        menu.Items.Add(stepMenu);

        menu.Items.Add(new Separator());

        // Refresh Monitors
        var refreshItem = new MenuItem
        {
            Header = "Refresh Monitors",
            Icon = CreateGlyphIcon("\uE72C")
        };
        refreshItem.Click += (_, _) => _onRefresh();
        menu.Items.Add(refreshItem);

        // Exit
        var exitItem = new MenuItem
        {
            Header = "Exit DimDock",
            Icon = CreateGlyphIcon("\uE7E8")
        };
        exitItem.Click += (_, _) => _onExit();
        menu.Items.Add(exitItem);

        return menu;
    }
}
