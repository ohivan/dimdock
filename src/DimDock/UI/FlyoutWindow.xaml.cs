using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using DimDock.Interop;
using DimDock.Models;
using DimDock.Services;

namespace DimDock.UI;

public partial class FlyoutWindow : Window
{
    private readonly BrightnessController _controller;
    private readonly TrayIconService _trayIconService;
    private readonly SystemThemeService _themeService;
    private DateTime _lastDeactivatedUtc = DateTime.MinValue;
    private bool _isClosing;

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
        if (IsVisible)
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
        SyncUiState();
        ApplyDwmBackdrop();

        // Measure desired size before positioning.
        Opacity = 0;
        Show();
        UpdateLayout();
        PositionAboveTray();

        // Smooth Windows 11 flyout entrance animation.
        SlideTransform.Y = 12;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slideAnim = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        var fadeAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };

        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        BeginAnimation(OpacityProperty, fadeAnim);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetForegroundWindow(hwnd);
        Activate();
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        Hide();
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

        if (IsVisible)
        {
            UpdateLayout();
            PositionAboveTray();
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
            HideFlyout();
            return;
        }

        base.OnClosing(e);
    }

    public void ForceClose()
    {
        _isClosing = true;
        Close();
    }

    private void PositionAboveTray()
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
            return;
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        uint dpi = hwnd != IntPtr.Zero ? NativeMethods.GetDpiForWindow(hwnd) : 96;
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;

        double workLeft = mi.rcWork.Left / scale;
        double workTop = mi.rcWork.Top / scale;
        double workRight = mi.rcWork.Right / scale;
        double workBottom = mi.rcWork.Bottom / scale;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 140;
        const double margin = 12.0;

        double targetLeft = hasTrayRect
            ? (anchorPx.X / scale) - (w / 2.0)
            : workRight - w - margin;

        // Clamp horizontally inside working area with 12px edge padding.
        targetLeft = Math.Clamp(targetLeft, workLeft + margin, Math.Max(workLeft + margin, workRight - w - margin));

        // Anchor cleanly above bottom taskbar (or inside work area if taskbar is on top/side).
        double targetTop = workBottom - h - margin;
        if (hasTrayRect)
        {
            double trayTopDip = trayRect.Top / scale;
            double trayBottomDip = trayRect.Bottom / scale;

            // If taskbar is at top of screen, place flyout below it.
            if (trayBottomDip <= workTop + 16)
            {
                targetTop = workTop + margin;
            }
            else if (trayTopDip >= workBottom - 16)
            {
                targetTop = workBottom - h - margin;
            }
            else
            {
                targetTop = Math.Clamp(trayTopDip - h - margin, workTop + margin, workBottom - h - margin);
            }
        }

        Left = Math.Round(targetLeft);
        Top = Math.Round(targetTop);
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
        // Zeroing that margin brings it to Windows 11 Quick Settings' exact 20x20 thumb diameter.
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
