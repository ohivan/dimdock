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
    private IntPtr _hIcon;
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

        _hIcon = CreateSunIcon(_isDarkTaskbar);
        AddOrUpdateIcon(isAdd: true);
    }

    public IntPtr HostHwnd => _messageWindow.Handle;

    public event EventHandler? LeftClicked;
    public event EventHandler? RightClicked;
    public event EventHandler? SystemThemeChanged;
    public event EventHandler? ExternalActivateRequested;

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (!_iconAdded) return;

        var nid = CreateBaseData();
        nid.uFlags = NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;
        nid.szTip = _tooltip;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
    }

    public void UpdateTheme(bool isDarkTaskbar)
    {
        if (_disposed || _isDarkTaskbar == isDarkTaskbar) return;
        _isDarkTaskbar = isDarkTaskbar;

        IntPtr newIcon = CreateSunIcon(_isDarkTaskbar);
        IntPtr oldIcon = _hIcon;
        _hIcon = newIcon;

        if (_iconAdded)
        {
            var nid = CreateBaseData();
            nid.uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_SHOWTIP;
            nid.hIcon = _hIcon;
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
        }

        if (oldIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(oldIcon);
        }
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

    private void AddOrUpdateIcon(bool isAdd)
    {
        var nid = CreateBaseData();
        nid.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;
        nid.uCallbackMessage = WmTrayCallback;
        nid.hIcon = _hIcon;
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
            AddOrUpdateIcon(isAdd: true);
        }
        else if (msg == _activateInstanceMessage && _activateInstanceMessage != 0)
        {
            ExternalActivateRequested?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        else if (msg == NativeMethods.WM_SETTINGCHANGE || msg == NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            SystemThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private static IntPtr CreateSunIcon(bool isDarkTaskbar)
    {
        const int size = 32;
        Color fg = isDarkTaskbar
            ? Color.FromRgb(0xF5, 0xF5, 0xF5)
            : Color.FromRgb(0x1E, 0x1E, 0x1E);

        var pen = new Pen(new SolidColorBrush(fg), 2.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            const double center = size / 2.0;
            const double ringRadius = 5.4;
            const double rayInner = 9.4;
            const double rayOuter = 13.0;

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

        int stride = size * 4;
        byte[] pixels = new byte[stride * size];
        rtb.CopyPixels(pixels, stride, 0);

        return CreateHIconFromBgra32(pixels, size, size);
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
        IntPtr hMaskBitmap = NativeMethods.CreateBitmap(width, height, 1, 1, IntPtr.Zero);

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

        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
    }
}
