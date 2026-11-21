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
    private bool _isHoverPinned;
    private bool _isShown;
    private bool _isClosing;
    private int _animGeneration;
    private DateTime _lastScrollUtc = DateTime.MinValue;

    public TrayToastWindow(TrayIconService trayIconService)
    {
        InitializeComponent();

        _trayIconService = trayIconService;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _hideTimer.Tick += OnHideTimerTick;
    }

    public void ShowHover(int? percent)
    {
        _isHoverPinned = true;
        _hideTimer.Stop();
        EnsureVisibleAndUpdate(percent);
    }

    public void ShowFromScroll(int? percent)
    {
        _lastScrollUtc = DateTime.UtcNow;
        EnsureVisibleAndUpdate(percent);

        if (!_isHoverPinned)
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }
    }

    public void UpdateIfVisible(int? percent)
    {
        if (!_isShown || !IsVisible) return;
        ApplyText(percent);
    }

    public void OnHoverClosed()
    {
        // Ignore spurious close signals while cursor is still hovering/scrolling on the icon.
        if ((DateTime.UtcNow - _lastScrollUtc).TotalMilliseconds < 300 && IsCursorOverTrayIcon())
        {
            return;
        }

        FadeOut();
    }

    public void FadeOut()
    {
        _isHoverPinned = false;
        _hideTimer.Stop();

        if (!_isShown || !IsVisible) return;

        _isShown = false;
        int gen = ++_animGeneration;
        double startOpacity = Opacity;

        // Set local value to 0 and animate with FillBehavior.Stop so no stale animation lingers.
        Opacity = 0.0;
        var fadeOut = new DoubleAnimation(startOpacity, 0.0, TimeSpan.FromMilliseconds(140))
        {
            FillBehavior = FillBehavior.Stop
        };
        fadeOut.Completed += (_, _) =>
        {
            if (gen == _animGeneration && !_isShown)
            {
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    public void HideImmediately()
    {
        _isHoverPinned = false;
        _isShown = false;
        _animGeneration++;
        _hideTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0.0;
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

    private void EnsureVisibleAndUpdate(int? percent)
    {
        ApplyText(percent);

        // Already shown and fully visible: update text in-place with zero animation/re-layout flicker.
        if (_isShown && IsVisible)
        {
            return;
        }

        _isShown = true;
        _animGeneration++;

        double startOpacity = IsVisible ? Opacity : 0.0;

        if (!IsVisible)
        {
            Opacity = 0.0;
            Show();
            UpdateLayout();
            PositionAboveTrayIcon();
        }

        // Set local Opacity = 1.0 BEFORE starting the Stop-behavior fade-in so it never snaps back to 0.
        Opacity = 1.0;
        var fadeIn = new DoubleAnimation(startOpacity, 1.0, TimeSpan.FromMilliseconds(90))
        {
            FillBehavior = FillBehavior.Stop
        };
        BeginAnimation(OpacityProperty, fadeIn);
    }

    private void ApplyText(int? percent)
    {
        if (percent.HasValue)
        {
            string formatted = $"({Math.Clamp(percent.Value, 0, 100)}%)";
            if (PercentText.Text != formatted)
            {
                PercentText.Text = formatted;
            }
            if (PercentText.Visibility != Visibility.Visible)
            {
                PercentText.Visibility = Visibility.Visible;
            }
        }
        else if (PercentText.Visibility != Visibility.Collapsed)
        {
            PercentText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        if (_isHoverPinned || IsCursorOverTrayIcon())
        {
            return;
        }

        FadeOut();
    }

    private bool IsCursorOverTrayIcon()
    {
        return NativeMethods.GetCursorPos(out POINT pt) &&
               _trayIconService.TryGetIconRect(out RECT rect) &&
               rect.Contains(pt);
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
