using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private const double OpenDurationMs = 230.0;
    private const double CloseDurationMs = 150.0;

    private readonly BrightnessController _controller;
    private readonly TrayIconService _trayIconService;
    private readonly SystemThemeService _themeService;
    private DateTime _lastDeactivatedUtc = DateTime.MinValue;
    private bool _isClosing;
    private bool _isHidingAnimated;
    private bool _isCloaked;

    // Active VSync slide animation state
    private bool _isAnimating;
    private bool _animatingOpen;
    private int _warmupFramesRemaining;
    private long _animStartTicks;
    private double _animDurationMs;
    private int _animX;
    private int _animStartY;
    private int _animEndY;
    private IntPtr _animInsertAfter;

    public FlyoutWindow(
        BrightnessController controller,
        TrayIconService trayIconService,
        SystemThemeService themeService)
    {
        InitializeComponent();

        _controller = controller;
        _trayIconService = trayIconService;
        _themeService = themeService;

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
        }

        // If the window was just shown from hidden, wait 2 render frames while cloaked so WPF finishes
        // presenting its painted surface before uncloaking, taking focus (which dismisses the overflow dock),
        // and starting the slide.
        StartSlideAnimation(
            opening: true,
            xPx: placement.TargetXPx,
            startYPx: startY,
            endYPx: placement.TargetYPx,
            durationMs: OpenDurationMs,
            insertAfter: placement.TaskbarHwnd,
            warmupFrames: wasHidden ? 2 : 0);
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
        int currentYPx = (int)Math.Round(Top * placement.Scale);
        int endYPx = placement.IsTaskbarAtTop
            ? placement.TargetYPx - placement.SlideDistancePx
            : placement.TargetYPx + placement.SlideDistancePx;

        StartSlideAnimation(
            opening: false,
            xPx: placement.TargetXPx,
            startYPx: currentYPx,
            endYPx: endYPx,
            durationMs: CloseDurationMs,
            insertAfter: placement.TaskbarHwnd);
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

        if (IsVisible && !_isAnimating && !_isHidingAnimated)
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
        Close();
    }

    private void StartSlideAnimation(
        bool opening,
        int xPx,
        int startYPx,
        int endYPx,
        double durationMs,
        IntPtr insertAfter,
        int warmupFrames = 0)
    {
        _animatingOpen = opening;
        _animX = xPx;
        _animStartY = startYPx;
        _animEndY = endYPx;
        _animDurationMs = durationMs;
        _animInsertAfter = insertAfter;
        _warmupFramesRemaining = warmupFrames;
        _animStartTicks = Stopwatch.GetTimestamp();

        if (!_isAnimating)
        {
            _isAnimating = true;
            CompositionTarget.Rendering += OnAnimationFrame;
        }
    }

    private void StopSlideAnimation()
    {
        if (_isAnimating)
        {
            _isAnimating = false;
            _warmupFramesRemaining = 0;
            CompositionTarget.Rendering -= OnAnimationFrame;
        }
    }

    private void OnAnimationFrame(object? sender, EventArgs e)
    {
        if (!_isAnimating) return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        // Wait for WPF's render thread to present the first painted frame before uncloaking.
        if (_warmupFramesRemaining > 0)
        {
            _warmupFramesRemaining--;
            if (_warmupFramesRemaining == 0)
            {
                if (hwnd != IntPtr.Zero)
                {
                    PositionWindowNative(hwnd, _animInsertAfter, _animX, _animStartY);
                    SetCloaked(hwnd, false);
                    // Take foreground focus AFTER uncloaking (and after Explorer finishes its click)
                    // so Windows Shell cleanly dismisses the overflow dock and enables Deactivated/Escape.
                    NativeMethods.SetForegroundWindow(hwnd);
                    Activate();
                    PositionWindowNative(hwnd, _animInsertAfter, _animX, _animStartY);
                }
                _animStartTicks = Stopwatch.GetTimestamp();
            }
            return;
        }

        double elapsedMs = (Stopwatch.GetTimestamp() - _animStartTicks) * 1000.0 / Stopwatch.Frequency;
        double t = Math.Clamp(elapsedMs / _animDurationMs, 0.0, 1.0);

        // Fluent deceleration on open (1 - (1 - t)^4), crisp acceleration on close (t^3).
        double eased = _animatingOpen
            ? 1.0 - Math.Pow(1.0 - t, 4.0)
            : t * t * t;

        int currentY = (int)Math.Round(_animStartY + (_animEndY - _animStartY) * eased);
        if (hwnd != IntPtr.Zero)
        {
            PositionWindowNative(hwnd, _animInsertAfter, _animX, currentY);
        }

        if (t >= 1.0)
        {
            StopSlideAnimation();
            if (!_animatingOpen)
            {
                _isHidingAnimated = false;
                if (hwnd != IntPtr.Zero)
                {
                    SetCloaked(hwnd, false);
                }
                Hide();
            }
        }
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

    private readonly record struct FlyoutPlacement(
        int TargetXPx,
        int TargetYPx,
        int SlideDistancePx,
        double Scale,
        bool IsTaskbarAtTop,
        IntPtr TaskbarHwnd);

    private FlyoutPlacement ComputePlacement()
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
            return new FlyoutPlacement(0, 0, 52, 1.0, false, IntPtr.Zero);
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        uint dpi = hwnd != IntPtr.Zero ? NativeMethods.GetDpiForWindow(hwnd) : 96;
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;

        var (taskbarHwnd, taskbarRect) = FindTaskbarForMonitor(mi.rcMonitor);
        bool hasTaskbarRect = taskbarHwnd != IntPtr.Zero && taskbarRect.Width > 0 && taskbarRect.Height > 0;

        // Account for auto-hide taskbars: when auto-hide is enabled, rcWork equals rcMonitor,
        // so use the taskbar's live GetWindowRect top/bottom when it is popped up on screen.
        int effectiveBottomPx = mi.rcWork.Bottom;
        int effectiveTopPx = mi.rcWork.Top;
        bool isTaskbarAtTop = false;

        if (hasTaskbarRect)
        {
            int midY = (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2;
            bool taskbarIsHorizontal = taskbarRect.Width > taskbarRect.Height;

            if (taskbarIsHorizontal && taskbarRect.Top > midY && taskbarRect.Top < mi.rcMonitor.Bottom - 4)
            {
                // Bottom taskbar (always-visible OR popped-up auto-hide)
                effectiveBottomPx = Math.Min(effectiveBottomPx, taskbarRect.Top);
            }
            else if (taskbarIsHorizontal && taskbarRect.Bottom < midY && taskbarRect.Bottom > mi.rcMonitor.Top + 4)
            {
                // Top taskbar
                isTaskbarAtTop = true;
                effectiveTopPx = Math.Max(effectiveTopPx, taskbarRect.Bottom);
            }
        }

        double workLeft = mi.rcWork.Left / scale;
        double workRight = mi.rcWork.Right / scale;
        double effectiveTop = effectiveTopPx / scale;
        double effectiveBottom = effectiveBottomPx / scale;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : (DesiredSize.Height > 0 ? DesiredSize.Height : 145);
        const double margin = 12.0;

        // True overflow detection that works with both normal and auto-hide taskbars:
        // if the tray icon rect sits outside the taskbar rect, it is inside the overflow dock.
        bool isInOverflow = false;
        if (hasTrayRect && hasTaskbarRect)
        {
            isInOverflow = isTaskbarAtTop
                ? trayRect.Top >= taskbarRect.Bottom
                : trayRect.Bottom <= taskbarRect.Top;
        }

        double targetTop = isTaskbarAtTop
            ? effectiveTop + margin
            : effectiveBottom - h - margin;

        double targetLeft = (!hasTrayRect || isInOverflow)
            ? workRight - w - margin
            : (anchorPx.X / scale) - (w / 2.0);

        targetLeft = Math.Clamp(targetLeft, workLeft + margin, Math.Max(workLeft + margin, workRight - w - margin));

        int targetYPx = (int)Math.Round(targetTop * scale);
        int slideDistancePx;

        if (isTaskbarAtTop)
        {
            IntPtr aboveMon = NativeMethods.MonitorFromPoint(
                new POINT { X = anchorPx.X, Y = mi.rcMonitor.Top - 10 },
                0); // MONITOR_DEFAULTTONULL
            slideDistancePx = aboveMon == IntPtr.Zero
                ? Math.Max(48, targetYPx + (int)Math.Round(h * scale) - effectiveTopPx)
                : Math.Max(48, effectiveTopPx - mi.rcMonitor.Top);
        }
        else
        {
            IntPtr belowMon = NativeMethods.MonitorFromPoint(
                new POINT { X = anchorPx.X, Y = mi.rcMonitor.Bottom + 10 },
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
            TaskbarHwnd: taskbarHwnd);
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
        await _controller.RefreshMonitorsAsync();
    }
}
