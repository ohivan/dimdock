using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DimDock.Interop;
using DimDock.Models;
using DimDock.Services;

namespace DimDock.UI;

public partial class FlyoutWindow : Window
{
    private const double OpenDurationMs = 300.0;
    private const double CloseDurationMs = 175.0;

    private readonly BrightnessController _controller;
    private readonly TrayIconService _trayIconService;
    private readonly SystemThemeService _themeService;
    private readonly NativeVSyncSlideAnimator _slideAnimator;
    private DateTime _lastDeactivatedUtc = DateTime.MinValue;
    private bool _isClosing;
    private bool _isHidingAnimated;
    private bool _isCloaked;

    // Warmup frame state before uncloaking and starting the VSync slide
    private bool _isWaitingWarmup;
    private int _warmupFramesRemaining;
    private int _pendingX;
    private int _pendingStartY;
    private int _pendingEndY;
    private IntPtr _pendingInsertAfter;

    public FlyoutWindow(
        BrightnessController controller,
        TrayIconService trayIconService,
        SystemThemeService themeService)
    {
        InitializeComponent();

        _controller = controller;
        _trayIconService = trayIconService;
        _themeService = themeService;
        _slideAnimator = new NativeVSyncSlideAnimator(Dispatcher);

        MonitorsList.ItemsSource = _controller.Monitors;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0)
        });

        _controller.MonitorsRefreshed += (_, _) => Dispatcher.BeginInvoke(SyncUiState);
        SyncUiState();
    }

    public void ToggleFromTray()
    {
        if (IsVisible && !_isHidingAnimated)
        {
            HideFlyout();
            return;
        }

        // Prevent immediate re-open when clicking the tray icon just triggered Deactivated.
        if ((DateTime.UtcNow - _lastDeactivatedUtc).TotalMilliseconds < 280)
        {
            return;
        }

        ShowFlyout();
    }

    public void ShowFlyout()
    {
        StopSlideAnimation();
        _isHidingAnimated = false;

        SyncUiState();

        // Ensure HWND exists so we can cloak it BEFORE Show() — preventing DWM from ever flashing
        // a raw unrendered gray Acrylic rectangle before WPF's render thread presents frame 1.
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        ApplyDwmBackdrop();

        bool wasHidden = !IsVisible;
        if (wasHidden)
        {
            SetCloaked(hwnd, true);
        }

        if (ActualHeight <= 0)
        {
            Measure(new Size(Width, 600));
        }

        var placement = ComputePlacement();

        int startY = placement.IsTaskbarAtTop
            ? placement.TargetYPx - placement.SlideDistancePx
            : placement.TargetYPx + placement.SlideDistancePx;

        Opacity = 1.0;
        Left = placement.TargetXPx / placement.Scale;
        Top = startY / placement.Scale;

        if (wasHidden)
        {
            Show();
            UpdateLayout();

            // Recompute exact placement once real rendered ActualHeight is known.
            placement = ComputePlacement();
            startY = placement.IsTaskbarAtTop
                ? placement.TargetYPx - placement.SlideDistancePx
                : placement.TargetYPx + placement.SlideDistancePx;
        }

        // Keep window directly behind the taskbar in topmost Z-order so it slides out from behind it.
        PositionWindowNative(hwnd, placement.TaskbarHwnd, placement.TargetXPx, startY);

        if (!wasHidden)
        {
            NativeMethods.SetForegroundWindow(hwnd);
            Activate();
            PositionWindowNative(hwnd, placement.TaskbarHwnd, placement.TargetXPx, startY);
            BeginNativeVSyncSlide(hwnd, opening: true, placement.TargetXPx, startY, placement.TargetYPx, OpenDurationMs);
        }
        else
        {
            // Wait 2 WPF render frames while cloaked so the swapchain is fully painted,
            // then uncloak, take focus, and run the native-Hz DwmFlush slide animation.
            _pendingX = placement.TargetXPx;
            _pendingStartY = startY;
            _pendingEndY = placement.TargetYPx;
            _pendingInsertAfter = placement.TaskbarHwnd;
            _warmupFramesRemaining = 2;
            _isWaitingWarmup = true;
            CompositionTarget.Rendering += OnWarmupFrame;
        }
    }

    public void HideFlyout(bool immediate = false)
    {
        if (!IsVisible) return;

        if (immediate || _isCloaked)
        {
            StopSlideAnimation();
            _isHidingAnimated = false;
            SetCloaked(new WindowInteropHelper(this).Handle, false);
            Hide();
            return;
        }

        if (_isHidingAnimated) return;

        StopSlideAnimation();
        _isHidingAnimated = true;

        var placement = ComputePlacement();
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int currentYPx = (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out RECT curRect))
            ? curRect.Top
            : (int)Math.Round(Top * placement.Scale);

        int endYPx = placement.IsTaskbarAtTop
            ? placement.TargetYPx - placement.SlideDistancePx
            : placement.TargetYPx + placement.SlideDistancePx;

        if (hwnd != IntPtr.Zero)
        {
            PositionWindowNative(hwnd, placement.TaskbarHwnd, placement.TargetXPx, currentYPx);
            BeginNativeVSyncSlide(hwnd, opening: false, placement.TargetXPx, currentYPx, endYPx, CloseDurationMs);
        }
        else
        {
            _isHidingAnimated = false;
            Hide();
        }
    }

    public void SyncUiState()
    {
        int count = _controller.Monitors.Count;
        EmptyStatePanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MonitorsList.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;

        LinkToggleButton.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        LinkToggleButton.IsChecked = _controller.Settings.LinkMonitors;
        LinkToggleButton.ToolTip = _controller.Settings.LinkMonitors
            ? "Monitors linked (click to unlink)"
            : "Link all monitors";

        if (IsVisible && !_slideAnimator.IsRunning && !_isWaitingWarmup && !_isHidingAnimated)
        {
            UpdateLayout();
            var placement = ComputePlacement();
            Left = placement.TargetXPx / placement.Scale;
            Top = placement.TargetYPx / placement.Scale;
        }
    }

    public void ApplyDwmBackdrop()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        var margins = new MARGINS(-1);
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int darkMode = _themeService.Current.IsAppDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        int cornerPref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        ApplyDwmBackdrop();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosing)
        {
            e.Cancel = true;
            HideFlyout(immediate: true);
            return;
        }

        StopSlideAnimation();
        base.OnClosing(e);
    }

    public void ForceClose()
    {
        _isClosing = true;
        StopSlideAnimation();
        _slideAnimator.Dispose();
        Close();
    }

    private void OnWarmupFrame(object? sender, EventArgs e)
    {
        if (!_isWaitingWarmup) return;

        _warmupFramesRemaining--;
        if (_warmupFramesRemaining > 0) return;

        _isWaitingWarmup = false;
        CompositionTarget.Rendering -= OnWarmupFrame;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        PositionWindowNative(hwnd, _pendingInsertAfter, _pendingX, _pendingStartY);
        SetCloaked(hwnd, false);
        // Take foreground focus AFTER uncloaking so Windows Shell sees the focus transfer cleanly.
        NativeMethods.SetForegroundWindow(hwnd);
        Activate();
        PositionWindowNative(hwnd, _pendingInsertAfter, _pendingX, _pendingStartY);

        BeginNativeVSyncSlide(hwnd, opening: true, _pendingX, _pendingStartY, _pendingEndY, OpenDurationMs);
    }

    private void BeginNativeVSyncSlide(IntPtr hwnd, bool opening, int xPx, int startYPx, int endYPx, double durationMs)
    {
        StopSlideAnimation();
        _slideAnimator.Start(
            hwnd,
            opening,
            xPx,
            startYPx,
            endYPx,
            durationMs,
            onCompleted: opening
                ? null
                : () =>
                {
                    _isHidingAnimated = false;
                    SetCloaked(hwnd, false);
                    Hide();
                });
    }

    private void StopSlideAnimation()
    {
        if (_isWaitingWarmup)
        {
            _isWaitingWarmup = false;
            _warmupFramesRemaining = 0;
            CompositionTarget.Rendering -= OnWarmupFrame;
        }

        _slideAnimator.Stop();
    }

    private void SetCloaked(IntPtr hwnd, bool cloaked)
    {
        _isCloaked = cloaked;
        if (hwnd == IntPtr.Zero) return;
        int value = cloaked ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAK, ref value, sizeof(int));
    }

    private static void PositionWindowNative(IntPtr hwnd, IntPtr insertAfter, int xPx, int yPx)
    {
        uint flags = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        if (insertAfter == IntPtr.Zero)
        {
            flags |= NativeMethods.SWP_NOZORDER;
        }

        NativeMethods.SetWindowPos(hwnd, insertAfter, xPx, yPx, 0, 0, flags);
    }

    private readonly record struct FlyoutPlacement(
        int TargetXPx,
        int TargetYPx,
        int SlideDistancePx,
        double Scale,
        bool IsTaskbarAtTop,
        IntPtr TaskbarHwnd);

    private FlyoutPlacement ComputePlacement()
    {
        if (!TaskbarPlacementHelper.TryResolveTrayContext(_trayIconService, out var ctx))
        {
            return new FlyoutPlacement(0, 0, 52, 1.0, false, IntPtr.Zero);
        }

        var mi = ctx.MonitorInfo;
        double scale = TaskbarPlacementHelper.GetDpiScale(new WindowInteropHelper(this).Handle);
        int effectiveTopPx = ctx.VerticalBounds.EffectiveTopPx;
        int effectiveBottomPx = ctx.VerticalBounds.EffectiveBottomPx;
        bool isTaskbarAtTop = ctx.VerticalBounds.IsTaskbarAtTop;

        double workLeft = mi.rcWork.Left / scale;
        double workRight = mi.rcWork.Right / scale;
        double effectiveTop = effectiveTopPx / scale;
        double effectiveBottom = effectiveBottomPx / scale;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : (DesiredSize.Height > 0 ? DesiredSize.Height : 145);
        const double margin = 12.0;

        double targetTop = isTaskbarAtTop
            ? effectiveTop + margin
            : effectiveBottom - h - margin;

        // Always anchor bottom-right (12px from right edge) to match Windows 11 Control Center
        // consistently whether DimDock is pinned on the taskbar or opened from the overflow dock.
        double targetLeft = Math.Max(workLeft + margin, workRight - w - margin);

        int targetYPx = (int)Math.Round(targetTop * scale);
        int slideDistancePx;

        if (isTaskbarAtTop)
        {
            IntPtr aboveMon = NativeMethods.MonitorFromPoint(
                new POINT { X = ctx.AnchorPx.X, Y = mi.rcMonitor.Top - 10 },
                0); // MONITOR_DEFAULTTONULL
            slideDistancePx = aboveMon == IntPtr.Zero
                ? Math.Max(48, targetYPx + (int)Math.Round(h * scale) - effectiveTopPx)
                : Math.Max(48, effectiveTopPx - mi.rcMonitor.Top);
        }
        else
        {
            IntPtr belowMon = NativeMethods.MonitorFromPoint(
                new POINT { X = ctx.AnchorPx.X, Y = mi.rcMonitor.Bottom + 10 },
                0); // MONITOR_DEFAULTTONULL
            // Slide completely out from behind the taskbar (top edge starts at effectiveBottomPx)
            // just like Windows 11 Control Center, unless another monitor is stacked underneath.
            slideDistancePx = belowMon == IntPtr.Zero
                ? Math.Max(48, effectiveBottomPx - targetYPx)
                : Math.Max(48, mi.rcMonitor.Bottom - effectiveBottomPx);
        }

        return new FlyoutPlacement(
            TargetXPx: (int)Math.Round(targetLeft * scale),
            TargetYPx: targetYPx,
            SlideDistancePx: slideDistancePx,
            Scale: scale,
            IsTaskbarAtTop: isTaskbarAtTop,
            TaskbarHwnd: ctx.TaskbarHwnd);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _lastDeactivatedUtc = DateTime.UtcNow;
        HideFlyout();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
        }
    }

    private void OnSliderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Slider slider) return;
        slider.ApplyTemplate();

        // .NET 10's built-in Fluent Slider gives the 20x20 Thumb's outer Border Margin="-2" (making it 24x24).
        // Zeroing that margin brings it to Windows 11 Quick Settings' exact 20x20 thumb diameter,
        // and insetting TrackBackground by 2px keeps the track ends tucked cleanly behind the thumb at 0 and 100.
        if (slider.Template.FindName("TrackBackground", slider) is Border trackBg)
        {
            trackBg.Margin = new Thickness(2, 0, 2, 0);
        }

        if (slider.Template.FindName("PART_Track", slider) is Track track &&
            track.Thumb is { } thumb)
        {
            thumb.ApplyTemplate();
            if (VisualTreeHelper.GetChildrenCount(thumb) > 0 &&
                VisualTreeHelper.GetChild(thumb, 0) is Border outerBorder)
            {
                outerBorder.Margin = new Thickness(0);
            }
        }
    }

    private void OnSliderPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || slider.Template.FindName("PART_Track", slider) is not Track track || track.Thumb is null)
        {
            return;
        }

        // If the user clicked directly on the thumb, let the Thumb handle drag natively.
        if (IsDescendantOf(e.OriginalSource as DependencyObject, track.Thumb))
        {
            return;
        }

        // Click-anywhere-and-drag: jump to the clicked point, then hand off immediately to the Thumb
        // so dragging continues smoothly without lifting the mouse button (matching Windows 11 Quick Settings).
        double value = track.ValueFromPoint(e.GetPosition(track));
        if (!double.IsNaN(value))
        {
            slider.Value = Math.Clamp(Math.Round(value), slider.Minimum, slider.Maximum);
            slider.UpdateLayout();

            e.Handled = true;
            var forwarded = new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
            {
                RoutedEvent = MouseLeftButtonDownEvent,
                Source = track.Thumb
            };
            track.Thumb.RaiseEvent(forwarded);
        }
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || slider.DataContext is not MonitorInfo monitor) return;

        int next = (int)Math.Round(e.NewValue);
        if (next == monitor.Brightness) return;

        _controller.SetMonitorBrightness(monitor.Id, next);
    }

    private void OnMonitorRowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MonitorInfo monitor) return;

        int notches = Math.Sign(e.Delta);
        if (notches != 0)
        {
            _controller.StepMonitorBrightness(monitor.Id, notches);
            e.Handled = true;
        }
    }

    private void OnLinkToggleClicked(object sender, RoutedEventArgs e)
    {
        bool linked = LinkToggleButton.IsChecked == true;
        _controller.SetLinkMonitors(linked);
        SyncUiState();
    }

    private async void OnRefreshButtonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _controller.RefreshMonitorsAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Manual monitor refresh failed", ex);
        }
    }
}
