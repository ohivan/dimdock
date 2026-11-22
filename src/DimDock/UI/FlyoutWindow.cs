using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DimDock.Interop;
using DimDock.Models;
using DimDock.Services;

namespace DimDock.UI;

/// <summary>
/// Native Win32 + Direct2D Windows 11 Quick Settings Acrylic brightness flyout.
/// </summary>
internal sealed class FlyoutWindow : IDisposable
{
    private const double OpenDurationMs = 300.0;
    private const double CloseDurationMs = 175.0;
    private const float FlyoutWidthDip = 356f;
    private const float BodyMaxHeightDip = 420f;
    private const float BodyPadLeft = 20f;
    private const float BodyPadRight = 20f;
    private const float BodyPadTop = 18f;
    private const float BodyPadBottom = 6f;
    private const float RowHeaderHeight = 22f;
    private const float RowHeaderGap = 4f;
    private const float RowSliderHeight = 28f;
    private const float RowBottomMargin = 12f;
    private const float RowTotalHeight = RowHeaderHeight + RowHeaderGap + RowSliderHeight + RowBottomMargin;
    private const float ValueColWidth = 46f;
    private const float DividerHeight = 1f;
    private const float FooterHeight = 48f;
    private const float EmptyStateHeight = 148f;
    private const nuint AnimTimerId = 1;

    private static bool s_classRegistered;
    private static readonly string WindowClassName = "DimDock_FlyoutWindow";
    private static readonly NativeMethods.WndProcDelegate StaticFlyoutWndProc = DispatchFlyoutWndProc;
    private static readonly Dictionary<IntPtr, FlyoutWindow> ActiveFlyouts = [];
    private static FlyoutWindow? s_creatingFlyout;

    private static IntPtr DispatchFlyoutWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (ActiveFlyouts.TryGetValue(hwnd, out var flyout))
        {
            return flyout.WndProc(hwnd, msg, wParam, lParam);
        }

