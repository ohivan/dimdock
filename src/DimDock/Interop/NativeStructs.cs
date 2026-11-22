using System.Runtime.InteropServices;

namespace DimDock.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public bool Contains(POINT pt) =>
        pt.X >= Left && pt.X <= Right && pt.Y >= Top && pt.Y <= Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MARGINS
{
    public int cxLeftWidth;
    public int cxRightWidth;
    public int cyTopHeight;
    public int cyBottomHeight;

    public MARGINS(int all)
    {
        cxLeftWidth = all;
        cxRightWidth = all;
        cyTopHeight = all;
        cyBottomHeight = all;
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PHYSICAL_MONITOR
{
    public IntPtr hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szPhysicalMonitorDescription;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;
    public int targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    [FieldOffset(0)] public uint infoType;
    [FieldOffset(4)] public uint id;
    [FieldOffset(8)] public LUID adapterId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public uint type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public fixed char viewGdiDeviceName[32];

    public string GetDeviceName()
    {
        fixed (char* ptr = viewGdiDeviceName)
        {
            return FixedBufferString.Read(ptr, 32);
        }
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;
    public fixed char monitorFriendlyDeviceName[64];
    public fixed char monitorDevicePath[128];

    public string GetFriendlyName()
    {
        fixed (char* ptr = monitorFriendlyDeviceName)
        {
            return FixedBufferString.Read(ptr, 64);
        }
    }

    public string GetDevicePath()
    {
        fixed (char* ptr = monitorDevicePath)
        {
            return FixedBufferString.Read(ptr, 128);
        }
    }
}

internal static unsafe class FixedBufferString
{
    public static string Read(char* buffer, int maxLength)
    {
        var span = new ReadOnlySpan<char>(buffer, maxLength);
        int nullIndex = span.IndexOf('\0');
        return nullIndex >= 0 ? new string(span[..nullIndex]) : new string(span);
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NOTIFYICONDATAW
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szTip;
    public uint dwState;
    public uint dwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szInfo;
    public uint uTimeoutOrVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string szInfoTitle;
    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NOTIFYICONIDENTIFIER
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID;
    public Guid guidItem;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    [MarshalAs(UnmanagedType.Bool)]
    public bool fIcon;
    public int xHotspot;
    public int yHotspot;
    public IntPtr hbmMask;
    public IntPtr hbmColor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    public uint bmiColors;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpszMenuName;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PAINTSTRUCT
{
    public IntPtr hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    public fixed byte rgbReserved[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct TRACKMOUSEEVENT
{
    public uint cbSize;
    public uint dwFlags;
    public IntPtr hwndTrack;
    public uint dwHoverTime;
}
