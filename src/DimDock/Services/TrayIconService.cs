using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DimDock.Interop;

namespace DimDock.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const int WmTrayCallback = NativeMethods.WM_APP + 1;

    private readonly HwndSource _messageWindow;
    private readonly uint _taskbarCreatedMessage;
    private readonly uint _activateInstanceMessage;
    private readonly Dictionary<int, IntPtr> _iconCache = [];
    private IntPtr _currentIcon;
    private int _currentBucket = 50;
    private int _currentIconSize;
    private bool _iconAdded;
    private bool _isDarkTaskbar;
    private string _tooltip = "DimDock";
    private bool _disposed;

    public TrayIconService(bool isDarkTaskbar, uint activateInstanceMessage)
    {
        _isDarkTaskbar = isDarkTaskbar;
        _activateInstanceMessage = activateInstanceMessage;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

        var parameters = new HwndSourceParameters("DimDock_TrayHost")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP (hidden top-level window so broadcast msgs arrive)
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);

        _currentIconSize = GetTargetIconSize();
        _currentIcon = GetOrCreateIcon(_currentBucket);
        AddOrUpdateIcon(isAdd: true);
    }

    public IntPtr HostHwnd => _messageWindow.Handle;

    public event EventHandler? LeftClicked;
    public event EventHandler? RightClicked;
    public event EventHandler? SystemThemeChanged;
    public event EventHandler? ExternalActivateRequested;

    public void UpdateStatus(string tooltip, int? brightnessPercent)
    {
        if (_disposed) return;

        string trimmed = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        bool tipChanged = !string.Equals(_tooltip, trimmed, StringComparison.Ordinal);
        _tooltip = trimmed;

        int nextBucket = brightnessPercent.HasValue
            ? QuantizeBucket(brightnessPercent.Value)
            : _currentBucket;
        bool iconChanged = nextBucket != _currentBucket;

        if (iconChanged)
        {
            _currentBucket = nextBucket;
            _currentIcon = GetOrCreateIcon(_currentBucket);
        }

        if (!_iconAdded || (!tipChanged && !iconChanged)) return;

        var nid = CreateBaseData();
        nid.uFlags = NativeMethods.NIF_SHOWTIP;

        if (tipChanged)
        {
            nid.uFlags |= NativeMethods.NIF_TIP;
            nid.szTip = _tooltip;
        }

        if (iconChanged)
        {
            nid.uFlags |= NativeMethods.NIF_ICON;
            nid.hIcon = _currentIcon;
        }

        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
    }

    public void UpdateTheme(bool isDarkTaskbar)
    {
        if (_disposed) return;
        RebuildIconsIfNeeded(isDarkTaskbar, pushToShell: true);
    }

    public void RefreshScale(uint? hintDpi = null)
    {
        if (_disposed) return;
        RebuildIconsIfNeeded(_isDarkTaskbar, pushToShell: true, hintDpi);
    }

    private bool RebuildIconsIfNeeded(bool isDarkTaskbar, bool pushToShell, uint? hintDpi = null)
    {
        int nextIconSize = GetTargetIconSize(hintDpi);
        if (_isDarkTaskbar == isDarkTaskbar && _currentIconSize == nextIconSize) return false;

        _isDarkTaskbar = isDarkTaskbar;
        _currentIconSize = nextIconSize;

        // Build the new current icon first, swap it into the tray, then free old cached HICONs.
        var oldIcons = _iconCache.Values.ToList();
        _iconCache.Clear();

        _currentIcon = GetOrCreateIcon(_currentBucket);

        if (pushToShell && _iconAdded)
        {
            var nid = CreateBaseData();
            nid.uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_SHOWTIP;
            nid.hIcon = _currentIcon;
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
        }

        foreach (IntPtr oldIcon in oldIcons)
        {
            if (oldIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(oldIcon);
            }
        }

        return true;
    }

    internal bool TryGetIconRect(out RECT rect)
    {
        rect = default;
        if (_disposed || !_iconAdded) return false;

        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _messageWindow.Handle,
            uID = TrayIconId
        };

        return NativeMethods.Shell_NotifyIconGetRect(ref identifier, out rect) == 0 &&
               rect.Width > 0 && rect.Height > 0;
    }

    internal static int QuantizeBucket(int percent) =>
        Math.Clamp((int)Math.Round(percent / 5.0, MidpointRounding.AwayFromZero) * 5, 0, 100);

    internal static int ComputeTargetIconSize(uint dpi, int metricSizeForDpi)
    {
        int resolved = metricSizeForDpi > 0
            ? metricSizeForDpi
            : (int)Math.Round(16.0 * (dpi > 0 ? dpi : 96u) / 96.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(resolved, 16, 128);
    }

    private int GetTargetIconSize(uint? hintDpi = null)
    {
        uint dpi = GetTaskbarDpi(hintDpi);
        int metricSize = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        return ComputeTargetIconSize(dpi, metricSize);
    }

    private uint GetTaskbarDpi(uint? hintDpi = null)
    {
        // 1. Prefer the live effective DPI of the monitor actually hosting our tray icon.
        if (_iconAdded && TryGetIconRect(out RECT iconRect))
        {
            var center = new POINT
            {
                X = (iconRect.Left + iconRect.Right) / 2,
                Y = (iconRect.Top + iconRect.Bottom) / 2
            };
            IntPtr hMonitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (TryGetMonitorDpi(hMonitor, out uint iconMonitorDpi))
            {
                return iconMonitorDpi;
            }
        }

        // 2. Otherwise query the primary taskbar's monitor or window DPI directly.
        IntPtr taskbarHwnd = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (taskbarHwnd != IntPtr.Zero)
        {
            IntPtr taskbarMonitor = NativeMethods.MonitorFromWindow(taskbarHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (TryGetMonitorDpi(taskbarMonitor, out uint taskbarMonitorDpi))
            {
                return taskbarMonitorDpi;
            }

            uint taskbarWindowDpi = NativeMethods.GetDpiForWindow(taskbarHwnd);
            if (taskbarWindowDpi > 0)
            {
                return taskbarWindowDpi;
            }
        }

        // 3. Fall back to the primary monitor's live effective DPI.
        IntPtr primaryMonitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (TryGetMonitorDpi(primaryMonitor, out uint primaryDpi))
        {
            return primaryDpi;
        }

        // 4. Use WM_DPICHANGED hint, message window DPI, or system DPI as final fallbacks.
        if (hintDpi is > 0)
        {
            return hintDpi.Value;
        }

        if (_messageWindow.Handle != IntPtr.Zero)
        {
            uint windowDpi = NativeMethods.GetDpiForWindow(_messageWindow.Handle);
            if (windowDpi > 0)
            {
                return windowDpi;
            }
        }

        uint systemDpi = NativeMethods.GetDpiForSystem();
        return systemDpi > 0 ? systemDpi : 96;
    }

    private static bool TryGetMonitorDpi(IntPtr hMonitor, out uint dpi)
    {
        dpi = 0;
        if (hMonitor == IntPtr.Zero) return false;

        if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
            dpiX > 0)
        {
            dpi = dpiX;
            return true;
        }

        return false;
    }

    private IntPtr GetOrCreateIcon(int bucket)
    {
        if (_iconCache.TryGetValue(bucket, out IntPtr existing) && existing != IntPtr.Zero)
        {
            return existing;
        }

        IntPtr created = CreateSunIcon(_isDarkTaskbar, bucket, _currentIconSize);
        _iconCache[bucket] = created;
        return created;
    }

    private void AddOrUpdateIcon(bool isAdd)
    {
        var nid = CreateBaseData();
        nid.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;
        nid.uCallbackMessage = WmTrayCallback;
        nid.hIcon = _currentIcon;
        nid.szTip = _tooltip;

        if (NativeMethods.Shell_NotifyIconW(isAdd ? NativeMethods.NIM_ADD : NativeMethods.NIM_MODIFY, ref nid))
        {
            _iconAdded = true;
            if (isAdd)
            {
                var versionData = CreateBaseData();
                versionData.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
                NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref versionData);
            }
        }
    }

    private NOTIFYICONDATAW CreateBaseData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _messageWindow.Handle,
        uID = TrayIconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayCallback)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            switch (evt)
            {
                case NativeMethods.NIN_SELECT:
                case NativeMethods.NIN_KEYSELECT:
                    LeftClicked?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case NativeMethods.WM_CONTEXTMENU:
                    RightClicked?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
            }
        }
        else if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _iconAdded = false;
            RebuildIconsIfNeeded(_isDarkTaskbar, pushToShell: false);
            AddOrUpdateIcon(isAdd: true);
        }
        else if (msg == _activateInstanceMessage && _activateInstanceMessage != 0)
        {
            ExternalActivateRequested?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        else if (msg == NativeMethods.WM_DPICHANGED)
        {
            uint dpi = (uint)(wParam.ToInt64() & 0xFFFF);
            RefreshScale(dpi > 0 ? dpi : null);
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            RefreshScale();
        }
        else if (msg == NativeMethods.WM_SETTINGCHANGE || msg == NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            SystemThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private static IntPtr CreateSunIcon(bool isDarkTaskbar, int brightnessPercent, int size)
    {
        byte[] pixels = RenderSunIconPixels(isDarkTaskbar, brightnessPercent, size);
        return CreateHIconFromBgra32(pixels, size, size);
    }

    internal static byte[] RenderSunIconPixels(bool isDarkTaskbar, int brightnessPercent, int size)
    {
        // Render directly at the live taskbar small-icon pixel size (16px @ 100%, 20px @ 125%,
        // 24px @ 150%, 32px @ 200%, 36px @ 225%, 48px @ 300%, ...) so Windows renders it 1:1
        // without upscaling blur or downscaling softness.
        double s = size / 16.0;
        double t = Math.Clamp(brightnessPercent, 0, 100) / 100.0;

        Color fg = isDarkTaskbar
            ? Color.FromRgb(0xFF, 0xFF, 0xFF)
            : Color.FromRgb(0x1A, 0x1A, 0x1A);

        var brush = new SolidColorBrush(fg);
        brush.Freeze();

        var pen = new Pen(brush, 1.45 * s)
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat
        };
        pen.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double center = size / 2.0;
            // Scale both the sun core and rays smoothly so rays always remain crisp visible lines
            // (~2.6-3.0px long even at 0%) while the full sun expands from ~13.5px (0%) to edge-to-edge 16px (100%).
            double ringRadius = (2.1 + (3.0 - 2.1) * t) * s;
            double rayInner = (4.1 + (5.0 - 4.1) * t) * s;
            double rayOuter = (6.7 + (8.0 - 6.7) * t) * s;

            dc.DrawEllipse(null, pen, new Point(center, center), ringRadius, ringRadius);

            for (int i = 0; i < 8; i++)
            {
                double angle = i * (Math.PI / 4.0);
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);

                var p1 = new Point(center + cos * rayInner, center + sin * rayInner);
                var p2 = new Point(center + cos * rayOuter, center + sin * rayOuter);
                dc.DrawLine(pen, p1, p2);
            }
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        // Windows 11's taskbar extracts 32bpp HICONs via WIC as straight (non-premultiplied) BGRA32.
        // Converting from Pbgra32 avoids double-premultiplication dimming on anti-aliased edges.
        var straightBgra = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);

        int stride = size * 4;
        byte[] pixels = new byte[stride * size];
        straightBgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static IntPtr CreateHIconFromBgra32(byte[] bgraPixels, int width, int height)
    {
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB
            }
        };

        IntPtr hColorBitmap = NativeMethods.CreateDIBSection(
            IntPtr.Zero,
            ref bmi,
            NativeMethods.DIB_RGB_COLORS,
            out IntPtr bitsPtr,
            IntPtr.Zero,
            0);

        if (hColorBitmap == IntPtr.Zero || bitsPtr == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Marshal.Copy(bgraPixels, 0, bitsPtr, bgraPixels.Length);
        int maskStride = ((width + 15) / 16) * 2;
        byte[] maskBits = new byte[maskStride * height];
        IntPtr hMaskBitmap = NativeMethods.CreateBitmap(width, height, 1, 1, maskBits);

        try
        {
            var iconInfo = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmColor = hColorBitmap,
                hbmMask = hMaskBitmap
            };

            return NativeMethods.CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            NativeMethods.DeleteObject(hColorBitmap);
            if (hMaskBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hMaskBitmap);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_iconAdded)
        {
            var nid = CreateBaseData();
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid);
            _iconAdded = false;
        }

        foreach (IntPtr icon in _iconCache.Values)
        {
            if (icon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(icon);
            }
        }
        _iconCache.Clear();
        _currentIcon = IntPtr.Zero;

        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
    }
}
