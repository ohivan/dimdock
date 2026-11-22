using System.Windows;
using System.Windows.Threading;
using DimDock.Interop;
using DimDock.Services;
using DimDock.UI;
using Microsoft.Win32;

namespace DimDock;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\DimDock_SingleInstance_Mutex";
    private const string ActivateMessageName = "DimDock_ActivateExistingInstance";

    private Mutex? _singleInstanceMutex;
    private uint _activateMessage;

    private SystemThemeService? _themeService;
    private SettingsService? _settingsService;
    private StartupService? _startupService;
    private DdcMonitorBackend? _monitorBackend;
    private BrightnessController? _brightnessController;
    private TrayIconService? _trayIconService;
    private TrayScrollHookService? _scrollHookService;
    private FlyoutWindow? _flyoutWindow;
    private TrayContextMenu? _contextMenu;
    private DispatcherTimer? _rescanDebounceTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _activateMessage = NativeMethods.RegisterWindowMessageW(ActivateMessageName);

        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
        if (!createdNew)
        {
            // Signal the running instance to toggle/show its flyout, then exit cleanly.
            if (_activateMessage != 0)
            {
                NativeMethods.PostMessageW(NativeMethods.HWND_BROADCAST, _activateMessage, IntPtr.Zero, IntPtr.Zero);
            }
            Shutdown(0);
            return;
        }

        _themeService = new SystemThemeService();
        _themeService.ApplyToApplication(this);

        _settingsService = new SettingsService();
        _startupService = new StartupService();
        _monitorBackend = new DdcMonitorBackend();
        _brightnessController = new BrightnessController(
            _monitorBackend,
            _settingsService,
            uiInvoke: action => Dispatcher.Invoke(action));

        _trayIconService = new TrayIconService(_themeService.Current.IsTaskbarDark, _activateMessage);
        _flyoutWindow = new FlyoutWindow(_brightnessController, _trayIconService, _themeService);
        _scrollHookService = new TrayScrollHookService(_trayIconService, Dispatcher);

        _contextMenu = new TrayContextMenu(
            _brightnessController,
            _startupService,
            _trayIconService,
            onRefresh: () => _ = _brightnessController.RefreshMonitorsAsync(),
            onLinkChanged: () => _flyoutWindow.SyncUiState(),
            onExit: ExitApplication);

        _rescanDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _rescanDebounceTimer.Tick += async (_, _) =>
        {
            _rescanDebounceTimer.Stop();
            if (_brightnessController is not null)
            {
                await _brightnessController.RefreshMonitorsAsync();
            }
        };

        // Wire events
        _trayIconService.LeftClicked += (_, _) => _flyoutWindow.ToggleFromTray();

        _trayIconService.RightClicked += (_, _) =>
        {
            _flyoutWindow.HideFlyout();
            _contextMenu.ShowAtCursor();
        };

        _trayIconService.ExternalActivateRequested += (_, _) => _flyoutWindow.ShowFlyout();
        _trayIconService.SystemThemeChanged += (_, _) => HandleThemeChanged();

        _scrollHookService.TrayScrolled += (_, notches) =>
        {
            _brightnessController.StepTrayBrightness(notches);
        };

        _brightnessController.BrightnessSummaryChanged += (_, summary) =>
        {
            _trayIconService.UpdateStatus(_brightnessController.FormatTrayTooltip(), summary);
        };

        SystemEvents.DisplaySettingsChanged += OnDisplayOrPowerChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        await _brightnessController.RefreshMonitorsAsync();
        _trayIconService.UpdateStatus(
            _brightnessController.FormatTrayTooltip(),
            _brightnessController.GetTraySummaryPercent());
    }

    private void HandleThemeChanged()
    {
        if (_themeService is null || _trayIconService is null || _flyoutWindow is null) return;

        var snapshot = _themeService.Refresh();
        _themeService.ApplyToApplication(this);
        _trayIconService.UpdateTheme(snapshot.IsTaskbarDark);
        _flyoutWindow.ApplyDwmBackdrop();
    }

    private void OnDisplayOrPowerChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _rescanDebounceTimer?.Stop();
            _rescanDebounceTimer?.Start();
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            OnDisplayOrPowerChanged(sender, e);
        }
    }

    private void ExitApplication()
    {
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayOrPowerChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _rescanDebounceTimer?.Stop();
        _scrollHookService?.Dispose();
        _flyoutWindow?.ForceClose();
        _trayIconService?.Dispose();
        _brightnessController?.Dispose();
        _monitorBackend?.Dispose();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex was not owned by this instance.
        }
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}
