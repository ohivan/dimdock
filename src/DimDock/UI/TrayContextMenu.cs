using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DimDock.Interop;
using DimDock.Models;
using DimDock.Services;

namespace DimDock.UI;

public sealed class TrayContextMenu
{
    private static readonly int[] StepOptions = [1, 2, 5, 10];

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

        menu.Placement = PlacementMode.MousePoint;

        // Bringing the hidden tray host window to foreground lets clicking outside dismiss the ContextMenu reliably.
        NativeMethods.SetForegroundWindow(_trayIconService.HostHwnd);
        menu.IsOpen = true;
    }

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

        // Tray Scroll Target submenu
        var targetMenu = new MenuItem { Header = "Tray Scroll Target" };
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
        var stepMenu = new MenuItem { Header = "Scroll Step" };
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
        var refreshItem = new MenuItem { Header = "Refresh Monitors" };
        refreshItem.Click += (_, _) => _onRefresh();
        menu.Items.Add(refreshItem);

        menu.Items.Add(new Separator());

        // Exit
        var exitItem = new MenuItem { Header = "Exit DimDock" };
        exitItem.Click += (_, _) => _onExit();
        menu.Items.Add(exitItem);

        return menu;
    }
}
