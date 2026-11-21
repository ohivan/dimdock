using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DimDock.Interop;
using DimDock.Services;

namespace DimDock.UI;

public partial class TrayToastWindow : Window
{
    private readonly TrayIconService _trayIconService;
    private readonly DispatcherTimer _hideTimer;
    private bool _isClosing;

    public TrayToastWindow(TrayIconService trayIconService)
    {
        InitializeComponent();

        _trayIconService = trayIconService;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1250) };
        _hideTimer.Tick += OnHideTimerTick;
    }

    public void ShowBrightness(int percent)
    {
        PercentText.Text = $"({Math.Clamp(percent, 0, 100)}%)";

        _hideTimer.Stop();
        BeginAnimation(OpacityProperty, null);

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        UpdateLayout();
        PositionAboveTrayIcon();

        var fadeIn = new DoubleAnimation(Opacity, 1.0, TimeSpan.FromMilliseconds(90));
        BeginAnimation(OpacityProperty, fadeIn);

        _hideTimer.Start();
    }

    public void HideImmediately()
    {
        _hideTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        if (IsVisible) Hide();
    }

    public void ForceClose()
    {
        _isClosing = true;
        _hideTimer.Stop();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosing)
        {
            e.Cancel = true;
            HideImmediately();
            return;
        }

        base.OnClosing(e);
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();

        var fadeOut = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) =>
        {
            if (!_hideTimer.IsEnabled && Opacity <= 0.01)
            {
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void PositionAboveTrayIcon()
    {
        if (!_trayIconService.TryGetIconRect(out RECT trayRect))
        {
            return;
        }

        var anchorPx = new POINT
        {
            X = (trayRect.Left + trayRect.Right) / 2,
            Y = trayRect.Top
        };

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

        double w = ActualWidth;
        double h = ActualHeight;

        double left = (anchorPx.X / scale) - (w / 2.0);
        left = Math.Clamp(left, workLeft + 4, Math.Max(workLeft + 4, workRight - w - 4));

        // Position snugly above the tray icon (accounting for the 8px drop-shadow margin).
        double top = (trayRect.Top / scale) - h - 2;
        if (top < workTop)
        {
            top = (trayRect.Bottom / scale) + 2;
        }

        Left = Math.Round(left);
        Top = Math.Round(Math.Min(top, workBottom - h));
    }
}
