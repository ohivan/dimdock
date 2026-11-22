using DimDock.Interop;
using DimDock.Services;
using DimDock.UI;

namespace DimDock;

/// <summary>
/// Application coordinator replacing WPF's Application class.
/// Owns the Win32 message loop, services, tray icon, lazy flyout lifecycle, and idle memory trimming.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private const string SingleInstanceMutexName = @"Local\DimDock_SingleInstance_Mutex";
    private const string ActivateMessageName = "DimDock_ActivateExistingInstance";
    private static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IdleTeardownDelay = TimeSpan.FromSeconds(45);

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private uint _activateMessage;

    private Win32Dispatcher? _dispatcher;
    private SystemThemeService? _themeService;
    private SettingsService? _settingsService;
    private StartupService? _startupService;
    private UpdateService? _updateService;
    private DdcMonitorBackend? _monitorBackend;
    private BrightnessController? _brightnessController;
    private TrayIconService? _trayIconService;
    private TrayScrollHookService? _scrollHookService;
    private FlyoutWindow? _flyoutWindow;
    private TrayContextMenu? _contextMenu;
    private Win32Timer? _rescanDebounceTimer;
    private Win32Timer? _idleTrimTimer;
    private bool _disposed;

    public void Run()
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        _activateMessage = NativeMethods.RegisterWindowMessageW(ActivateMessageName);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            if (_activateMessage != 0)
            {
                NativeMethods.PostMessageW(NativeMethods.HWND_BROADCAST, _activateMessage, IntPtr.Zero, IntPtr.Zero);
            }
            return;
        }

        _dispatcher = new Win32Dispatcher();
        _themeService = new SystemThemeService();
        _settingsService = new SettingsService();
        _startupService = new StartupService();
        _updateService = new UpdateService();
        _monitorBackend = new DdcMonitorBackend();
        _brightnessController = new BrightnessController(
            _monitorBackend,
            _settingsService,
            uiInvoke: action => _dispatcher.Invoke(action));

        _trayIconService = new TrayIconService(_dispatcher, _themeService.Current.IsTaskbarDark, _activateMessage);
        _scrollHookService = new TrayScrollHookService(_trayIconService, _dispatcher.BeginInvoke);

        _contextMenu = new TrayContextMenu(
            _dispatcher,
            _brightnessController,
            _startupService,
            _updateService,
            _trayIconService,
            _themeService,
            onRefresh: SafeRefreshMonitorsAsync,
            onLinkChanged: () => _flyoutWindow?.SyncUiState(),
            onExit: () => _dispatcher.Shutdown());

        _contextMenu.MenuOpened += (_, _) => _idleTrimTimer?.Stop();
        _contextMenu.MenuClosed += (_, _) => ScheduleIdleTrim(IdleTeardownDelay);

        _rescanDebounceTimer = _dispatcher.CreateTimer(TimeSpan.FromMilliseconds(1500), async () =>
        {
            _rescanDebounceTimer?.Stop();
            _trayIconService?.RefreshScale();
            await SafeRefreshMonitorsAsync();
        });

        _idleTrimTimer = _dispatcher.CreateTimer(IdleTeardownDelay, OnIdleTrimTimerTick);

        _trayIconService.LeftClicked += (_, _) =>
        {
            if (_flyoutWindow is { IsVisible: true })
            {
                _flyoutWindow.ToggleFromTray();
                return;
            }

            GetOrCreateFlyoutWindow().ToggleFromTray();
        };

        _trayIconService.RightClicked += (_, _) =>
        {
            _flyoutWindow?.HideFlyout();
            _contextMenu.ShowAtCursor();
        };

        _trayIconService.ExternalActivateRequested += (_, _) => GetOrCreateFlyoutWindow().ShowFlyout();
        _trayIconService.SystemThemeChanged += (_, _) => HandleThemeChanged();
        _trayIconService.DisplayOrPowerChanged += (_, _) => OnDisplayOrPowerChanged();

        _scrollHookService.TrayScrolled += (_, notches) =>
        {
            _brightnessController.StepTrayBrightness(notches);
        };

        _brightnessController.BrightnessSummaryChanged += (_, summary) =>
        {
            _trayIconService.UpdateStatus(_brightnessController.FormatTrayTooltip(), summary);
        };

        _updateService.BackgroundCheckCompleted += (_, _) =>
        {
            _dispatcher.BeginInvoke(() => ScheduleIdleTrim(StartupTrimDelay));
        };

        // Kick off initial monitor scan on startup
        _dispatcher.BeginInvoke(async () =>
        {
            await SafeRefreshMonitorsAsync();
            _trayIconService.UpdateStatus(
                _brightnessController.FormatTrayTooltip(),
                _brightnessController.GetTraySummaryPercent());
            _updateService.StartPeriodicChecks();
            ScheduleIdleTrim(StartupTrimDelay);
        });

        _dispatcher.RunMessageLoop();
    }

    private FlyoutWindow GetOrCreateFlyoutWindow()
    {
        if (_flyoutWindow is not null)
        {
            return _flyoutWindow;
        }

        var flyout = new FlyoutWindow(_dispatcher!, _brightnessController!, _trayIconService!, _themeService!);
        flyout.FlyoutShown += (_, _) => _idleTrimTimer?.Stop();
        flyout.FlyoutHidden += (_, _) => ScheduleIdleTrim(IdleTeardownDelay);
        _flyoutWindow = flyout;
        return flyout;
    }

    private void ScheduleIdleTrim(TimeSpan delay)
    {
        if (_idleTrimTimer is null) return;
        if (_flyoutWindow is { IsVisible: true } || _contextMenu is { IsOpen: true })
        {
            return;
        }

        _idleTrimTimer.Stop();
        _idleTrimTimer.Interval = delay;
        _idleTrimTimer.Start();
    }

    private void OnIdleTrimTimerTick()
    {
        _idleTrimTimer?.Stop();

        if (_contextMenu is { IsOpen: true })
        {
            return;
        }

        if (_flyoutWindow is not null)
        {
            if (_flyoutWindow.IsVisible || _flyoutWindow.IsAnimating)
            {
                return;
            }

            _flyoutWindow.ForceClose();
            _flyoutWindow = null;
        }

        MemoryOptimizer.TrimMemory();
    }

    private async Task SafeRefreshMonitorsAsync()
    {
        if (_brightnessController is null) return;

        try
        {
            await _brightnessController.RefreshMonitorsAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Monitor refresh failed", ex);
        }
    }

    private void HandleThemeChanged()
    {
        if (_themeService is null || _trayIconService is null) return;

        var snapshot = _themeService.Refresh();
        _trayIconService.UpdateTheme(snapshot.IsTaskbarDark);
        _flyoutWindow?.ApplyDwmBackdrop();
        _scrollHookService?.EnsureHookInstalled();
    }

    private void OnDisplayOrPowerChanged()
    {
        _trayIconService?.RefreshScale();
        _rescanDebounceTimer?.Stop();
        _rescanDebounceTimer?.Start();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLogger.Error($"Unhandled domain exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;

        _updateService?.ApplyPendingUpdateOnExit();
        _updateService?.Dispose();
        _idleTrimTimer?.Stop();
        _rescanDebounceTimer?.Stop();
        _scrollHookService?.Dispose();
        _contextMenu?.Dispose();
        _flyoutWindow?.ForceClose();
        _trayIconService?.Dispose();
        _brightnessController?.Dispose();
        _monitorBackend?.Dispose();
        _dispatcher?.Dispose();

        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
            _ownsSingleInstanceMutex = false;
        }
        _singleInstanceMutex?.Dispose();
    }
}
