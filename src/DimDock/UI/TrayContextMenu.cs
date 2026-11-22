using System.Diagnostics;
using System.Runtime.InteropServices;
using DimDock.Interop;
using DimDock.Models;
using DimDock.Services;

namespace DimDock.UI;

/// <summary>
/// Native Win32 + Direct2D Windows 11 Fluent tray context menu with animated pill switches,
/// radio submenus, live FluentProgressRing spinners, and VSync slide-up animation.
/// </summary>
public sealed class TrayContextMenu : IDisposable
{
    internal const double OpenSlideDistanceDip = 16.0;
    internal const double OpenSlideDurationMs = 200.0;
    private const int MinRefreshSpinMs = 850;
    private const int RefreshDoneDisplayMs = 1700;

    private static readonly int[] StepOptions = [1, 2, 5, 10];
    private static bool s_classRegistered;
    private static readonly string MenuClassName = "DimDock_TrayMenu";
    private static readonly NativeMethods.WndProcDelegate StaticMenuWndProc = DispatchMenuWndProc;
    private static readonly Dictionary<IntPtr, MenuPopup> ActivePopups = [];
    private static MenuPopup? s_creatingPopup;

    private static IntPtr DispatchMenuWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (ActivePopups.TryGetValue(hwnd, out var popup))
        {
            return popup.WndProc(hwnd, msg, wParam, lParam);
        }

