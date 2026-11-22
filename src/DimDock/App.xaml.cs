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
    private static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IdleTeardownDelay = TimeSpan.FromSeconds(45);

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private uint _activateMessage;

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
    private DispatcherTimer? _rescanDebounceTimer;
    private DispatcherTimer? _idleTrimTimer;
    private bool _themeApplied;
    private bool _lastDwmBackdropActive = true;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        _activateMessage = NativeMethods.RegisterWindowMessageW(ActivateMessageName);

        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
        _ownsSingleInstanceMutex = createdNew;
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
        _settingsService = new SettingsService();
        _startupService = new StartupService();
        _updateService = new UpdateService();
        _monitorBackend = new DdcMonitorBackend();
        _brightnessController = new BrightnessController(
            _monitorBackend,
            _settingsService,
            uiInvoke: action => Dispatcher.Invoke(action));

        _trayIconService = new TrayIconService(_themeService.Current.IsTaskbarDark, _activateMessage);
        _scrollHookService = new TrayScrollHookService(_trayIconService, Dispatcher);

        _contextMenu = new TrayContextMenu(
            _brightnessController,
            _startupService,
            _updateService,
            _trayIconService,
            onRefresh: SafeRefreshMonitorsAsync,
            onLinkChanged: () => _flyoutWindow?.SyncUiState(),
            onExit: ExitApplication);

        _contextMenu.MenuOpened += (_, _) => _idleTrimTimer?.Stop();
        _contextMenu.MenuClosed += (_, _) => ScheduleIdleTrim(IdleTeardownDelay);

        _rescanDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _rescanDebounceTimer.Tick += async (_, _) =>
        {
            _rescanDebounceTimer.Stop();
            _trayIconService?.RefreshScale();
            await SafeRefreshMonitorsAsync();
        };

        _idleTrimTimer = new DispatcherTimer { Interval = IdleTeardownDelay };
        _idleTrimTimer.Tick += OnIdleTrimTimerTick;

        // Wire events
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
            EnsureThemeApplied();
            _flyoutWindow?.HideFlyout();
            _contextMenu.ShowAtCursor();
        };

        _trayIconService.ExternalActivateRequested += (_, _) => GetOrCreateFlyoutWindow().ShowFlyout();
        _trayIconService.SystemThemeChanged += (_, _) => HandleThemeChanged();

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
            Dispatcher.BeginInvoke(() => ScheduleIdleTrim(StartupTrimDelay));
        };

        SystemEvents.DisplaySettingsChanged += OnDisplayOrPowerChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        await SafeRefreshMonitorsAsync();
        _trayIconService.UpdateStatus(
            _brightnessController.FormatTrayTooltip(),
            _brightnessController.GetTraySummaryPercent());
        _updateService.StartPeriodicChecks();
        ScheduleIdleTrim(StartupTrimDelay);
    }

    private void EnsureThemeApplied()
    {
        if (_themeApplied || _themeService is null) return;
        _themeService.ApplyToApplication(this, useDwmBackdrop: _lastDwmBackdropActive);
        _themeApplied = true;
    }

    private FlyoutWindow GetOrCreateFlyoutWindow()
    {
        EnsureThemeApplied();

        if (_flyoutWindow is not null)
        {
            return _flyoutWindow;
        }

        var flyout = new FlyoutWindow(_brightnessController!, _trayIconService!, _themeService!);
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

    private void OnIdleTrimTimerTick(object? sender, EventArgs e)
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

            _lastDwmBackdropActive = _flyoutWindow.IsDwmBackdropActive;
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled UI dispatcher exception", e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLogger.Error($"Unhandled domain exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
    }

    private void HandleThemeChanged()
    {
        if (_themeService is null || _trayIconService is null) return;

        var snapshot = _themeService.Refresh();
        _trayIconService.UpdateTheme(snapshot.IsTaskbarDark);

        if (_themeApplied)
        {
            bool useBackdrop = _flyoutWindow?.IsDwmBackdropActive ?? _lastDwmBackdropActive;
            _themeService.ApplyToApplication(this, useDwmBackdrop: useBackdrop);
        }

        _flyoutWindow?.ApplyDwmBackdrop();
        _scrollHookService?.EnsureHookInstalled();
    }

    private void OnDisplayOrPowerChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _trayIconService?.RefreshScale();
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
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        SystemEvents.DisplaySettingsChanged -= OnDisplayOrPowerChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

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

        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Mutex was not owned by this instance.
            }
            _ownsSingleInstanceMutex = false;
        }
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}
