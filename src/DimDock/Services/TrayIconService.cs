using System.Runtime.InteropServices;
using DimDock.Interop;
using DimDock.UI;

namespace DimDock.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint WmTrayCallback = NativeMethods.WM_APP + 1;

    private readonly Win32Dispatcher _dispatcher;
    private readonly Func<IntPtr, uint, IntPtr, IntPtr, (bool Handled, IntPtr Result)> _hook;
    private readonly uint _taskbarCreatedMessage;
    private readonly uint _activateInstanceMessage;
    private readonly Dictionary<int, IntPtr> _iconCache = [];
    private IntPtr _currentIcon;
    private int _currentBucket = 50;
    private int _currentIconSize;
    private bool _iconAdded;
    private bool _isDarkTaskbar;
    private string _tooltip = "Dim Dock";
    private bool _disposed;

    internal TrayIconService(Win32Dispatcher dispatcher, bool isDarkTaskbar, uint activateInstanceMessage)
    {
        _dispatcher = dispatcher;
        _isDarkTaskbar = isDarkTaskbar;
        _activateInstanceMessage = activateInstanceMessage;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

        _hook = WndProc;
        _dispatcher.AddMessageHook(_hook);

        _currentIconSize = GetTargetIconSize();
        _currentIcon = GetOrCreateIcon(_currentBucket);
        AddOrUpdateIcon(isAdd: true);
    }

    public IntPtr HostHwnd => _dispatcher.HostHwnd;

    public event EventHandler? LeftClicked;
    public event EventHandler? RightClicked;
    public event EventHandler? SystemThemeChanged;
    public event EventHandler? DisplayOrPowerChanged;
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
            hWnd = _dispatcher.HostHwnd,
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

        IntPtr primaryMonitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (TryGetMonitorDpi(primaryMonitor, out uint primaryDpi))
        {
            return primaryDpi;
        }

        if (hintDpi is > 0)
        {
            return hintDpi.Value;
        }

        if (_dispatcher.HostHwnd != IntPtr.Zero)
        {
            uint windowDpi = NativeMethods.GetDpiForWindow(_dispatcher.HostHwnd);
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
        hWnd = _dispatcher.HostHwnd,
        uID = TrayIconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private (bool Handled, IntPtr Result) WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTrayCallback)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            switch (evt)
            {
                case NativeMethods.NIN_SELECT:
                case NativeMethods.NIN_KEYSELECT:
                    LeftClicked?.Invoke(this, EventArgs.Empty);
                    return (true, IntPtr.Zero);
                case NativeMethods.WM_CONTEXTMENU:
                    RightClicked?.Invoke(this, EventArgs.Empty);
                    return (true, IntPtr.Zero);
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
            return (true, IntPtr.Zero);
        }
        else if (msg == NativeMethods.WM_DPICHANGED)
        {
            uint dpi = (uint)(wParam.ToInt64() & 0xFFFF);
            RefreshScale(dpi > 0 ? dpi : null);
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            RefreshScale();
            DisplayOrPowerChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (msg == NativeMethods.WM_POWERBROADCAST)
        {
            int powerEvent = wParam.ToInt32();
            if (powerEvent is NativeMethods.PBT_APMRESUMESUSPEND or NativeMethods.PBT_APMRESUMEAUTOMATIC)
            {
                DisplayOrPowerChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (msg == NativeMethods.WM_SETTINGCHANGE || msg == NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            SystemThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        return (false, IntPtr.Zero);
    }

    private static IntPtr CreateSunIcon(bool isDarkTaskbar, int brightnessPercent, int size)
    {
        byte[] pixels = RenderSunIconPixels(isDarkTaskbar, brightnessPercent, size);
        return CreateHIconFromBgra32(pixels, size, size);
    }

    internal static byte[] RenderSunIconPixels(bool isDarkTaskbar, int brightnessPercent, int size)
    {
        const double invSqrt2 = 0.7071067811865475244;

        double s = size / 16.0;
        double t = Math.Clamp(brightnessPercent, 0, 100) / 100.0;

        byte fg = isDarkTaskbar ? (byte)0xFF : (byte)0x1A;

        double center = size * 0.5;
        double ringRadius = (2.1 + (3.0 - 2.1) * t) * s;
        double rayInner = (4.1 + (5.0 - 4.1) * t) * s;
        double rayOuter = (6.7 + (8.0 - 6.7) * t) * s;
        double halfStroke = (1.45 * s) * 0.5;
        double rayMid = (rayInner + rayOuter) * 0.5;
        double rayHalfLen = (rayOuter - rayInner) * 0.5;

        int stride = size * 4;
        byte[] pixels = new byte[stride * size];

        for (int py = 0; py < size; py++)
        {
            double dy = (py + 0.5) - center;
            double absY = Math.Abs(dy);
            int rowOffset = py * stride;

            for (int px = 0; px < size; px++)
            {
                double dx = (px + 0.5) - center;
                double absX = Math.Abs(dx);

                double distFromCenter = Math.Sqrt(dx * dx + dy * dy);
                double d = Math.Abs(distFromCenter - ringRadius) - halfStroke;

                double u = absX >= absY ? absX : absY;
                double v = absX >= absY ? absY : absX;

                double dCardinal = BoxSdf(Math.Abs(u - rayMid) - rayHalfLen, v - halfStroke);
                if (dCardinal < d) d = dCardinal;

                double diagProj = (u + v) * invSqrt2;
                double diagPerp = (u - v) * invSqrt2;
                double dDiagonal = BoxSdf(Math.Abs(diagProj - rayMid) - rayHalfLen, diagPerp - halfStroke);
                if (dDiagonal < d) d = dDiagonal;

                double coverage = Math.Clamp(0.5 - d, 0.0, 1.0);
                if (coverage <= 0.0) continue;

                byte alpha = (byte)Math.Round(coverage * 255.0);
                if (alpha == 0) continue;

                int idx = rowOffset + (px * 4);
                pixels[idx] = fg;
                pixels[idx + 1] = fg;
                pixels[idx + 2] = fg;
                pixels[idx + 3] = alpha;
            }
        }

        return pixels;
    }

    private static double BoxSdf(double qx, double qy)
    {
        double ox = qx > 0.0 ? qx : 0.0;
        double oy = qy > 0.0 ? qy : 0.0;
        double inside = Math.Min(Math.Max(qx, qy), 0.0);
        return Math.Sqrt(ox * ox + oy * oy) + inside;
    }

    private static IntPtr CreateHIconFromBgra32(byte[] bgraPixels, int width, int height)
    {
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
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

        _dispatcher.RemoveMessageHook(_hook);
    }
}