        if (s_creatingPopup is { } creating)
        {
            return creating.WndProc(hwnd, msg, wParam, lParam);
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private readonly Win32Dispatcher _dispatcher;
    private readonly BrightnessController _controller;
    private readonly StartupService _startupService;
    private readonly UpdateService _updateService;
    private readonly TrayIconService _trayIconService;
    private readonly SystemThemeService _themeService;
    private readonly Func<Task> _onRefresh;
    private readonly Action _onLinkChanged;
    private readonly Action _onExit;
    private readonly NativeVSyncSlideAnimator _slideAnimator;

    private MenuPopup? _rootPopup;
    private MenuPopup? _subPopup;
    private MenuItemModel? _openSubmenuOwner;
    private MenuItemModel? _linkItem;
    private MenuItemModel? _targetItem;
    private MenuItemModel? _refreshItem;
    private MenuItemModel? _updateItem;

    private CancellationTokenSource? _refreshResetCts;
    private bool _isRefreshing;
    private bool _isManualUpdateChecking;
    private bool _isListeningLiveUpdates;
    private bool _disposed;

    public bool IsOpen => _rootPopup is { IsOpen: true };

    public event EventHandler? MenuOpened;
    public event EventHandler? MenuClosed;

    internal TrayContextMenu(
        Win32Dispatcher dispatcher,
        BrightnessController controller,
        StartupService startupService,
        UpdateService updateService,
        TrayIconService trayIconService,
        SystemThemeService themeService,
        Func<Task> onRefresh,
        Action onLinkChanged,
        Action onExit)
    {
        _dispatcher = dispatcher;
        _controller = controller;
        _startupService = startupService;
        _updateService = updateService;
        _trayIconService = trayIconService;
        _themeService = themeService;
        _onRefresh = onRefresh;
        _onLinkChanged = onLinkChanged;
        _onExit = onExit;
        _slideAnimator = new NativeVSyncSlideAnimator(_dispatcher.BeginInvoke);
    }

    public void ShowAtCursor()
    {
        if (_disposed) return;

        CloseMenu(raiseClosedEvent: false);

        var palette = _themeService.BuildPalette(useDwmBackdrop: true);
        var items = BuildRootItems();

        _rootPopup = new MenuPopup(
            items,
            palette,
            minWidthDip: 216f,
            onItemClicked: HandleItemClick,
            onHoverChanged: HandleRootHoverChanged,
            onDeactivated: HandlePopupDeactivated,
            onEscape: () => CloseMenu(raiseClosedEvent: true));

        var placement = ComputeRootPlacement(_rootPopup.WidthDip, _rootPopup.HeightDip);
        int startYPx = ComputeSlideStartY(placement.TargetYPx, placement.Scale, placement.IsTaskbarAtTop);

        SubscribeLiveMenuUpdates();
        MenuOpened?.Invoke(this, EventArgs.Empty);

        _rootPopup.Show(placement.TargetXPx, startYPx, placement.Scale, activate: true, insertAfterHwnd: placement.TaskbarHwnd);

        _slideAnimator.Start(
            _rootPopup.Hwnd,
            NativeVSyncSlideAnimator.EvaluateMenuOpenEase,
            placement.TargetXPx,
            startYPx,
            placement.TargetYPx,
            OpenSlideDurationMs);
    }

    internal static int ComputeSlideStartY(int targetYPx, double dpiScale, bool isTaskbarAtTop)
    {
        double safeScale = dpiScale > 0 ? dpiScale : 1.0;
        int offsetPx = Math.Max(1, (int)Math.Round(OpenSlideDistanceDip * safeScale));
        return isTaskbarAtTop
            ? targetYPx - offsetPx
            : targetYPx + offsetPx;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseMenu(raiseClosedEvent: false);
        _slideAnimator.Dispose();
    }

    private void CloseMenu(bool raiseClosedEvent)
    {
        bool wasOpen = _rootPopup is not null;
        CancelRefreshReset();
        UnsubscribeLiveMenuUpdates();
        _slideAnimator.Stop();

        CloseSubmenu();

        if (_rootPopup is { } root)
        {
            _rootPopup = null;
            root.Dispose();
        }

        _linkItem = null;
        _targetItem = null;
        _refreshItem = null;
        _updateItem = null;

        if (wasOpen && raiseClosedEvent)
        {
            MenuClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CloseSubmenu()
    {
        _openSubmenuOwner = null;
        if (_subPopup is { } sub)
        {
            _subPopup = null;
            sub.Dispose();
        }
    }

    private void HandlePopupDeactivated(IntPtr activatedHwnd)
    {
        if (_disposed) return;

        // Keep open if focus moved between root menu and its submenu.
        if ((_rootPopup is not null && activatedHwnd == _rootPopup.Hwnd) ||
            (_subPopup is not null && activatedHwnd == _subPopup.Hwnd))
        {
            return;
        }

        CloseMenu(raiseClosedEvent: true);
    }

    private void HandleRootHoverChanged(MenuItemModel? hovered, float itemTopDip)
    {
        if (_rootPopup is null || _disposed) return;

        if (hovered is { SubItems.Count: > 0 })
        {
            if (ReferenceEquals(_openSubmenuOwner, hovered) && _subPopup is { IsOpen: true })
            {
                return;
            }

            OpenSubmenu(hovered, itemTopDip);
        }
        else if (_subPopup is not null)
        {
            // Keep submenu open if cursor is currently over the submenu window.
            if (NativeMethods.GetCursorPos(out POINT pt) &&
                NativeMethods.GetWindowRect(_subPopup.Hwnd, out RECT subRect) &&
                subRect.Contains(pt))
            {
                return;
            }

            CloseSubmenu();
        }
    }

    private void OpenSubmenu(MenuItemModel owner, float itemTopDip)
    {
        CloseSubmenu();
        if (_rootPopup is null || owner.SubItems is null || owner.SubItems.Count == 0) return;

        _openSubmenuOwner = owner;
        var palette = _themeService.BuildPalette(useDwmBackdrop: true);
        _subPopup = new MenuPopup(
            owner.SubItems,
            palette,
            minWidthDip: 148f,
            onItemClicked: HandleItemClick,
            onHoverChanged: null,
            onDeactivated: HandlePopupDeactivated,
            onEscape: CloseSubmenu);

        double scale = _rootPopup.Scale;
        NativeMethods.GetWindowRect(_rootPopup.Hwnd, out RECT rootRect);
        int subWidthPx = (int)Math.Round(_subPopup.WidthDip * scale);
        int subHeightPx = (int)Math.Round(_subPopup.HeightDip * scale);

        int x = rootRect.Right - (int)Math.Round(2.0 * scale);
        int y = rootRect.Top + (int)Math.Round((itemTopDip - 4.0) * scale);

        // Flip to left side if overflowing monitor right edge
        IntPtr hMon = NativeMethods.MonitorFromWindow(_rootPopup.Hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (NativeMethods.GetMonitorInfoW(hMon, ref mi))
        {
            if (x + subWidthPx > mi.rcWork.Right - 4)
            {
                x = rootRect.Left - subWidthPx + (int)Math.Round(2.0 * scale);
            }
            if (y + subHeightPx > mi.rcWork.Bottom - 4)
            {
                y = Math.Max(mi.rcWork.Top + 4, mi.rcWork.Bottom - subHeightPx - 4);
            }
        }

        _subPopup.Show(x, y, scale, activate: false);
    }

    private void HandleItemClick(MenuItemModel item)
    {
        if (!item.IsEnabled) return;

        item.OnClick?.Invoke(item);

        if (!item.StaysOpenOnClick)
        {
            CloseMenu(raiseClosedEvent: true);
        }
        else
        {
            _rootPopup?.Invalidate();
            _subPopup?.Invalidate();
        }
    }

    private List<MenuItemModel> BuildRootItems()
    {
        var items = new List<MenuItemModel>();

        // Start with Windows
        items.Add(new MenuItemModel
        {
            Text = "Start with Windows",
            IconGlyph = "\uE945",
            Kind = MenuItemKind.Switch,
            IsChecked = _startupService.IsEnabled,
            SwitchProgress = _startupService.IsEnabled ? 1f : 0f,
            StaysOpenOnClick = true,
            OnClick = self =>
            {
                bool next = !self.IsChecked;
                if (_startupService.SetEnabled(next))
                {
                    self.IsChecked = next;
                }
                else
                {
                    self.IsChecked = _startupService.IsEnabled;
                }
                _rootPopup?.StartAnimationLoop();
            }
        });

        // Link All Monitors
        _linkItem = new MenuItemModel
        {
            Text = "Link All Monitors",
            IconGlyph = "\uE71B",
            Kind = MenuItemKind.Switch,
            IsChecked = _controller.Settings.LinkMonitors,
            SwitchProgress = _controller.Settings.LinkMonitors ? 1f : 0f,
            IsEnabled = _controller.Monitors.Count > 1,
            StaysOpenOnClick = true,
            OnClick = self =>
            {
                bool next = !self.IsChecked;
                self.IsChecked = next;
                _controller.SetLinkMonitors(next);
                _onLinkChanged();
                _rootPopup?.StartAnimationLoop();
            }
        };
        items.Add(_linkItem);

        items.Add(MenuItemModel.Separator());

        // Tray Scroll Target submenu
        _targetItem = new MenuItemModel
        {
            Text = "Tray Scroll Target",
            IconGlyph = "\uE7F4",
            Kind = MenuItemKind.Submenu,
            StaysOpenOnClick = true,
            SubItems = BuildScrollTargetSubItems()
        };
        items.Add(_targetItem);

        // Scroll Step submenu
        var stepSubItems = new List<MenuItemModel>(StepOptions.Length);
        foreach (int step in StepOptions)
        {
            int capturedStep = step;
            stepSubItems.Add(new MenuItemModel
            {
                Text = $"{capturedStep}%",
                Kind = MenuItemKind.Radio,
                IsChecked = _controller.Settings.ScrollStep == capturedStep,
                StaysOpenOnClick = true,
                OnClick = self =>
                {
                    _controller.SetScrollStep(capturedStep);
                    SelectExclusiveRadio(stepSubItems, self);
                }
            });
        }
        items.Add(new MenuItemModel
        {
            Text = "Scroll Step",
            IconGlyph = "\uE9E9",
            Kind = MenuItemKind.Submenu,
            StaysOpenOnClick = true,
            SubItems = stepSubItems
        });

        items.Add(MenuItemModel.Separator());

        // Refresh Monitors
        _refreshItem = new MenuItemModel
        {
            StaysOpenOnClick = true,
            OnClick = _ => OnRefreshClicked()
        };
        if (_isRefreshing)
        {
            ApplyRefreshSpinningState(_refreshItem);
        }
        else
        {
            ApplyRefreshIdleState(_refreshItem);
        }
        items.Add(_refreshItem);

        // Updates
        _updateItem = new MenuItemModel
        {
            OnClick = _ => OnUpdateClicked()
        };
        ConfigureUpdateMenuItem(_updateItem);
        items.Add(_updateItem);

        items.Add(MenuItemModel.Separator());

        // Exit
        items.Add(new MenuItemModel
        {
            Text = "Exit DimDock",
            IconGlyph = "\uE7E8",
            StaysOpenOnClick = false,
            OnClick = _ => _onExit()
        });

        return items;
    }

    private List<MenuItemModel> BuildScrollTargetSubItems()
    {
        var list = new List<MenuItemModel>();
        string currentTarget = _controller.Settings.TrayScrollTarget;
        var radioItems = new List<MenuItemModel>(_controller.Monitors.Count + 1);

        var allItem = new MenuItemModel
        {
            Text = "All Monitors",
            Kind = MenuItemKind.Radio,
            IsChecked = string.Equals(currentTarget, AppSettings.AllMonitorsTarget, StringComparison.OrdinalIgnoreCase),
            StaysOpenOnClick = true,
            OnClick = self =>
            {
                _controller.SetTrayScrollTarget(AppSettings.AllMonitorsTarget);
                SelectExclusiveRadio(radioItems, self);
            }
        };
        radioItems.Add(allItem);
        list.Add(allItem);

        if (_controller.Monitors.Count > 0)
        {
            list.Add(MenuItemModel.Separator());
            foreach (var monitor in _controller.Monitors)
            {
                string id = monitor.Id;
                var monitorItem = new MenuItemModel
                {
                    Text = monitor.DisplayName,
                    Kind = MenuItemKind.Radio,
                    IsChecked = string.Equals(currentTarget, id, StringComparison.OrdinalIgnoreCase),
                    StaysOpenOnClick = true,
                    OnClick = self =>
                    {
                        _controller.SetTrayScrollTarget(id);
                        SelectExclusiveRadio(radioItems, self);
                    }
                };
                radioItems.Add(monitorItem);
                list.Add(monitorItem);
            }
        }

        return list;
    }

    private static void SelectExclusiveRadio(List<MenuItemModel> group, MenuItemModel selected)
    {
        foreach (var item in group)
        {
            item.IsChecked = ReferenceEquals(item, selected);
        }
    }

    private async void OnRefreshClicked()
    {
        if (_disposed || _isRefreshing) return;

        _isRefreshing = true;
        CancelRefreshReset();

        if (_refreshItem is not null)
        {
            ApplyRefreshSpinningState(_refreshItem);
            _rootPopup?.StartAnimationLoop();
            _rootPopup?.Invalidate();
        }

        try
        {
            await Task.WhenAll(_onRefresh(), Task.Delay(MinRefreshSpinMs));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Tray menu monitor refresh failed", ex);
        }
        finally
        {
            _isRefreshing = false;
        }

        if (_disposed) return;

        _dispatcher.BeginInvoke(async () =>
        {
            if (_disposed || _refreshItem is null || !IsOpen) return;

            var liveItem = _refreshItem;
            ApplyRefreshCompletedState(liveItem);
            _rootPopup?.Invalidate();

            var cts = new CancellationTokenSource();
            _refreshResetCts = cts;
            try
            {
                await Task.Delay(RefreshDoneDisplayMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_disposed || cts.IsCancellationRequested) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed || !ReferenceEquals(_refreshItem, liveItem) || _isRefreshing) return;
                ApplyRefreshIdleState(liveItem);
                _rootPopup?.Invalidate();
            });
        });
    }

    private void ApplyRefreshIdleState(MenuItemModel item)
    {
        item.Text = "Refresh Monitors";
        item.IconGlyph = "\uE72C";
        item.UseAccentIcon = false;
        item.ShowSpinner = false;
    }

    private void ApplyRefreshSpinningState(MenuItemModel item)
    {
        item.Text = "Refreshing Monitors...";
        item.IconGlyph = null;
        item.UseAccentIcon = true;
        item.ShowSpinner = true;
        item.SpinnerStartTimestamp = Stopwatch.GetTimestamp();
    }

    private void ApplyRefreshCompletedState(MenuItemModel item)
    {
        item.Text = _controller.Monitors.Count > 0 ? "Monitors Refreshed" : "No Monitors Found";
        item.IconGlyph = "\uE73E";
        item.UseAccentIcon = true;
        item.ShowSpinner = false;
    }

    private async void OnUpdateClicked()
    {
        if (_disposed || _isManualUpdateChecking) return;

        switch (_updateService.State)
        {
            case UpdateState.ReadyToRestart:
                _updateService.ApplyPendingUpdateAndRestart();
                break;

            case UpdateState.Idle:
            case UpdateState.Error:
                _isManualUpdateChecking = true;
                if (_updateItem is not null)
                {
                    ApplyUpdateSpinningState(_updateItem, "Checking for Updates...");
                    _rootPopup?.StartAnimationLoop();
                    _rootPopup?.Invalidate();
                }

                try
                {
                    await Task.WhenAll(
                        _updateService.CheckAndDownloadAsync(isManual: true),
                        Task.Delay(MinRefreshSpinMs));
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Manual update check failed", ex);
                }
                finally
                {
                    _isManualUpdateChecking = false;
                }

                if (_disposed) return;
                _dispatcher.BeginInvoke(() =>
                {
                    if (_disposed || _updateItem is null || !IsOpen) return;
                    ConfigureUpdateMenuItem(_updateItem);
                    _rootPopup?.Invalidate();
                });
                break;
        }
    }

    private static void ApplyUpdateSpinningState(MenuItemModel item, string text)
    {
        item.Text = text;
        item.IconGlyph = null;
        item.UseAccentIcon = true;
        item.ShowSpinner = true;
        item.SpinnerStartTimestamp = Stopwatch.GetTimestamp();
        item.IsEnabled = false;
        item.StaysOpenOnClick = true;
    }

    private void ConfigureUpdateMenuItem(MenuItemModel item)
    {
        if (_isManualUpdateChecking)
        {
            ApplyUpdateSpinningState(item, "Checking for Updates...");
            return;
        }

        item.ShowSpinner = false;

        switch (_updateService.State)
        {
            case UpdateState.ReadyToRestart:
                item.Text = $"Restart to Update (v{_updateService.AvailableVersion})";
                item.IconGlyph = "\uE896";
                item.UseAccentIcon = true;
                item.IsEnabled = true;
                item.StaysOpenOnClick = false;
                break;

            case UpdateState.Checking:
                ApplyUpdateSpinningState(item, "Checking for Updates...");
                break;

            case UpdateState.Downloading:
                ApplyUpdateSpinningState(item, "Downloading Update...");
                break;

            case UpdateState.UpToDate:
                item.Text = $"Up to Date (v{_updateService.CurrentVersion})";
                item.IconGlyph = "\uE73E";
                item.UseAccentIcon = true;
                item.IsEnabled = false;
                item.StaysOpenOnClick = true;
                break;

            case UpdateState.Error:
                item.Text = "Update Check Failed";
                item.IconGlyph = "\uE783";
                item.UseAccentIcon = false;
                item.IsEnabled = true;
                item.StaysOpenOnClick = true;
                break;

            default:
                item.Text = "Check for Updates";
                item.IconGlyph = "\uE895";
                item.UseAccentIcon = false;
                item.IsEnabled = true;
                item.StaysOpenOnClick = true;
                break;
        }
    }

    private void SubscribeLiveMenuUpdates()
    {
        if (_isListeningLiveUpdates) return;
        _isListeningLiveUpdates = true;
        _updateService.StateChanged += OnUpdateServiceStateChanged;
        _controller.MonitorsRefreshed += OnMonitorsRefreshed;
    }

    private void UnsubscribeLiveMenuUpdates()
    {
        if (!_isListeningLiveUpdates) return;
        _isListeningLiveUpdates = false;
        _updateService.StateChanged -= OnUpdateServiceStateChanged;
        _controller.MonitorsRefreshed -= OnMonitorsRefreshed;
    }

    private void OnUpdateServiceStateChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _updateItem is null || !IsOpen || _isManualUpdateChecking) return;
            ConfigureUpdateMenuItem(_updateItem);
            if (_updateItem.ShowSpinner) _rootPopup?.StartAnimationLoop();
            _rootPopup?.Invalidate();
        });
    }

    private void OnMonitorsRefreshed(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !IsOpen) return;

            if (_linkItem is not null)
            {
                _linkItem.IsEnabled = _controller.Monitors.Count > 1;
                _linkItem.IsChecked = _controller.Settings.LinkMonitors;
                _linkItem.SwitchProgress = _linkItem.IsChecked ? 1f : 0f;
            }

            if (_targetItem is not null)
            {
                _targetItem.SubItems = BuildScrollTargetSubItems();
            }

            _rootPopup?.Invalidate();
        });
    }

    private void CancelRefreshReset()
    {
        if (_refreshResetCts is { } cts)
        {
            _refreshResetCts = null;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private readonly record struct MenuPlacement(int TargetXPx, int TargetYPx, double Scale, bool IsTaskbarAtTop, IntPtr TaskbarHwnd);

    private MenuPlacement ComputeRootPlacement(float widthDip, float heightDip)
    {
        double scale = TaskbarPlacementHelper.GetDpiScale(_trayIconService.HostHwnd);
        double visualWidth = widthDip * scale;
        double visualHeight = heightDip * scale;
        double marginPx = 12.0 * scale;

        if (!TaskbarPlacementHelper.TryResolveTrayContext(_trayIconService, out var ctx))
        {
            NativeMethods.GetCursorPos(out POINT fallbackPt);
            return new MenuPlacement(fallbackPt.X, (int)Math.Round(fallbackPt.Y - visualHeight), scale, false, IntPtr.Zero);
        }

        double visualX = ctx.AnchorPx.X - (visualWidth / 2.0);
        double minX = ctx.MonitorInfo.rcWork.Left + marginPx;
        double maxX = Math.Max(minX, ctx.MonitorInfo.rcWork.Right - visualWidth - marginPx);
        visualX = Math.Clamp(visualX, minX, maxX);

        double visualY = ctx.VerticalBounds.IsTaskbarAtTop
            ? ctx.VerticalBounds.EffectiveTopPx + marginPx
            : ctx.VerticalBounds.EffectiveBottomPx - visualHeight - marginPx;

        if (ctx.HasTrayRect && ctx.HasTaskbarRect && !ctx.VerticalBounds.IsTaskbarAtTop &&
            ctx.TrayRect.Bottom <= ctx.TaskbarRect.Top)
        {
            visualY = Math.Max(ctx.MonitorInfo.rcWork.Top + marginPx, ctx.TrayRect.Top - visualHeight - marginPx);
        }

        return new MenuPlacement(
            (int)Math.Round(visualX),
            (int)Math.Round(visualY),
            scale,
            ctx.VerticalBounds.IsTaskbarAtTop,
            ctx.TaskbarHwnd);
    }

    private enum MenuItemKind
    {
        Normal,
        Switch,
        Radio,
        Submenu,
        Separator
    }

    private sealed class MenuItemModel
    {
        public string Text { get; set; } = string.Empty;
        public string? IconGlyph { get; set; }
        public MenuItemKind Kind { get; init; } = MenuItemKind.Normal;
        public bool IsChecked { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool StaysOpenOnClick { get; set; }
        public bool UseAccentIcon { get; set; }
        public bool ShowSpinner { get; set; }
        public long SpinnerStartTimestamp { get; set; }
        public float SwitchProgress { get; set; }
        public List<MenuItemModel>? SubItems { get; set; }
        public Action<MenuItemModel>? OnClick { get; init; }

        public static MenuItemModel Separator() => new() { Kind = MenuItemKind.Separator, IsEnabled = false };
    }

    private sealed class MenuPopup : IDisposable
    {
        private const float PadY = 4f;
        private const float ItemHeight = 34f;
        private const float SeparatorHeight = 7f;
        private const float ItemPadX = 4f;
        private const nuint AnimTimerId = 1;

        private readonly List<MenuItemModel> _items;
        private readonly ThemePalette _palette;
        private readonly Action<MenuItemModel> _onItemClicked;
        private readonly Action<MenuItemModel?, float>? _onHoverChanged;
        private readonly Action<IntPtr> _onDeactivated;
        private readonly Action _onEscape;

        private IntPtr _hwnd;
        private IntPtr _d2dFactory;
        private IntPtr _dwriteFactory;
        private IntPtr _rt;
        private IntPtr _solidBrush;
        private IntPtr _roundStroke;
        private IntPtr _fmtText;
        private IntPtr _fmtIcon14;
        private IntPtr _fmtChevron;

        private float _dpi = 96f;
        private int _hoveredIndex = -1;
        private bool _trackingMouse;
        private bool _animRunning;
        private long _lastAnimTick;
        private bool _disposed;

        public IntPtr Hwnd => _hwnd;
        public float WidthDip { get; }
        public float HeightDip { get; }
        public double Scale { get; private set; } = 1.0;
        public bool IsOpen { get; private set; }

        public MenuPopup(
            List<MenuItemModel> items,
            ThemePalette palette,
            float minWidthDip,
            Action<MenuItemModel> onItemClicked,
            Action<MenuItemModel?, float>? onHoverChanged,
            Action<IntPtr> onDeactivated,
            Action onEscape)
        {
            _items = items;
            _palette = palette;
            _onItemClicked = onItemClicked;
            _onHoverChanged = onHoverChanged;
            _onDeactivated = onDeactivated;
            _onEscape = onEscape;

            _d2dFactory = Direct2D.CreateD2DFactory();
            _dwriteFactory = Direct2D.CreateDWriteFactory();
            if (_dwriteFactory != IntPtr.Zero)
            {
                _fmtText = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe UI Variable Text", 13.5f,
                    textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_LEADING, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
                _fmtIcon14 = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe Fluent Icons", 14f,
                    textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_CENTER, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
                _fmtChevron = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe Fluent Icons", 10f,
                    textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_CENTER, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            }
            if (_d2dFactory != IntPtr.Zero)
            {
                _roundStroke = Direct2D.CreateRoundStrokeStyle(_d2dFactory);
            }

            float maxWidth = minWidthDip;
            float totalHeight = PadY * 2f;
            foreach (var item in _items)
            {
                if (item.Kind == MenuItemKind.Separator)
                {
                    totalHeight += SeparatorHeight;
                    continue;
                }

                totalHeight += ItemHeight;
                float textW = Direct2D.MeasureTextWidth(_dwriteFactory, item.Text, _fmtText);
                float rightExtra = item.Kind switch
                {
                    MenuItemKind.Switch => 52f,
                    MenuItemKind.Submenu => 28f,
                    _ => 10f
                };
                float rowW = 40f + textW + rightExtra + 16f;
                if (rowW > maxWidth) maxWidth = rowW;
            }

            WidthDip = (float)Math.Ceiling(maxWidth);
            HeightDip = (float)Math.Ceiling(totalHeight);
        }

        public void Show(int xPx, int yPx, double scale, bool activate, IntPtr insertAfterHwnd = default)
        {
            if (_disposed) return;

            Scale = scale > 0 ? scale : 1.0;
            _dpi = (float)(Scale * 96.0);

            IntPtr hInstance = NativeMethods.GetModuleHandleW(null);
            if (!s_classRegistered)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    style = NativeMethods.CS_HREDRAW | NativeMethods.CS_VREDRAW,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticMenuWndProc),
                    hInstance = hInstance,
                    hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(NativeMethods.IDC_ARROW)),
                    lpszClassName = MenuClassName
                };
                if (NativeMethods.RegisterClassExW(ref wc) != 0)
                {
                    s_classRegistered = true;
                }
            }

            int widthPx = Math.Max(1, (int)Math.Round(WidthDip * Scale));
            int heightPx = Math.Max(1, (int)Math.Round(HeightDip * Scale));

            uint exStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            if (!activate)
            {
                exStyle |= NativeMethods.WS_EX_NOACTIVATE;
            }

            s_creatingPopup = this;
            try
            {
                _hwnd = NativeMethods.CreateWindowExW(
                    exStyle,
                    MenuClassName,
                    "DimDockMenu",
                    NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
                    xPx, yPx, widthPx, heightPx,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            }
            finally
            {
                s_creatingPopup = null;
            }

            if (_hwnd == IntPtr.Zero) return;
            ActivePopups[_hwnd] = this;

            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_FRAMECHANGED);

            var margins = new MARGINS(-1);
            NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

            int darkMode = _palette.IsDark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int cornerPref = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

            int noBorder = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

            int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            // Cloak while showing, activating, and painting frame 1 so DWM never flashes an unrendered or inactive frame.
            int cloak = 1;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_CLOAK, ref cloak, sizeof(int));

            NativeMethods.ShowWindow(_hwnd, activate ? NativeMethods.SW_SHOW : NativeMethods.SW_SHOWNOACTIVATE);
            IsOpen = true;

            if (activate)
            {
                NativeMethods.SetForegroundWindow(_hwnd);
            }

            // Keep the menu directly behind the taskbar in topmost Z-order so its bottom shadow
            // is cleanly clipped behind the taskbar rather than spilling over it.
            if (insertAfterHwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    _hwnd,
                    insertAfterHwnd,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            Render();
            NativeMethods.DwmFlush();

            cloak = 0;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_CLOAK, ref cloak, sizeof(int));

            if (_items.Any(i => i.ShowSpinner))
            {
                StartAnimationLoop();
            }
        }

        public void StartAnimationLoop()
        {
            if (_animRunning || _hwnd == IntPtr.Zero || _disposed) return;
            _animRunning = true;
            _lastAnimTick = Stopwatch.GetTimestamp();
            NativeMethods.SetTimer(_hwnd, AnimTimerId, 16, IntPtr.Zero);
        }

        private void StopAnimationLoop()
        {
            if (!_animRunning) return;
            _animRunning = false;
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.KillTimer(_hwnd, AnimTimerId);
            }
        }

        public void Invalidate()
        {
            if (_hwnd != IntPtr.Zero && IsOpen)
            {
                NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
            }
        }

        private void EnsureRenderTarget()
        {
            if (_rt != IntPtr.Zero || _hwnd == IntPtr.Zero || _d2dFactory == IntPtr.Zero) return;
            NativeMethods.GetClientRect(_hwnd, out RECT rc);
            _rt = Direct2D.CreateHwndRenderTarget(_d2dFactory, _hwnd, (uint)Math.Max(1, rc.Width), (uint)Math.Max(1, rc.Height), _dpi);
            if (_rt != IntPtr.Zero)
            {
                _solidBrush = Direct2D.CreateSolidColorBrush(_rt, _palette.TextPrimary);
            }
        }

        private void Render()
        {
            if (_hwnd == IntPtr.Zero || _disposed) return;

            EnsureRenderTarget();
            if (_rt == IntPtr.Zero || _solidBrush == IntPtr.Zero) return;

            Direct2D.BeginDraw(_rt);
            Direct2D.Clear(_rt, ArgbColor.Transparent);
            if (_palette.BodyTint.A > 0)
            {
                Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.BodyTint);
                Direct2D.FillRectangle(_rt, new D2D1_RECT_F(0f, 0f, WidthDip, HeightDip), _solidBrush);
            }

            float y = PadY;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Kind == MenuItemKind.Separator)
                {
                    float lineY = y + (SeparatorHeight * 0.5f);
                    Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.MenuPopupBorder);
                    Direct2D.FillRectangle(_rt, new D2D1_RECT_F(0f, lineY, WidthDip, lineY + 1f), _solidBrush);
                    y += SeparatorHeight;
                    continue;
                }

                // Hover highlight
                if (i == _hoveredIndex && item.IsEnabled)
                {
                    var hlRect = new D2D1_ROUNDED_RECT(
                        new D2D1_RECT_F(ItemPadX, y + 1f, WidthDip - ItemPadX, y + ItemHeight - 1f), 4f);
                    Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.ButtonHover);
                    Direct2D.FillRoundedRectangle(_rt, hlRect, _solidBrush);
                }

                byte alphaScale = item.IsEnabled ? (byte)0xFF : (byte)0x6B;
                float iconCenterX = 22f;
                float centerY = y + (ItemHeight * 0.5f);

                // Left slot: Radio, Spinner, or Glyph
                if (item.Kind == MenuItemKind.Radio)
                {
                    var outer = new D2D1_ELLIPSE(iconCenterX, centerY, 7f);
                    if (item.IsChecked)
                    {
                        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.Accent);
                        Direct2D.FillEllipse(_rt, outer, _solidBrush);
                        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.AccentForeground);
                        Direct2D.FillEllipse(_rt, new D2D1_ELLIPSE(iconCenterX, centerY, 3f), _solidBrush);
                    }
                    else
                    {
                        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.TextMuted);
                        Direct2D.DrawEllipse(_rt, outer, _solidBrush, 1f);
                    }
                }
                else if (item.ShowSpinner)
                {
                    double elapsed = (Stopwatch.GetTimestamp() - item.SpinnerStartTimestamp) / (double)Stopwatch.Frequency;
                    Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.Accent);
                    FluentProgressRing.Draw(_d2dFactory, _rt, _solidBrush, _roundStroke, iconCenterX, centerY, elapsed);
                }
                else if (!string.IsNullOrEmpty(item.IconGlyph))
                {
                    ArgbColor baseCol = item.UseAccentIcon ? _palette.Accent : _palette.TextPrimary;
                    ArgbColor col = baseCol.WithAlpha((byte)((baseCol.A * alphaScale) / 255));
                    Direct2D.SetSolidColorBrushColor(_solidBrush, col);
                    Direct2D.DrawText(_rt, item.IconGlyph, _fmtIcon14,
                        new D2D1_RECT_F(10f, y, 34f, y + ItemHeight), _solidBrush);
                }

                // Label
                ArgbColor textCol = _palette.TextPrimary.WithAlpha((byte)((_palette.TextPrimary.A * alphaScale) / 255));
                Direct2D.SetSolidColorBrushColor(_solidBrush, textCol);
                float textRight = item.Kind == MenuItemKind.Switch ? WidthDip - 54f
                    : (item.Kind == MenuItemKind.Submenu ? WidthDip - 32f : WidthDip - 12f);
                Direct2D.DrawText(_rt, item.Text, _fmtText,
                    new D2D1_RECT_F(40f, y, textRight, y + ItemHeight), _solidBrush);

                // Right slot: Fluent Pill Switch or Submenu Chevron
                if (item.Kind == MenuItemKind.Switch)
                {
                    float trackW = 34f;
                    float trackH = 18f;
                    float trackLeft = WidthDip - 16f - trackW;
                    float trackTop = centerY - (trackH * 0.5f);
                    var trackRect = new D2D1_ROUNDED_RECT(
                        new D2D1_RECT_F(trackLeft, trackTop, trackLeft + trackW, trackTop + trackH), 9f);

                    if (item.IsChecked)
                    {
                        ArgbColor fill = _palette.Accent.WithAlpha((byte)((_palette.Accent.A * alphaScale) / 255));
                        Direct2D.SetSolidColorBrushColor(_solidBrush, fill);
                        Direct2D.FillRoundedRectangle(_rt, trackRect, _solidBrush);
                    }
                    else
                    {
                        ArgbColor border = _palette.TextMuted.WithAlpha((byte)((_palette.TextMuted.A * alphaScale) / 255));
                        Direct2D.SetSolidColorBrushColor(_solidBrush, border);
                        Direct2D.DrawRoundedRectangle(_rt, trackRect, _solidBrush, 1f);
                    }

                    float knobX = trackLeft + 9f + (16f * item.SwitchProgress);
                    ArgbColor knobCol = item.IsChecked ? _palette.AccentForeground : _palette.TextSecondary;
                    knobCol = knobCol.WithAlpha((byte)((knobCol.A * alphaScale) / 255));
                    Direct2D.SetSolidColorBrushColor(_solidBrush, knobCol);
                    Direct2D.FillEllipse(_rt, new D2D1_ELLIPSE(knobX, centerY, 5f), _solidBrush);
                }
                else if (item.Kind == MenuItemKind.Submenu)
                {
                    Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.TextSecondary);
                    Direct2D.DrawText(_rt, "\uE76C", _fmtChevron,
                        new D2D1_RECT_F(WidthDip - 28f, y + 1f, WidthDip - 10f, y + ItemHeight + 1f), _solidBrush);
                }

                y += ItemHeight;
            }

            int hr = Direct2D.EndDraw(_rt);
            if (hr == Direct2D.D2DERR_RECREATE_TARGET)
            {
                Direct2D.Release(ref _solidBrush);
                Direct2D.Release(ref _rt);
            }
        }

        private (int Index, float TopDip) HitTestItem(float yDip)
        {
            float y = PadY;
            for (int i = 0; i < _items.Count; i++)
            {
                float h = _items[i].Kind == MenuItemKind.Separator ? SeparatorHeight : ItemHeight;
                if (yDip >= y && yDip < y + h)
                {
                    return _items[i].Kind == MenuItemKind.Separator ? (-1, 0f) : (i, y);
                }
                y += h;
            }
            return (-1, 0f);
        }

        private void StepAnimations()
        {
            long now = Stopwatch.GetTimestamp();
            float dt = Math.Clamp((float)((now - _lastAnimTick) / (double)Stopwatch.Frequency), 0.001f, 0.05f);
            _lastAnimTick = now;

            bool keepRunning = false;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.ShowSpinner)
                {
                    keepRunning = true;
                }
                if (item.Kind == MenuItemKind.Switch)
                {
                    float target = item.IsChecked ? 1f : 0f;
                    float diff = target - item.SwitchProgress;
                    if (Math.Abs(diff) < 0.01f)
                    {
                        item.SwitchProgress = target;
                    }
                    else
                    {
                        item.SwitchProgress += diff * Math.Min(1f, dt * 18f);
                        keepRunning = true;
                    }
                }
            }

            Invalidate();
            if (!keepRunning)
            {
                StopAnimationLoop();
            }
        }

        internal IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case NativeMethods.WM_NCCALCSIZE:
                    if (wParam != IntPtr.Zero)
                    {
                        return IntPtr.Zero;
                    }
                    break;

                case NativeMethods.WM_ERASEBKGND:
                    return new IntPtr(1);

                case NativeMethods.WM_MOUSEACTIVATE:
                    // Prevent submenu click from stealing activation and dismissing root menu
                    if (_onHoverChanged is null)
                    {
                        return new IntPtr(NativeMethods.MA_NOACTIVATE);
                    }
                    break;

                case NativeMethods.WM_PAINT:
                {
                    NativeMethods.BeginPaint(hwnd, out PAINTSTRUCT ps);
                    Render();
                    NativeMethods.EndPaint(hwnd, ref ps);
                    return IntPtr.Zero;
                }

                case NativeMethods.WM_TIMER:
                    if ((nuint)wParam.ToInt64() == AnimTimerId)
                    {
                        StepAnimations();
                    }
                    return IntPtr.Zero;

                case NativeMethods.WM_ACTIVATE:
                {
                    int state = wParam.ToInt32() & 0xFFFF;
                    if (state == NativeMethods.WA_INACTIVE)
                    {
                        _onDeactivated(lParam);
                    }
                    return IntPtr.Zero;
                }

                case NativeMethods.WM_KEYDOWN:
                    OnKeyDown(wParam.ToInt32());
                    return IntPtr.Zero;

                case NativeMethods.WM_MOUSEMOVE:
                {
                    if (!_trackingMouse)
                    {
                        var tme = new TRACKMOUSEEVENT
                        {
                            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                            dwFlags = NativeMethods.TME_LEAVE,
                            hwndTrack = hwnd
                        };
                        NativeMethods.TrackMouseEvent(ref tme);
                        _trackingMouse = true;
                    }

                    int yPx = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                    float yDip = yPx / (_dpi / 96f);
                    var (idx, topDip) = HitTestItem(yDip);
                    if (idx != _hoveredIndex)
                    {
                        _hoveredIndex = idx;
                        Invalidate();
                        _onHoverChanged?.Invoke(idx >= 0 ? _items[idx] : null, topDip);
                    }
                    return IntPtr.Zero;
                }

                case NativeMethods.WM_MOUSELEAVE:
                    _trackingMouse = false;
                    if (_hoveredIndex != -1)
                    {
                        _hoveredIndex = -1;
                        Invalidate();
                    }
                    return IntPtr.Zero;

                case NativeMethods.WM_LBUTTONUP:
                {
                    int yPx = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                    float yDip = yPx / (_dpi / 96f);
                    var (idx, topDip) = HitTestItem(yDip);
                    if (idx >= 0)
                    {
                        var item = _items[idx];
                        if (item.Kind == MenuItemKind.Submenu)
                        {
                            _onHoverChanged?.Invoke(item, topDip);
                        }
                        else
                        {
                            _onItemClicked(item);
                        }
                    }
                    return IntPtr.Zero;
                }
            }

            return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        private void OnKeyDown(int vk)
        {
            switch (vk)
            {
                case NativeMethods.VK_ESCAPE:
                    _onEscape();
                    break;
                case NativeMethods.VK_DOWN:
                    MoveSelection(1);
                    break;
                case NativeMethods.VK_UP:
                    MoveSelection(-1);
                    break;
                case NativeMethods.VK_RETURN:
                case NativeMethods.VK_SPACE:
                    if (_hoveredIndex >= 0 && _hoveredIndex < _items.Count)
                    {
                        var item = _items[_hoveredIndex];
                        if (item.Kind != MenuItemKind.Submenu)
                        {
                            _onItemClicked(item);
                        }
                    }
                    break;
            }
        }

        private void MoveSelection(int dir)
        {
            if (_items.Count == 0) return;
            int idx = _hoveredIndex;
            for (int step = 0; step < _items.Count; step++)
            {
                idx = (idx + dir + _items.Count) % _items.Count;
                if (_items[idx].Kind != MenuItemKind.Separator && _items[idx].IsEnabled)
                {
                    _hoveredIndex = idx;
                    Invalidate();
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsOpen = false;

            StopAnimationLoop();

            Direct2D.Release(ref _solidBrush);
            Direct2D.Release(ref _rt);
            Direct2D.Release(ref _roundStroke);
            Direct2D.Release(ref _fmtText);
            Direct2D.Release(ref _fmtIcon14);
            Direct2D.Release(ref _fmtChevron);
            Direct2D.Release(ref _dwriteFactory);
            Direct2D.Release(ref _d2dFactory);

            if (_hwnd != IntPtr.Zero)
            {
                ActivePopups.Remove(_hwnd);
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
