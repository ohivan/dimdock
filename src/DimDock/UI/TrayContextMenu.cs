using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DimDock.Interop;
using DimDock.Models;
using DimDock.Services;

namespace DimDock.UI;

public sealed class TrayContextMenu : IDisposable
{
    internal const double OpenSlideDistanceDip = 16.0;
    internal const double OpenSlideDurationMs = 200.0;
    private const int MinRefreshSpinMs = 850;
    private const int RefreshDoneDisplayMs = 1700;

    private static readonly int[] StepOptions = [1, 2, 5, 10];
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly IEasingFunction FluentDecelerateEase = CreateFluentDecelerateEase();

    private readonly BrightnessController _controller;
    private readonly StartupService _startupService;
    private readonly UpdateService _updateService;
    private readonly TrayIconService _trayIconService;
    private readonly Func<Task> _onRefresh;
    private readonly Action _onLinkChanged;
    private readonly Action _onExit;
    private readonly NativeVSyncSlideAnimator _slideAnimator;
    private ContextMenu? _activeMenu;
    private MenuItem? _activeLinkItem;
    private MenuItem? _activeTargetMenu;
    private MenuItem? _activeRefreshItem;
    private FluentProgressRing? _activeRefreshRing;
    private MenuItem? _activeUpdateItem;
    private FluentProgressRing? _activeUpdateRing;
    private CancellationTokenSource? _refreshResetCts;
    private bool _isRefreshing;
    private bool _isManualUpdateChecking;
    private bool _isListeningLiveUpdates;
    private bool _lastPlacementIsTaskbarAtTop;
    private double _lastPlacementDpiScale = 1.0;
    private bool _disposed;

    public bool IsOpen => _activeMenu?.IsOpen == true;

    public event EventHandler? MenuOpened;
    public event EventHandler? MenuClosed;