        if (s_creatingFlyout is { } creating)
        {
            return creating.WndProc(hwnd, msg, wParam, lParam);
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private readonly Win32Dispatcher _dispatcher;
    private readonly BrightnessController _controller;
    private readonly TrayIconService _trayIconService;
    private readonly SystemThemeService _themeService;
    private readonly NativeVSyncSlideAnimator _slideAnimator;
    private readonly EventHandler _monitorsRefreshedHandler;
    private readonly PropertyChangedEventHandler _monitorPropChangedHandler;
    private readonly List<MonitorInfo> _subscribedMonitors = [];

    private IntPtr _hwnd;
    private IntPtr _d2dFactory;
    private IntPtr _dwriteFactory;
    private IntPtr _rt;
    private IntPtr _solidBrush;
    private IntPtr _noiseBrush;
    private IntPtr _fmtIcon20;
    private IntPtr _fmtIcon26;
    private IntPtr _fmtIcon14;
    private IntPtr _fmtName;
    private IntPtr _fmtValue;
    private IntPtr _fmtFooter;
    private IntPtr _fmtEmptyTitle;
    private IntPtr _fmtEmptySub;
    private IntPtr _fmtButton;

    private ThemePalette _palette;
    private float _dpi = 96f;
    private float _currentWidthDip = FlyoutWidthDip;
    private float _currentHeightDip = 145f;
    private float _scrollOffsetDip;
    private bool _isVisible;
    private bool _isHidingAnimated;
    private bool _isCloaked;
    private bool _isDwmBackdropActive = true;
    private bool _trackingMouse;
    private bool _disposed;
    private DateTime _lastDeactivatedUtc = DateTime.MinValue;
    private IntPtr _lastTaskbarHwnd;

    // Interactive states
    private int _hoveredSliderIndex = -1;
    private int _draggingSliderIndex = -1;
    private bool _linkHovered;
    private bool _refreshBtnHovered;
    private bool _refreshBtnPressed;
    private float[] _thumbDotScales = [];
    private long _lastAnimTick;
    private bool _animTimerRunning;

    public bool IsVisible => _isVisible;
    public bool IsDwmBackdropActive => _isDwmBackdropActive;
    public bool IsAnimating => _slideAnimator.IsRunning || _isHidingAnimated;

    public event EventHandler? FlyoutShown;
    public event EventHandler? FlyoutHidden;

    public FlyoutWindow(
        Win32Dispatcher dispatcher,
        BrightnessController controller,
        TrayIconService trayIconService,
        SystemThemeService themeService)
    {
        _dispatcher = dispatcher;
        _controller = controller;
        _trayIconService = trayIconService;
        _themeService = themeService;
        _slideAnimator = new NativeVSyncSlideAnimator(_dispatcher.BeginInvoke);

        _palette = _themeService.BuildPalette(useDwmBackdrop: true);
        _monitorsRefreshedHandler = (_, _) => _dispatcher.BeginInvoke(SyncUiState);
        _monitorPropChangedHandler = (_, _) => _dispatcher.BeginInvoke(Invalidate);

        _controller.MonitorsRefreshed += _monitorsRefreshedHandler;

        EnsureWindowCreated();
        SyncUiState();
    }

    public void ToggleFromTray()
    {
        if (_isVisible && !_isHidingAnimated)
        {
            HideFlyout();
            return;
        }

        if ((DateTime.UtcNow - _lastDeactivatedUtc).TotalMilliseconds < 280)
        {
            return;
        }

        ShowFlyout();
    }

    public void ShowFlyout()
    {
        if (_disposed) return;

        StopSlideAnimation();
        _isHidingAnimated = false;
        FlyoutShown?.Invoke(this, EventArgs.Empty);

        EnsureWindowCreated();
        if (_hwnd == IntPtr.Zero) return;

        SyncUiState();
        ApplyDwmBackdrop();

        bool wasHidden = !_isVisible;
        if (wasHidden)
        {
            SetCloaked(true);
        }

        var placement = ComputePlacement();
        _dpi = (float)(placement.Scale * 96.0);
        int widthPx = Math.Max(1, (int)Math.Round(_currentWidthDip * placement.Scale));
        int heightPx = Math.Max(1, (int)Math.Round(_currentHeightDip * placement.Scale));

        int startY = placement.IsTaskbarAtTop
            ? placement.TargetYPx - placement.SlideDistancePx
            : placement.TargetYPx + placement.SlideDistancePx;

        NativeMethods.SetWindowPos(
            _hwnd,
            placement.TaskbarHwnd != IntPtr.Zero ? placement.TaskbarHwnd : NativeMethods.HWND_TOPMOST,
            placement.TargetXPx,
            startY,
            widthPx,
            heightPx,
            NativeMethods.SWP_NOACTIVATE);

        if (wasHidden)
        {
            // Show and activate WHILE still cloaked so DWM transitions DWMSBT_TRANSIENTWINDOW
            // from inactive solid fallback to active live Acrylic before frame 1 becomes visible.
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
            _isVisible = true;
            ApplyDwmBackdrop();
            NativeMethods.SetForegroundWindow(_hwnd);
            PositionWindowNative(placement.TaskbarHwnd, placement.TargetXPx, startY);

            // Paint frame 1 synchronously and flush one DWM composition cycle while cloaked.
            Render();
            NativeMethods.DwmFlush();

            SetCloaked(false);
            PositionWindowNative(placement.TaskbarHwnd, placement.TargetXPx, startY);
        }
        else
        {
            NativeMethods.SetForegroundWindow(_hwnd);
            PositionWindowNative(placement.TaskbarHwnd, placement.TargetXPx, startY);
            Invalidate();
        }

        _lastTaskbarHwnd = placement.TaskbarHwnd;
        BeginNativeVSyncSlide(opening: true, placement.TargetXPx, startY, placement.TargetYPx, OpenDurationMs);
    }

    public void HideFlyout(bool immediate = false)
    {
        if (!_isVisible || _hwnd == IntPtr.Zero) return;

        StopAnimTimer();
        if (_draggingSliderIndex >= 0)
        {
            _draggingSliderIndex = -1;
            NativeMethods.ReleaseCapture();
        }

        if (immediate || _isCloaked)
        {
            StopSlideAnimation();
            _isHidingAnimated = false;
            SetCloaked(false);
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            _isVisible = false;
            FlyoutHidden?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_isHidingAnimated) return;

        StopSlideAnimation();
        _isHidingAnimated = true;

        var placement = ComputePlacement();
        int currentYPx = NativeMethods.GetWindowRect(_hwnd, out RECT curRect)
            ? curRect.Top
            : placement.TargetYPx;

        int endYPx = placement.IsTaskbarAtTop
            ? placement.TargetYPx - placement.SlideDistancePx
            : placement.TargetYPx + placement.SlideDistancePx;

        PositionWindowNative(placement.TaskbarHwnd, placement.TargetXPx, currentYPx);
        BeginNativeVSyncSlide(opening: false, placement.TargetXPx, currentYPx, endYPx, CloseDurationMs);
    }

    public void SyncUiState()
    {
        if (_disposed) return;

        ResubscribeMonitors();

        int count = _controller.Monitors.Count;
        if (_thumbDotScales.Length != count)
        {
            _thumbDotScales = new float[count];
            Array.Fill(_thumbDotScales, 1.0f);
        }

        float contentHeight = count > 0
            ? BodyPadTop + (count * RowTotalHeight) + BodyPadBottom
            : EmptyStateHeight;
        float bodyHeight = Math.Min(contentHeight, BodyMaxHeightDip);
        float maxScroll = Math.Max(0f, contentHeight - bodyHeight);
        _scrollOffsetDip = Math.Clamp(_scrollOffsetDip, 0f, maxScroll);

        _currentWidthDip = FlyoutWidthDip;
        _currentHeightDip = bodyHeight + DividerHeight + FooterHeight;

        if (_isVisible && !_slideAnimator.IsRunning && !_isHidingAnimated && _hwnd != IntPtr.Zero)
        {
            var placement = ComputePlacement();
            int widthPx = Math.Max(1, (int)Math.Round(_currentWidthDip * placement.Scale));
            int heightPx = Math.Max(1, (int)Math.Round(_currentHeightDip * placement.Scale));
            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                placement.TargetXPx,
                placement.TargetYPx,
                widthPx,
                heightPx,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
        }

        Invalidate();
    }

    public void ApplyDwmBackdrop()
    {
        if (_hwnd == IntPtr.Zero) return;

        var margins = new MARGINS(-1);
        NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        int darkMode = _themeService.Current.IsAppDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        int cornerPref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        int hr = NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        _isDwmBackdropActive = hr == 0;
        _palette = _themeService.BuildPalette(useDwmBackdrop: _isDwmBackdropActive);
        Invalidate();
    }

    public void ForceClose()
    {
        Dispose();
    }

    private void EnsureWindowCreated()
    {
        if (_hwnd != IntPtr.Zero || _disposed) return;

        IntPtr hInstance = NativeMethods.GetModuleHandleW(null);
        if (!s_classRegistered)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = NativeMethods.CS_HREDRAW | NativeMethods.CS_VREDRAW,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticFlyoutWndProc),
                hInstance = hInstance,
                hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(NativeMethods.IDC_ARROW)),
                lpszClassName = WindowClassName
            };

            if (NativeMethods.RegisterClassExW(ref wc) != 0)
            {
                s_classRegistered = true;
            }
        }

        s_creatingFlyout = this;
        try
        {
            // Match WPF WindowStyle="None" + WindowChrome (WS_POPUP without WS_CAPTION) so DWM
            // renders the light, tight flyout shadow instead of a heavy active app-window shadow,
            // while still supporting DWMWCP_ROUND and DWMSBT_TRANSIENTWINDOW Desktop Acrylic.
            _hwnd = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST,
                WindowClassName,
                "DimDock",
                NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
                0, 0, 356, 145,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);
        }
        finally
        {
            s_creatingFlyout = null;
        }

        if (_hwnd != IntPtr.Zero)
        {
            ActiveFlyouts[_hwnd] = this;
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_FRAMECHANGED);
            uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
            _dpi = dpi > 0 ? dpi : 96f;
        }
    }

    private void EnsureDirect2DResources()
    {
        if (_hwnd == IntPtr.Zero) return;

        if (_d2dFactory == IntPtr.Zero)
        {
            _d2dFactory = Direct2D.CreateD2DFactory();
        }
        if (_dwriteFactory == IntPtr.Zero)
        {
            _dwriteFactory = Direct2D.CreateDWriteFactory();
        }

        if (_d2dFactory == IntPtr.Zero || _dwriteFactory == IntPtr.Zero) return;

        if (_fmtIcon20 == IntPtr.Zero)
        {
            _fmtIcon20 = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe Fluent Icons", 20f,
                textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_LEADING, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            _fmtIcon26 = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe Fluent Icons", 26f,
                textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_CENTER, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            _fmtIcon14 = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe Fluent Icons", 14f,
                textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_CENTER, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            _fmtName = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe UI Variable Text", 14.5f,
                textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_LEADING, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER,
                noWrap: true, ellipsisTrimming: true);
            _fmtValue = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe UI Variable Text", 20f,
                textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_TRAILING, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            _fmtFooter = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe UI Variable Text", 13f,
                textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_LEADING, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            _fmtEmptyTitle = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe UI Variable Text", 14f,
                weight: Direct2D.DWRITE_FONT_WEIGHT_SEMIBOLD,
                textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_CENTER, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            _fmtEmptySub = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe UI Variable Text", 12f,
                textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_CENTER, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_NEAR,
                noWrap: false);
            _fmtButton = Direct2D.CreateTextFormat(_dwriteFactory, "Segoe UI Variable Text", 13f,
                textAlignment: Direct2D.DWRITE_TEXT_ALIGNMENT_CENTER, paragraphAlignment: Direct2D.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        }

        if (_rt == IntPtr.Zero)
        {
            NativeMethods.GetClientRect(_hwnd, out RECT rc);
            uint w = (uint)Math.Max(1, rc.Width);
            uint h = (uint)Math.Max(1, rc.Height);
            _rt = Direct2D.CreateHwndRenderTarget(_d2dFactory, _hwnd, w, h, _dpi);
            if (_rt != IntPtr.Zero)
            {
                _solidBrush = Direct2D.CreateSolidColorBrush(_rt, _palette.TextPrimary);
                _noiseBrush = Direct2D.CreateNoiseBitmapBrush(_rt, _dpi);
            }
        }
    }

    private void ReleaseRenderTarget()
    {
        Direct2D.Release(ref _noiseBrush);
        Direct2D.Release(ref _solidBrush);
        Direct2D.Release(ref _rt);
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero || _disposed) return;

        EnsureDirect2DResources();
        if (_rt == IntPtr.Zero || _solidBrush == IntPtr.Zero) return;

        NativeMethods.GetClientRect(_hwnd, out RECT clientRect);
        uint pixW = (uint)Math.Max(1, clientRect.Width);
        uint pixH = (uint)Math.Max(1, clientRect.Height);
        Direct2D.Resize(_rt, pixW, pixH);
        Direct2D.SetDpi(_rt, _dpi, _dpi);

        float scale = _dpi / 96f;
        float widthDip = pixW / scale;
        float heightDip = pixH / scale;
        float bodyHeight = Math.Max(0f, heightDip - DividerHeight - FooterHeight);

        Direct2D.BeginDraw(_rt);
        Direct2D.Clear(_rt, ArgbColor.Transparent);

        // 1. Body surface tint + 2% Acrylic noise
        var bodyRect = new D2D1_RECT_F(0f, 0f, widthDip, bodyHeight);
        if (_palette.BodyTint.A > 0)
        {
            Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.BodyTint);
            Direct2D.FillRectangle(_rt, bodyRect, _solidBrush);
        }
        if (_noiseBrush != IntPtr.Zero)
        {
            Direct2D.FillRectangle(_rt, bodyRect, _noiseBrush);
        }

        // 2. Body content (clipped to bodyRect)
        Direct2D.PushAxisAlignedClip(_rt, bodyRect);
        int count = _controller.Monitors.Count;
        if (count == 0)
        {
            DrawEmptyState(widthDip);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                float rowY = BodyPadTop + (i * RowTotalHeight) - _scrollOffsetDip;
                if (rowY + RowTotalHeight < 0 || rowY > bodyHeight) continue;
                DrawMonitorRow(i, _controller.Monitors[i], rowY, widthDip);
            }

            // Slim vertical scrollbar if needed
            float totalContentHeight = BodyPadTop + (count * RowTotalHeight) + BodyPadBottom;
            if (totalContentHeight > bodyHeight + 1f)
            {
                float trackTop = 6f;
                float trackHeight = Math.Max(16f, bodyHeight - 12f);
                float thumbHeight = Math.Max(24f, trackHeight * (bodyHeight / totalContentHeight));
                float maxScroll = totalContentHeight - bodyHeight;
                float scrollRatio = maxScroll > 0f ? _scrollOffsetDip / maxScroll : 0f;
                float thumbTop = trackTop + (trackHeight - thumbHeight) * scrollRatio;
                var scrollRect = new D2D1_ROUNDED_RECT(
                    new D2D1_RECT_F(widthDip - 8f, thumbTop, widthDip - 2f, thumbTop + thumbHeight), 3f);
                Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.ScrollThumb);
                Direct2D.FillRoundedRectangle(_rt, scrollRect, _solidBrush);
            }
        }
        Direct2D.PopAxisAlignedClip(_rt);

        // 3. 1px Translucent Divider
        var dividerRect = new D2D1_RECT_F(0f, bodyHeight, widthDip, bodyHeight + DividerHeight);
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.Divider);
        Direct2D.FillRectangle(_rt, dividerRect, _solidBrush);

        // 4. 48px Footer
        float footerTop = bodyHeight + DividerHeight;
        var footerRect = new D2D1_RECT_F(0f, footerTop, widthDip, heightDip);
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.FooterTint);
        Direct2D.FillRectangle(_rt, footerRect, _solidBrush);
        if (_noiseBrush != IntPtr.Zero)
        {
            Direct2D.FillRectangle(_rt, footerRect, _noiseBrush);
        }

        // Footer label: "Adjust Brightness"
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.TextSecondary);
        Direct2D.DrawText(_rt, "Adjust Brightness", _fmtFooter,
            new D2D1_RECT_F(20f, footerTop, widthDip - 52f, heightDip - 2f), _solidBrush);

        // Footer Link toggle button (when > 1 monitor)
        if (count > 1)
        {
            var linkRect = GetLinkButtonRect(widthDip, footerTop);
            bool linked = _controller.Settings.LinkMonitors;
            if (linked || _linkHovered)
            {
                Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.ButtonHover);
                Direct2D.FillRoundedRectangle(_rt, new D2D1_ROUNDED_RECT(linkRect, 5f), _solidBrush);
            }

            ArgbColor iconColor = linked
                ? _palette.Accent
                : (_linkHovered ? _palette.TextPrimary : _palette.TextMuted);
            Direct2D.SetSolidColorBrushColor(_solidBrush, iconColor);
            Direct2D.DrawText(_rt, "\uE71B", _fmtIcon14, linkRect, _solidBrush);
        }

        int hr = Direct2D.EndDraw(_rt);
        if (hr == Direct2D.D2DERR_RECREATE_TARGET)
        {
            ReleaseRenderTarget();
        }
    }

    private void DrawMonitorRow(int index, MonitorInfo monitor, float y, float widthDip)
    {
        float contentRight = widthDip - BodyPadRight;

        // Monitor icon + friendly name
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.TextPrimary);
        Direct2D.DrawText(_rt, "\uE7F4", _fmtIcon20,
            new D2D1_RECT_F(BodyPadLeft, y, BodyPadLeft + 24f, y + RowHeaderHeight), _solidBrush);

        Direct2D.DrawText(_rt, monitor.DisplayName, _fmtName,
            new D2D1_RECT_F(BodyPadLeft + 30f, y, contentRight, y + RowHeaderHeight), _solidBrush);

        // Slider + value readout
        float sliderTop = y + RowHeaderHeight + RowHeaderGap;
        float sliderCenterY = sliderTop + (RowSliderHeight * 0.5f);
        var trackBounds = GetSliderTrackBounds(widthDip);
        float ratio = Math.Clamp(monitor.Brightness, 0, 100) / 100f;
        float thumbCenterX = trackBounds.ThumbMinX + (trackBounds.ThumbMaxX - trackBounds.ThumbMinX) * ratio;

        // Background track (4 DIP tall, 2 DIP radius)
        var bgTrack = new D2D1_ROUNDED_RECT(
            new D2D1_RECT_F(trackBounds.TrackLeft, sliderCenterY - 2f, trackBounds.TrackRight, sliderCenterY + 2f), 2f);
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.SliderTrack);
        Direct2D.FillRoundedRectangle(_rt, bgTrack, _solidBrush);

        // Active accent fill
        if (thumbCenterX > trackBounds.TrackLeft + 1f)
        {
            var fillTrack = new D2D1_ROUNDED_RECT(
                new D2D1_RECT_F(trackBounds.TrackLeft, sliderCenterY - 2f, thumbCenterX, sliderCenterY + 2f), 2f);
            Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.Accent);
            Direct2D.FillRoundedRectangle(_rt, fillTrack, _solidBrush);
        }

        // Thumb outer circle (20 DIP diameter, 1 DIP subtle border)
        var outerEllipse = new D2D1_ELLIPSE(thumbCenterX, sliderCenterY, 10f);
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.SliderThumbOuter);
        Direct2D.FillEllipse(_rt, outerEllipse, _solidBrush);
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.SliderThumbBorder);
        Direct2D.DrawEllipse(_rt, new D2D1_ELLIPSE(thumbCenterX, sliderCenterY, 9.5f), _solidBrush, 1f);

        // Thumb inner accent dot (12 DIP base, animated on hover/drag)
        float dotScale = index < _thumbDotScales.Length ? _thumbDotScales[index] : 1.0f;
        ArgbColor dotColor = _draggingSliderIndex == index
            ? _palette.AccentPressed
            : (_hoveredSliderIndex == index ? _palette.AccentHover : _palette.Accent);
        Direct2D.SetSolidColorBrushColor(_solidBrush, dotColor);
        Direct2D.FillEllipse(_rt, new D2D1_ELLIPSE(thumbCenterX, sliderCenterY, 6f * dotScale), _solidBrush);

        // Brightness readout (0..100)
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.TextPrimary);
        Direct2D.DrawText(_rt, monitor.Brightness.ToString(), _fmtValue,
            new D2D1_RECT_F(contentRight - ValueColWidth, sliderTop - 1f, contentRight, sliderTop + RowSliderHeight - 1f), _solidBrush);
    }

    private void DrawEmptyState(float widthDip)
    {
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.TextMuted);
        Direct2D.DrawText(_rt, "\uE7F4", _fmtIcon26,
            new D2D1_RECT_F(20f, 18f, widthDip - 20f, 48f), _solidBrush);

        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.TextPrimary);
        Direct2D.DrawText(_rt, "No DDC/CI monitors detected", _fmtEmptyTitle,
            new D2D1_RECT_F(20f, 52f, widthDip - 20f, 72f), _solidBrush);

        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.TextMuted);
        Direct2D.DrawText(_rt, "Ensure DDC/CI is enabled in your monitor's OSD menu.", _fmtEmptySub,
            new D2D1_RECT_F(24f, 76f, widthDip - 24f, 108f), _solidBrush);

        var btnRect = GetRefreshButtonRect(widthDip);
        ArgbColor bg = _refreshBtnPressed ? _palette.ButtonPressed
            : (_refreshBtnHovered ? _palette.ButtonHover : _palette.ButtonBg);
        Direct2D.SetSolidColorBrushColor(_solidBrush, bg);
        Direct2D.FillRoundedRectangle(_rt, new D2D1_ROUNDED_RECT(btnRect, 5f), _solidBrush);
        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.ButtonBorder);
        Direct2D.DrawRoundedRectangle(_rt, new D2D1_ROUNDED_RECT(btnRect, 5f), _solidBrush, 1f);

        Direct2D.SetSolidColorBrushColor(_solidBrush, _palette.TextPrimary);
        Direct2D.DrawText(_rt, "Refresh monitors", _fmtButton, btnRect, _solidBrush);
    }

    private readonly record struct SliderTrackBounds(float TrackLeft, float TrackRight, float ThumbMinX, float ThumbMaxX);

    private static SliderTrackBounds GetSliderTrackBounds(float widthDip)
    {
        float left = BodyPadLeft + 2f;
        float right = widthDip - BodyPadRight - ValueColWidth - 2f;
        return new SliderTrackBounds(left, right, left + 8f, right - 8f);
    }

    private static D2D1_RECT_F GetLinkButtonRect(float widthDip, float footerTop)
    {
        float right = widthDip - 14f;
        float left = right - 30f;
        float top = footerTop + (FooterHeight - 28f) * 0.5f;
        return new D2D1_RECT_F(left, top, right, top + 28f);
    }

    private static D2D1_RECT_F GetRefreshButtonRect(float widthDip)
    {
        float btnW = 136f;
        float left = (widthDip - btnW) * 0.5f;
        return new D2D1_RECT_F(left, 110f, left + btnW, 140f);
    }

    private int HitTestSlider(float xDip, float yDip)
    {
        float bodyHeight = _currentHeightDip - DividerHeight - FooterHeight;
        if (yDip < 0 || yDip > bodyHeight) return -1;

        var bounds = GetSliderTrackBounds(_currentWidthDip);
        if (xDip < bounds.TrackLeft - 10f || xDip > bounds.TrackRight + 10f) return -1;

        for (int i = 0; i < _controller.Monitors.Count; i++)
        {
            float rowY = BodyPadTop + (i * RowTotalHeight) - _scrollOffsetDip;
            float sliderTop = rowY + RowHeaderHeight + RowHeaderGap;
            if (yDip >= sliderTop - 2f && yDip <= sliderTop + RowSliderHeight + 2f)
            {
                return i;
            }
        }
        return -1;
    }

    private int HitTestMonitorRow(float yDip)
    {
        float bodyHeight = _currentHeightDip - DividerHeight - FooterHeight;
        if (yDip < 0 || yDip > bodyHeight) return -1;

        for (int i = 0; i < _controller.Monitors.Count; i++)
        {
            float rowY = BodyPadTop + (i * RowTotalHeight) - _scrollOffsetDip;
            if (yDip >= rowY && yDip <= rowY + RowTotalHeight)
            {
                return i;
            }
        }
        return -1;
    }

    private void ApplySliderDrag(int index, float xDip)
    {
        if (index < 0 || index >= _controller.Monitors.Count) return;
        var bounds = GetSliderTrackBounds(_currentWidthDip);
        float t = Math.Clamp((xDip - bounds.ThumbMinX) / Math.Max(1f, bounds.ThumbMaxX - bounds.ThumbMinX), 0f, 1f);
        int next = (int)Math.Round(t * 100f);
        var monitor = _controller.Monitors[index];
        if (next != monitor.Brightness)
        {
            _controller.SetMonitorBrightness(monitor.Id, next);
            Invalidate();
        }
    }

    private void StartAnimTimerIfNeeded()
    {
        if (_animTimerRunning || _hwnd == IntPtr.Zero) return;
        _animTimerRunning = true;
        _lastAnimTick = Stopwatch.GetTimestamp();
        NativeMethods.SetTimer(_hwnd, AnimTimerId, 16, IntPtr.Zero);
    }

    private void StopAnimTimer()
    {
        if (!_animTimerRunning) return;
        _animTimerRunning = false;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_hwnd, AnimTimerId);
        }
    }

    private void StepThumbAnimations()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = Math.Clamp((float)((now - _lastAnimTick) / (double)Stopwatch.Frequency), 0.001f, 0.05f);
        _lastAnimTick = now;

        bool anyMoving = false;
        float speed = 16f;
        for (int i = 0; i < _thumbDotScales.Length; i++)
        {
            float target = _draggingSliderIndex == i ? 0.833f
                : (_hoveredSliderIndex == i ? 1.167f : 1.0f);
            float diff = target - _thumbDotScales[i];
            if (Math.Abs(diff) < 0.005f)
            {
                _thumbDotScales[i] = target;
            }
            else
            {
                _thumbDotScales[i] += diff * Math.Min(1f, dt * speed);
                anyMoving = true;
            }
        }

        Invalidate();
        if (!anyMoving)
        {
            StopAnimTimer();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
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
                    StepThumbAnimations();
                }
                return IntPtr.Zero;

            case NativeMethods.WM_ACTIVATE:
            {
                int state = wParam.ToInt32() & 0xFFFF;
                if (state == NativeMethods.WA_INACTIVE)
                {
                    _lastDeactivatedUtc = DateTime.UtcNow;
                    HideFlyout();
                }
                else if (_lastTaskbarHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetWindowPos(
                        hwnd,
                        _lastTaskbarHwnd,
                        0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
                return IntPtr.Zero;
            }

            case NativeMethods.WM_KEYDOWN:
                if (wParam.ToInt32() == NativeMethods.VK_ESCAPE)
                {
                    HideFlyout();
                    return IntPtr.Zero;
                }
                break;

            case NativeMethods.WM_MOUSEMOVE:
                OnMouseMove(lParam);
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSELEAVE:
                _trackingMouse = false;
                if (_hoveredSliderIndex != -1 || _linkHovered || _refreshBtnHovered)
                {
                    _hoveredSliderIndex = -1;
                    _linkHovered = false;
                    _refreshBtnHovered = false;
                    StartAnimTimerIfNeeded();
                    Invalidate();
                }
                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONDOWN:
                OnLeftButtonDown(lParam);
                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONUP:
                OnLeftButtonUp(lParam);
                return IntPtr.Zero;

            case NativeMethods.WM_CAPTURECHANGED:
                if (_draggingSliderIndex >= 0 || _refreshBtnPressed)
                {
                    _draggingSliderIndex = -1;
                    _refreshBtnPressed = false;
                    StartAnimTimerIfNeeded();
                    Invalidate();
                }
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEWHEEL:
                OnMouseWheel(wParam, lParam);
                return IntPtr.Zero;

            case NativeMethods.WM_DPICHANGED:
                uint newDpi = (uint)(wParam.ToInt64() & 0xFFFF);
                if (newDpi > 0)
                {
                    _dpi = newDpi;
                    ReleaseRenderTarget();
                    SyncUiState();
                }
                return IntPtr.Zero;

            case NativeMethods.WM_CLOSE:
                HideFlyout(immediate: true);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private (float X, float Y) UnpackDipCoords(IntPtr lParam)
    {
        int xPx = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int yPx = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        float scale = _dpi / 96f;
        return (xPx / scale, yPx / scale);
    }

    private void OnMouseMove(IntPtr lParam)
    {
        if (!_trackingMouse && _hwnd != IntPtr.Zero)
        {
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = NativeMethods.TME_LEAVE,
                hwndTrack = _hwnd
            };
            NativeMethods.TrackMouseEvent(ref tme);
            _trackingMouse = true;
        }

        var (x, y) = UnpackDipCoords(lParam);

        if (_draggingSliderIndex >= 0)
        {
            ApplySliderDrag(_draggingSliderIndex, x);
            return;
        }

        int prevHover = _hoveredSliderIndex;
        bool prevLink = _linkHovered;
        bool prevRefresh = _refreshBtnHovered;

        _hoveredSliderIndex = HitTestSlider(x, y);
        float footerTop = _currentHeightDip - FooterHeight;
        _linkHovered = _controller.Monitors.Count > 1 && GetLinkButtonRect(_currentWidthDip, footerTop).Contains(x, y);
        _refreshBtnHovered = _controller.Monitors.Count == 0 && GetRefreshButtonRect(_currentWidthDip).Contains(x, y);

        if (_hoveredSliderIndex != prevHover)
        {
            StartAnimTimerIfNeeded();
        }
        if (_hoveredSliderIndex != prevHover || _linkHovered != prevLink || _refreshBtnHovered != prevRefresh)
        {
            Invalidate();
        }
    }

    private void OnLeftButtonDown(IntPtr lParam)
    {
        var (x, y) = UnpackDipCoords(lParam);

        int sliderIdx = HitTestSlider(x, y);
        if (sliderIdx >= 0)
        {
            _draggingSliderIndex = sliderIdx;
            NativeMethods.SetCapture(_hwnd);
            ApplySliderDrag(sliderIdx, x);
            StartAnimTimerIfNeeded();
            return;
        }

        float footerTop = _currentHeightDip - FooterHeight;
        if (_controller.Monitors.Count > 1 && GetLinkButtonRect(_currentWidthDip, footerTop).Contains(x, y))
        {
            _controller.SetLinkMonitors(!_controller.Settings.LinkMonitors);
            SyncUiState();
            return;
        }

        if (_controller.Monitors.Count == 0 && GetRefreshButtonRect(_currentWidthDip).Contains(x, y))
        {
            _refreshBtnPressed = true;
            NativeMethods.SetCapture(_hwnd);
            Invalidate();
        }
    }

    private async void OnLeftButtonUp(IntPtr lParam)
    {
        if (_draggingSliderIndex >= 0)
        {
            _draggingSliderIndex = -1;
            NativeMethods.ReleaseCapture();
            StartAnimTimerIfNeeded();
            Invalidate();
            return;
        }

        if (_refreshBtnPressed)
        {
            _refreshBtnPressed = false;
            NativeMethods.ReleaseCapture();
            var (x, y) = UnpackDipCoords(lParam);
            bool inside = _controller.Monitors.Count == 0 && GetRefreshButtonRect(_currentWidthDip).Contains(x, y);
            Invalidate();
            if (inside)
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
    }

    private void OnMouseWheel(IntPtr wParam, IntPtr lParam)
    {
        short delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
        if (delta == 0) return;

        var pt = new POINT
        {
            X = unchecked((short)(lParam.ToInt64() & 0xFFFF)),
            Y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF))
        };
        NativeMethods.ScreenToClient(_hwnd, ref pt);
        float scale = _dpi / 96f;
        float yDip = pt.Y / scale;

        int rowIdx = HitTestMonitorRow(yDip);
        if (rowIdx >= 0 && rowIdx < _controller.Monitors.Count)
        {
            _controller.StepMonitorBrightness(_controller.Monitors[rowIdx].Id, Math.Sign(delta));
            Invalidate();
        }
    }

    private void Invalidate()
    {
        if (_hwnd != IntPtr.Zero && _isVisible)
        {
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void BeginNativeVSyncSlide(bool opening, int xPx, int startYPx, int endYPx, double durationMs)
    {
        StopSlideAnimation();
        _slideAnimator.Start(
            _hwnd,
            opening,
            xPx,
            startYPx,
            endYPx,
            durationMs,
            onCompleted: opening
                ? (Action?)null
                : () =>
                {
                    _isHidingAnimated = false;
                    SetCloaked(false);
                    if (_hwnd != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
                    }
                    _isVisible = false;
                    FlyoutHidden?.Invoke(this, EventArgs.Empty);
                });
    }

    private void StopSlideAnimation()
    {
        _slideAnimator.Stop();
    }

    private void SetCloaked(bool cloaked)
    {
        _isCloaked = cloaked;
        if (_hwnd == IntPtr.Zero) return;
        int value = cloaked ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_CLOAK, ref value, sizeof(int));
    }

    private void PositionWindowNative(IntPtr insertAfter, int xPx, int yPx)
    {
        uint flags = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        if (insertAfter == IntPtr.Zero)
        {
            flags |= NativeMethods.SWP_NOZORDER;
        }
        NativeMethods.SetWindowPos(_hwnd, insertAfter, xPx, yPx, 0, 0, flags);
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
        double scale = TaskbarPlacementHelper.GetDpiScale(_hwnd != IntPtr.Zero ? _hwnd : _trayIconService.HostHwnd);
        int effectiveTopPx = ctx.VerticalBounds.EffectiveTopPx;
        int effectiveBottomPx = ctx.VerticalBounds.EffectiveBottomPx;
        bool isTaskbarAtTop = ctx.VerticalBounds.IsTaskbarAtTop;

        double workLeft = mi.rcWork.Left / scale;
        double workRight = mi.rcWork.Right / scale;
        double effectiveTop = effectiveTopPx / scale;
        double effectiveBottom = effectiveBottomPx / scale;

        double w = _currentWidthDip;
        double h = _currentHeightDip;
        const double margin = 12.0;

        double targetTop = isTaskbarAtTop
            ? effectiveTop + margin
            : effectiveBottom - h - margin;

        double targetLeft = Math.Max(workLeft + margin, workRight - w - margin);

        int targetYPx = (int)Math.Round(targetTop * scale);
        int slideDistancePx;

        if (isTaskbarAtTop)
        {
            IntPtr aboveMon = NativeMethods.MonitorFromPoint(
                new POINT { X = ctx.AnchorPx.X, Y = mi.rcMonitor.Top - 10 }, 0);
            slideDistancePx = aboveMon == IntPtr.Zero
                ? Math.Max(48, targetYPx + (int)Math.Round(h * scale) - effectiveTopPx)
                : Math.Max(48, effectiveTopPx - mi.rcMonitor.Top);
        }
        else
        {
            IntPtr belowMon = NativeMethods.MonitorFromPoint(
                new POINT { X = ctx.AnchorPx.X, Y = mi.rcMonitor.Bottom + 10 }, 0);
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

    private void ResubscribeMonitors()
    {
        foreach (var old in _subscribedMonitors)
        {
            old.PropertyChanged -= _monitorPropChangedHandler;
        }
        _subscribedMonitors.Clear();

        foreach (var mon in _controller.Monitors)
        {
            mon.PropertyChanged += _monitorPropChangedHandler;
            _subscribedMonitors.Add(mon);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _controller.MonitorsRefreshed -= _monitorsRefreshedHandler;
        foreach (var mon in _subscribedMonitors)
        {
            mon.PropertyChanged -= _monitorPropChangedHandler;
        }
        _subscribedMonitors.Clear();

        StopAnimTimer();
        StopSlideAnimation();
        _slideAnimator.Dispose();

        ReleaseRenderTarget();
        Direct2D.Release(ref _fmtIcon20);
        Direct2D.Release(ref _fmtIcon26);
        Direct2D.Release(ref _fmtIcon14);
        Direct2D.Release(ref _fmtName);
        Direct2D.Release(ref _fmtValue);
        Direct2D.Release(ref _fmtFooter);
        Direct2D.Release(ref _fmtEmptyTitle);
        Direct2D.Release(ref _fmtEmptySub);
        Direct2D.Release(ref _fmtButton);
        Direct2D.Release(ref _dwriteFactory);
        Direct2D.Release(ref _d2dFactory);

        if (_hwnd != IntPtr.Zero)
        {
            ActiveFlyouts.Remove(_hwnd);
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _isVisible = false;
    }
}
