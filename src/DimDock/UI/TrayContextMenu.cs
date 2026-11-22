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
    private readonly UpdateService _updateService;
    private readonly TrayIconService _trayIconService;
    private readonly Action _onRefresh;
    private readonly Action _onLinkChanged;
    private readonly Action _onExit;
    private ContextMenu? _activeMenu;

    public TrayContextMenu(
        BrightnessController controller,
        StartupService startupService,
        UpdateService updateService,
        TrayIconService trayIconService,
        Action onRefresh,
        Action onLinkChanged,
        Action onExit)
    {
        _controller = controller;
        _startupService = startupService;
        _updateService = updateService;
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
        if (!TaskbarPlacementHelper.TryResolveTrayContext(_trayIconService, out var ctx))
        {
            NativeMethods.GetCursorPos(out POINT fallbackPt);
            return [new CustomPopupPlacement(new Point(fallbackPt.X, fallbackPt.Y - popupSize.Height), PopupPrimaryAxis.Horizontal)];
        }

        double scale = TaskbarPlacementHelper.GetDpiScale(_trayIconService.HostHwnd);
        double marginPx = 12.0 * scale;

        // Center horizontally on the tray icon and clamp 12px inside the screen edges (matching native tray menus).
        // Account for the 8px drop-shadow margin around the custom ContextMenu Border.
        double shadowMarginPx = 8.0 * scale;
        double visualWidth = popupSize.Width - (shadowMarginPx * 2.0);
        double visualHeight = popupSize.Height - (shadowMarginPx * 2.0);

        double visualX = ctx.AnchorPx.X - (visualWidth / 2.0);
        double minX = ctx.MonitorInfo.rcWork.Left + marginPx;
        double maxX = Math.Max(minX, ctx.MonitorInfo.rcWork.Right - visualWidth - marginPx);
        visualX = Math.Clamp(visualX, minX, maxX);

        double visualY = ctx.VerticalBounds.IsTaskbarAtTop
            ? ctx.VerticalBounds.EffectiveTopPx + marginPx
            : ctx.VerticalBounds.EffectiveBottomPx - visualHeight - marginPx;

        // If the icon is inside the overflow dock and the menu would overlap the icon, anchor cleanly above it.
        if (ctx.HasTrayRect && ctx.HasTaskbarRect && !ctx.VerticalBounds.IsTaskbarAtTop &&
            ctx.TrayRect.Bottom <= ctx.TaskbarRect.Top)
        {
            visualY = Math.Max(ctx.MonitorInfo.rcWork.Top + marginPx, ctx.TrayRect.Top - visualHeight - marginPx);
        }

        double x = visualX - shadowMarginPx;
        double y = visualY - shadowMarginPx;

        return [new CustomPopupPlacement(new Point(Math.Round(x), Math.Round(y)), PopupPrimaryAxis.Horizontal)];
    }

    private static TextBlock CreateGlyphIcon(string glyph) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static Separator CreateSeparator()
    {
        var sep = new Separator();
        if (Application.Current?.TryFindResource("DimDockSeparatorStyle") is Style sepStyle)
        {
            sep.Style = sepStyle;
        }
        return sep;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        if (Application.Current?.TryFindResource("DimDockContextMenuStyle") is Style menuStyle)
        {
            menu.Style = menuStyle;
        }
        if (Application.Current?.TryFindResource("DimDockMenuItemStyle") is Style menuItemStyle)
        {
            menu.ItemContainerStyle = menuItemStyle;
        }

        // Start with Windows
        var startupItem = new MenuItem
        {
            Header = "Start with Windows",
            Icon = CreateGlyphIcon("\uE945"),
            Tag = "switch",
            IsCheckable = true,
            IsChecked = _startupService.IsEnabled,
            StaysOpenOnClick = true
        };
        startupItem.Click += (_, _) =>
        {
            if (!_startupService.SetEnabled(startupItem.IsChecked))
            {
                startupItem.IsChecked = _startupService.IsEnabled;
            }
        };
        menu.Items.Add(startupItem);

        // Link All Monitors
        var linkItem = new MenuItem
        {
            Header = "Link All Monitors",
            Icon = CreateGlyphIcon("\uE71B"),
            Tag = "switch",
            IsCheckable = true,
            IsChecked = _controller.Settings.LinkMonitors,
            IsEnabled = _controller.Monitors.Count > 1,
            StaysOpenOnClick = true
        };
        linkItem.Click += (_, _) =>
        {
            _controller.SetLinkMonitors(linkItem.IsChecked);
            _onLinkChanged();
        };
        menu.Items.Add(linkItem);

        menu.Items.Add(CreateSeparator());

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
            Tag = "radio",
            IsChecked = string.Equals(currentTarget, AppSettings.AllMonitorsTarget, StringComparison.OrdinalIgnoreCase)
        };
        allItem.Click += (_, _) => _controller.SetTrayScrollTarget(AppSettings.AllMonitorsTarget);
        targetMenu.Items.Add(allItem);

        if (_controller.Monitors.Count > 0)
        {
            targetMenu.Items.Add(CreateSeparator());
            foreach (var monitor in _controller.Monitors)
            {
                string id = monitor.Id;
                var monitorItem = new MenuItem
                {
                    Header = monitor.DisplayName,
                    IsCheckable = true,
                    Tag = "radio",
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
                Tag = "radio",
                IsChecked = _controller.Settings.ScrollStep == capturedStep
            };
            stepItem.Click += (_, _) => _controller.SetScrollStep(capturedStep);
            stepMenu.Items.Add(stepItem);
        }
        menu.Items.Add(stepMenu);

        menu.Items.Add(CreateSeparator());

        // Refresh Monitors
        var refreshItem = new MenuItem
        {
            Header = "Refresh Monitors",
            Icon = CreateGlyphIcon("\uE72C")
        };
        refreshItem.Click += (_, _) => _onRefresh();
        menu.Items.Add(refreshItem);

        // Updates
        menu.Items.Add(BuildUpdateMenuItem());

        menu.Items.Add(CreateSeparator());

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

    private MenuItem BuildUpdateMenuItem()
    {
        var item = new MenuItem();

        switch (_updateService.State)
        {
            case UpdateState.ReadyToRestart:
                item.Header = $"Restart to Update (v{_updateService.AvailableVersion})";
                var dlIcon = CreateGlyphIcon("\uE896");
                if (Application.Current?.TryFindResource("DimAccentBrush") is Brush accent)
                {
                    dlIcon.Foreground = accent;
                }
                item.Icon = dlIcon;
                item.Click += (_, _) => _updateService.ApplyPendingUpdateAndRestart();
                break;

            case UpdateState.Checking:
                item.Header = "Checking for Updates...";
                item.Icon = CreateGlyphIcon("\uE895");
                item.IsEnabled = false;
                break;

            case UpdateState.Downloading:
                item.Header = "Downloading Update...";
                item.Icon = CreateGlyphIcon("\uE896");
                item.IsEnabled = false;
                break;

            case UpdateState.UpToDate:
                item.Header = $"Up to Date (v{_updateService.CurrentVersion})";
                item.Icon = CreateGlyphIcon("\uE73E");
                item.IsEnabled = false;
                break;

            case UpdateState.Error:
                item.Header = "Update Check Failed";
                item.Icon = CreateGlyphIcon("\uE783");
                item.Click += (_, _) => _ = _updateService.CheckAndDownloadAsync(isManual: true);
                break;

            default:
                item.Header = "Check for Updates";
                item.Icon = CreateGlyphIcon("\uE895");
                item.Click += (_, _) => _ = _updateService.CheckAndDownloadAsync(isManual: true);
                break;
        }

        return item;
    }
}