    public TrayContextMenu(
        BrightnessController controller,
        StartupService startupService,
        UpdateService updateService,
        TrayIconService trayIconService,
        Func<Task> onRefresh,
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
        _slideAnimator = new NativeVSyncSlideAnimator(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
    }

    public void ShowAtCursor()
    {
        if (_disposed) return;

        _slideAnimator.Stop();

        if (_activeMenu is not null)
        {
            _activeMenu.IsOpen = false;
        }

        var menu = BuildMenu();
        _activeMenu = menu;
        _lastPlacementIsTaskbarAtTop = false;
        _lastPlacementDpiScale = TaskbarPlacementHelper.GetDpiScale(_trayIconService.HostHwnd);

        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = PlaceAboveTrayIcon;
        menu.Opened += OnMenuOpened;
        menu.Closed += OnMenuClosed;
        menu.PreviewMouseUp += OnMenuPreviewMouseUp;
        SubscribeLiveMenuUpdates();

        // Bringing the hidden tray host window to foreground lets clicking outside dismiss the ContextMenu reliably.
        NativeMethods.SetForegroundWindow(_trayIconService.HostHwnd);
        MenuOpened?.Invoke(this, EventArgs.Empty);
        menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelRefreshReset();
        StopRefreshRing();
        StopUpdateRing();
        UnsubscribeLiveMenuUpdates();
        _slideAnimator.Dispose();
        if (_activeMenu is not null)
        {
            _activeMenu.Opened -= OnMenuOpened;
            _activeMenu.Closed -= OnMenuClosed;
            _activeMenu.PreviewMouseUp -= OnMenuPreviewMouseUp;
            _activeMenu.IsOpen = false;
            _activeMenu = null;
        }
        _activeLinkItem = null;
        _activeTargetMenu = null;
        _activeRefreshItem = null;
        _activeUpdateItem = null;
    }

    internal static int ComputeSlideStartY(int targetYPx, double dpiScale, bool isTaskbarAtTop)
    {
        double safeScale = dpiScale > 0 ? dpiScale : 1.0;
        int offsetPx = Math.Max(1, (int)Math.Round(OpenSlideDistanceDip * safeScale));
        return isTaskbarAtTop
            ? targetYPx - offsetPx
            : targetYPx + offsetPx;
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not ContextMenu menu || !ReferenceEquals(menu, _activeMenu))
        {
            return;
        }

        if (PresentationSource.FromVisual(menu) is not HwndSource source || source.Handle == IntPtr.Zero)
        {
            return;
        }

        IntPtr hwnd = source.Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out RECT rect))
        {
            return;
        }

        double scale = TaskbarPlacementHelper.GetDpiScale(hwnd);
        if (scale <= 0)
        {
            scale = _lastPlacementDpiScale;
        }

        int targetXPx = rect.Left;
        int targetYPx = rect.Top;
        int startYPx = ComputeSlideStartY(targetYPx, scale, _lastPlacementIsTaskbarAtTop);

        // Move the popup HWND to the slide origin synchronously before WPF paints frame 1 so there is no jump.
        PositionPopupNative(hwnd, targetXPx, startYPx);

        // Start the VSync glide right after the initial Render pass completes (Loaded runs after Render)
        // so cold-start rendering never clips the beginning of the animation.
        menu.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_disposed || !menu.IsOpen || !ReferenceEquals(_activeMenu, menu))
            {
                return;
            }

            if (PresentationSource.FromVisual(menu) is not HwndSource liveSource || liveSource.Handle != hwnd)
            {
                return;
            }

            // If WPF repositioned the popup during layout/render, re-anchor the animation to its final position.
            if (NativeMethods.GetWindowRect(hwnd, out RECT currentRect) && currentRect.Top != startYPx)
            {
                targetXPx = currentRect.Left;
                targetYPx = currentRect.Top;
                startYPx = ComputeSlideStartY(targetYPx, scale, _lastPlacementIsTaskbarAtTop);
                PositionPopupNative(hwnd, targetXPx, startYPx);
            }

            _slideAnimator.Start(
                hwnd,
                NativeVSyncSlideAnimator.EvaluateMenuOpenEase,
                targetXPx,
                startYPx,
                targetYPx,
                OpenSlideDurationMs);
        });
    }

    private void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        menu.Opened -= OnMenuOpened;
        menu.Closed -= OnMenuClosed;
        menu.PreviewMouseUp -= OnMenuPreviewMouseUp;
        menu.CustomPopupPlacementCallback = null;
        menu.Items.Clear();

        if (ReferenceEquals(_activeMenu, menu))
        {
            CancelRefreshReset();
            StopRefreshRing();
            StopUpdateRing();
            UnsubscribeLiveMenuUpdates();
            _slideAnimator.Stop();
            _activeMenu = null;
            _activeLinkItem = null;
            _activeTargetMenu = null;
            _activeRefreshItem = null;
            _activeUpdateItem = null;
        }

        MenuClosed?.Invoke(this, EventArgs.Empty);
    }

    private static void OnMenuPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // In WPF, releasing the mouse over a disabled MenuItem, a Separator, or menu padding
        // bubbles up to MenuBase and dismisses the ContextMenu. Swallow those interior non-action
        // clicks while letting outside clicks (OriginalSource == menu) and enabled MenuItems work normally.
        if (sender is not ContextMenu menu || e.OriginalSource is not DependencyObject source) return;
        if (ReferenceEquals(source, menu)) return;

        var current = source;
        while (current is not null && !ReferenceEquals(current, menu))
        {
            if (current is MenuItem menuItem)
            {
                if (!menuItem.IsEnabled)
                {
                    e.Handled = true;
                }
                return;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        e.Handled = true;
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
        var menu = _activeMenu;
        var updateItem = _activeUpdateItem;
        if (_disposed || menu is null || updateItem is null) return;

        menu.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !ReferenceEquals(_activeMenu, menu) || !ReferenceEquals(_activeUpdateItem, updateItem))
            {
                return;
            }

            if (_isManualUpdateChecking)
            {
                return;
            }

            ConfigureUpdateMenuItem(updateItem, animate: true);
        });
    }

    private void OnMonitorsRefreshed(object? sender, EventArgs e)
    {
        var menu = _activeMenu;
        if (_disposed || menu is null) return;

        menu.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !ReferenceEquals(_activeMenu, menu))
            {
                return;
            }

            if (_activeLinkItem is not null)
            {
                _activeLinkItem.IsEnabled = _controller.Monitors.Count > 1;
                _activeLinkItem.IsChecked = _controller.Settings.LinkMonitors;
            }

            if (_activeTargetMenu is not null)
            {
                PopulateScrollTargetItems(_activeTargetMenu);
            }
        });
    }

    private static void PositionPopupNative(IntPtr hwnd, int xPx, int yPx)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            xPx,
            yPx,
            0,
            0,
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOZORDER);
    }

    private CustomPopupPlacement[] PlaceAboveTrayIcon(Size popupSize, Size targetSize, Point offset)
    {
        if (!TaskbarPlacementHelper.TryResolveTrayContext(_trayIconService, out var ctx))
        {
            _lastPlacementIsTaskbarAtTop = false;
            _lastPlacementDpiScale = TaskbarPlacementHelper.GetDpiScale(_trayIconService.HostHwnd);
            NativeMethods.GetCursorPos(out POINT fallbackPt);
            return [new CustomPopupPlacement(new Point(fallbackPt.X, fallbackPt.Y - popupSize.Height), PopupPrimaryAxis.Horizontal)];
        }

        double scale = TaskbarPlacementHelper.GetDpiScale(_trayIconService.HostHwnd);
        _lastPlacementIsTaskbarAtTop = ctx.VerticalBounds.IsTaskbarAtTop;
        _lastPlacementDpiScale = scale;
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
        _activeLinkItem = linkItem;
        menu.Items.Add(linkItem);

        menu.Items.Add(CreateSeparator());

        // Tray Scroll Target submenu
        var targetMenu = new MenuItem
        {
            Header = "Tray Scroll Target",
            Icon = CreateGlyphIcon("\uE7F4"),
            StaysOpenOnClick = true
        };
        PopulateScrollTargetItems(targetMenu);
        _activeTargetMenu = targetMenu;
        menu.Items.Add(targetMenu);

        // Scroll Step submenu
        var stepMenu = new MenuItem
        {
            Header = "Scroll Step",
            Icon = CreateGlyphIcon("\uE9E9"),
            StaysOpenOnClick = true
        };
        var stepRadioItems = new List<MenuItem>(StepOptions.Length);
        foreach (int step in StepOptions)
        {
            int capturedStep = step;
            var stepItem = new MenuItem
            {
                Header = $"{capturedStep}%",
                IsCheckable = true,
                Tag = "radio",
                IsChecked = _controller.Settings.ScrollStep == capturedStep,
                StaysOpenOnClick = true
            };
            stepItem.Click += (_, _) =>
            {
                _controller.SetScrollStep(capturedStep);
                SelectExclusiveRadioItem(stepRadioItems, stepItem);
            };
            stepRadioItems.Add(stepItem);
            stepMenu.Items.Add(stepItem);
        }
        menu.Items.Add(stepMenu);

        menu.Items.Add(CreateSeparator());

        // Refresh Monitors
        var refreshItem = new MenuItem
        {
            StaysOpenOnClick = true
        };
        if (_isRefreshing)
        {
            ApplyRefreshSpinningState(refreshItem);
        }
        else
        {
            ApplyRefreshIdleState(refreshItem);
        }
        refreshItem.Click += OnRefreshMenuItemClick;
        _activeRefreshItem = refreshItem;
        menu.Items.Add(refreshItem);

        // Updates
        var updateItem = new MenuItem();
        ConfigureUpdateMenuItem(updateItem);
        updateItem.Click += OnUpdateMenuItemClick;
        _activeUpdateItem = updateItem;
        menu.Items.Add(updateItem);

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

    private void PopulateScrollTargetItems(MenuItem targetMenu)
    {
        targetMenu.Items.Clear();

        string currentTarget = _controller.Settings.TrayScrollTarget;
        var radioItems = new List<MenuItem>(_controller.Monitors.Count + 1);

        var allItem = new MenuItem
        {
            Header = "All Monitors",
            IsCheckable = true,
            Tag = "radio",
            IsChecked = string.Equals(currentTarget, AppSettings.AllMonitorsTarget, StringComparison.OrdinalIgnoreCase),
            StaysOpenOnClick = true
        };
        allItem.Click += (_, _) =>
        {
            _controller.SetTrayScrollTarget(AppSettings.AllMonitorsTarget);
            SelectExclusiveRadioItem(radioItems, allItem);
        };
        radioItems.Add(allItem);
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
                    IsChecked = string.Equals(currentTarget, id, StringComparison.OrdinalIgnoreCase),
                    StaysOpenOnClick = true
                };
                monitorItem.Click += (_, _) =>
                {
                    _controller.SetTrayScrollTarget(id);
                    SelectExclusiveRadioItem(radioItems, monitorItem);
                };
                radioItems.Add(monitorItem);
                targetMenu.Items.Add(monitorItem);
            }
        }
    }

    private static void SelectExclusiveRadioItem(List<MenuItem> group, MenuItem selected)
    {
        foreach (var item in group)
        {
            item.IsChecked = ReferenceEquals(item, selected);
        }
    }

    private async void OnRefreshMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _isRefreshing) return;

        _isRefreshing = true;
        CancelRefreshReset();

        var refreshItem = _activeRefreshItem;
        if (refreshItem is not null)
        {
            ApplyRefreshSpinningState(refreshItem);
        }

        try
        {
            // Run the actual DDC/CI scan alongside a single smooth 360° spin floor so the
            // user always gets crisp visual feedback even when the scan finishes quickly.
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

        var liveItem = _activeRefreshItem;
        if (liveItem is null || !IsOpen) return;

        ApplyRefreshCompletedState(liveItem);

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
        if (ReferenceEquals(_activeRefreshItem, liveItem) && !_isRefreshing)
        {
            ApplyRefreshIdleState(liveItem, animateSettle: true);
        }
    }

    private void ApplyRefreshIdleState(MenuItem item, bool animateSettle = false)
    {
        StopRefreshRing();
        var icon = CreateGlyphIcon("\uE72C");
        if (animateSettle)
        {
            AnimateFluentIconReveal(icon, initialAngleDeg: -35.0);
        }

        item.Header = "Refresh Monitors";
        item.Icon = icon;
    }

    private void ApplyRefreshSpinningState(MenuItem item)
    {
        StopRefreshRing();

        var ring = new FluentProgressRing(ResolveAccentBrush());
        AnimateFluentIconReveal(ring, initialScale: 0.72);

        _activeRefreshRing = ring;
        item.Header = "Refreshing Monitors...";
        item.Icon = ring;
    }

    private void ApplyRefreshCompletedState(MenuItem item)
    {
        StopRefreshRing();

        var doneIcon = CreateGlyphIcon("\uE73E");
        doneIcon.Foreground = ResolveAccentBrush();
        AnimateFluentIconReveal(doneIcon, initialScale: 0.58);

        item.Header = _controller.Monitors.Count > 0 ? "Monitors Refreshed" : "No Monitors Found";
        item.Icon = doneIcon;
    }

    private void StopRefreshRing()
    {
        if (_activeRefreshRing is { } ring)
        {
            ring.Stop();
            _activeRefreshRing = null;
        }
    }

    private void StopUpdateRing()
    {
        if (_activeUpdateRing is { } ring)
        {
            ring.Stop();
            _activeUpdateRing = null;
        }
    }

    private static Brush ResolveAccentBrush() =>
        Application.Current?.TryFindResource("DimAccentBrush") as Brush
        ?? Brushes.DeepSkyBlue;

    private static IEasingFunction CreateFluentDecelerateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static void AnimateFluentIconReveal(FrameworkElement element, double initialScale = 0.85, double initialAngleDeg = 0.0)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.Opacity = 0.0;

        var scale = new ScaleTransform(initialScale, initialScale);
        var group = new TransformGroup();
        group.Children.Add(scale);

        RotateTransform? rotate = null;
        if (Math.Abs(initialAngleDeg) > 0.01)
        {
            rotate = new RotateTransform(initialAngleDeg);
            group.Children.Add(rotate);
        }

        element.RenderTransform = group;

        var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = FluentDecelerateEase
        };
        var scaleAnim = new DoubleAnimation(initialScale, 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = FluentDecelerateEase
        };

        element.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        if (rotate is not null)
        {
            var rotateAnim = new DoubleAnimation(initialAngleDeg, 0.0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = FluentDecelerateEase
            };
            rotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);
        }
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

    private async void OnUpdateMenuItemClick(object sender, RoutedEventArgs e)
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
                var updateItem = _activeUpdateItem;
                if (updateItem is not null)
                {
                    ApplyUpdateSpinningState(updateItem, "Checking for Updates...", animate: true);
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
                if (_activeUpdateItem is { } liveItem && IsOpen)
                {
                    ConfigureUpdateMenuItem(liveItem, animate: true);
                }
                break;
        }
    }

    private void ApplyUpdateSpinningState(MenuItem item, string header, bool animate)
    {
        StopUpdateRing();

        var ring = new FluentProgressRing(ResolveAccentBrush());
        if (animate)
        {
            AnimateFluentIconReveal(ring, initialScale: 0.72);
        }

        _activeUpdateRing = ring;
        item.Header = header;
        item.Icon = ring;
        item.IsEnabled = false;
        item.StaysOpenOnClick = true;
    }

    private void ConfigureUpdateMenuItem(MenuItem item, bool animate = false)
    {
        if (_isManualUpdateChecking)
        {
            ApplyUpdateSpinningState(item, "Checking for Updates...", animate);
            return;
        }

        StopUpdateRing();

        switch (_updateService.State)
        {
            case UpdateState.ReadyToRestart:
                item.Header = $"Restart to Update (v{_updateService.AvailableVersion})";
                var dlIcon = CreateGlyphIcon("\uE896");
                dlIcon.Foreground = ResolveAccentBrush();
                if (animate) AnimateFluentIconReveal(dlIcon, initialScale: 0.58);
                item.Icon = dlIcon;
                item.IsEnabled = true;
                item.StaysOpenOnClick = false;
                break;

            case UpdateState.Checking:
                ApplyUpdateSpinningState(item, "Checking for Updates...", animate);
                break;

            case UpdateState.Downloading:
                ApplyUpdateSpinningState(item, "Downloading Update...", animate);
                break;

            case UpdateState.UpToDate:
                item.Header = $"Up to Date (v{_updateService.CurrentVersion})";
                var upToDateIcon = CreateGlyphIcon("\uE73E");
                upToDateIcon.Foreground = ResolveAccentBrush();
                if (animate) AnimateFluentIconReveal(upToDateIcon, initialScale: 0.58);
                item.Icon = upToDateIcon;
                item.IsEnabled = false;
                item.StaysOpenOnClick = true;
                break;

            case UpdateState.Error:
                item.Header = "Update Check Failed";
                var errIcon = CreateGlyphIcon("\uE783");
                if (animate) AnimateFluentIconReveal(errIcon, initialScale: 0.75);
                item.Icon = errIcon;
                item.IsEnabled = true;
                item.StaysOpenOnClick = true;
                break;

            default:
                item.Header = "Check for Updates";
                var idleIcon = CreateGlyphIcon("\uE895");
                if (animate) AnimateFluentIconReveal(idleIcon, initialAngleDeg: -35.0);
                item.Icon = idleIcon;
                item.IsEnabled = true;
                item.StaysOpenOnClick = true;
                break;
        }
    }
}
