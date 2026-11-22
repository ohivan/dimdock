using System.Runtime.InteropServices;
using DimDock.Models;

namespace DimDock.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_COLOR_F
{
    public float R, G, B, A;

    public static D2D1_COLOR_F FromArgb(ArgbColor c) => new()
    {
        R = c.R / 255f,
        G = c.G / 255f,
        B = c.B / 255f,
        A = c.A / 255f
    };

    public static D2D1_COLOR_F Transparent => default;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_POINT_2F
{
    public float X, Y;

    public D2D1_POINT_2F(float x, float y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_SIZE_F
{
    public float Width, Height;

    public D2D1_SIZE_F(float width, float height)
    {
        Width = width;
        Height = height;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_SIZE_U
{
    public uint Width, Height;

    public D2D1_SIZE_U(uint width, uint height)
    {
        Width = width;
        Height = height;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_RECT_F
{
    public float Left, Top, Right, Bottom;

    public D2D1_RECT_F(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public bool Contains(float x, float y) =>
        x >= Left && x <= Right && y >= Top && y <= Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_ROUNDED_RECT
{
    public D2D1_RECT_F Rect;
    public float RadiusX;
    public float RadiusY;

    public D2D1_ROUNDED_RECT(D2D1_RECT_F rect, float radius)
    {
        Rect = rect;
        RadiusX = radius;
        RadiusY = radius;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_ELLIPSE
{
    public D2D1_POINT_2F Point;
    public float RadiusX;
    public float RadiusY;

    public D2D1_ELLIPSE(float cx, float cy, float radius)
    {
        Point = new D2D1_POINT_2F(cx, cy);
        RadiusX = radius;
        RadiusY = radius;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_ARC_SEGMENT
{
    public D2D1_POINT_2F Point;
    public D2D1_SIZE_F Size;
    public float RotationAngle;
    public uint SweepDirection; // 0 = CCW, 1 = CW
    public uint ArcSize;        // 0 = Small, 1 = Large
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_PIXEL_FORMAT
{
    public uint Format;    // DXGI_FORMAT_B8G8R8A8_UNORM = 87
    public uint AlphaMode; // D2D1_ALPHA_MODE_PREMULTIPLIED = 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_RENDER_TARGET_PROPERTIES
{
    public uint Type; // 0 = DEFAULT
    public D2D1_PIXEL_FORMAT PixelFormat;
    public float DpiX;
    public float DpiY;
    public uint Usage;
    public uint MinLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_HWND_RENDER_TARGET_PROPERTIES
{
    public IntPtr Hwnd;
    public D2D1_SIZE_U PixelSize;
    public uint PresentOptions; // 0 = NONE, 2 = IMMEDIATELY
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_BITMAP_PROPERTIES
{
    public D2D1_PIXEL_FORMAT PixelFormat;
    public float DpiX;
    public float DpiY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_BITMAP_BRUSH_PROPERTIES
{
    public uint ExtendModeX; // 1 = WRAP
    public uint ExtendModeY; // 1 = WRAP
    public uint InterpolationMode; // 0 = NEAREST_NEIGHBOR, 1 = LINEAR
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_MATRIX_3X2_F
{
    public float M11, M12, M21, M22, M31, M32;

    public static D2D1_MATRIX_3X2_F Identity => new() { M11 = 1f, M22 = 1f };
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_BRUSH_PROPERTIES
{
    public float Opacity;
    public D2D1_MATRIX_3X2_F Transform;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_STROKE_STYLE_PROPERTIES
{
    public uint StartCap;   // 2 = ROUND
    public uint EndCap;     // 2 = ROUND
    public uint DashCap;    // 2 = ROUND
    public uint LineJoin;   // 0 = MITER, 2 = ROUND
    public float MiterLimit;
    public uint DashStyle;  // 0 = SOLID
    public float DashOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWRITE_TRIMMING
{
    public uint Granularity; // 1 = CHARACTER, 2 = WORD
    public uint Delimiter;
    public uint DelimiterCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWRITE_TEXT_METRICS
{
    public float Left;
    public float Top;
    public float Width;
    public float WidthIncludingTrailingWhitespace;
    public float Height;
    public float LayoutWidth;
    public float LayoutHeight;
    public uint MaxBidiReorderingDepth;
    public uint LineCount;
}

/// <summary>
/// Minimal, zero-dependency Direct2D + DirectWrite COM interop wrapper.
/// 100% NativeAOT-compatible using unmanaged Stdcall function pointers.
/// </summary>
internal static unsafe class Direct2D
{
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const uint D2D1_ALPHA_MODE_PREMULTIPLIED = 1;
    public const uint D2D1_EXTEND_MODE_WRAP = 1;
    public const uint D2D1_CAP_STYLE_ROUND = 2;
    public const uint D2D1_SWEEP_DIRECTION_CLOCKWISE = 1;
    public const uint D2D1_ARC_SIZE_SMALL = 0;
    public const uint D2D1_ARC_SIZE_LARGE = 1;
    public const uint D2D1_FIGURE_BEGIN_HOLLOW = 1;
    public const uint D2D1_FIGURE_END_OPEN = 0;
    public const uint D2D1_ANTIALIAS_MODE_PER_PRIMITIVE = 0;
    public const uint D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE = 2;
    public const uint D2D1_DRAW_TEXT_OPTIONS_CLIP = 0x00000002;
    public const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    public const uint DWRITE_FONT_WEIGHT_NORMAL = 400;
    public const uint DWRITE_FONT_WEIGHT_SEMIBOLD = 600;
    public const uint DWRITE_FONT_STYLE_NORMAL = 0;
    public const uint DWRITE_FONT_STRETCH_NORMAL = 5;
    public const uint DWRITE_TEXT_ALIGNMENT_LEADING = 0;
    public const uint DWRITE_TEXT_ALIGNMENT_TRAILING = 1;
    public const uint DWRITE_TEXT_ALIGNMENT_CENTER = 2;
    public const uint DWRITE_PARAGRAPH_ALIGNMENT_NEAR = 0;
    public const uint DWRITE_PARAGRAPH_ALIGNMENT_FAR = 1;
    public const uint DWRITE_PARAGRAPH_ALIGNMENT_CENTER = 2;
    public const uint DWRITE_WORD_WRAPPING_WRAP = 0;
    public const uint DWRITE_WORD_WRAPPING_NO_WRAP = 1;
    public const uint DWRITE_TRIMMING_GRANULARITY_CHARACTER = 1;

    private static readonly Guid IID_ID2D1Factory = new("06152247-6f50-465a-9245-118bfd3b6007");
    private static readonly Guid IID_IDWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(
        uint factoryType,
        ref Guid riid,
        IntPtr pFactoryOptions,
        out IntPtr ppIFactory);

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(
        uint factoryType,
        ref Guid iid,
        out IntPtr factory);

    public static void Release(ref IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            void** vtbl = *(void***)ptr;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2])(ptr);
            ptr = IntPtr.Zero;
        }
    }

    public static void Release(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            void** vtbl = *(void***)ptr;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2])(ptr);
        }
    }

    public static IntPtr CreateD2DFactory()
    {
        var iid = IID_ID2D1Factory;
        int hr = D2D1CreateFactory(0 /* SINGLE_THREADED */, ref iid, IntPtr.Zero, out IntPtr factory);
        return hr >= 0 ? factory : IntPtr.Zero;
    }

    public static IntPtr CreateDWriteFactory()
    {
        var iid = IID_IDWriteFactory;
        int hr = DWriteCreateFactory(0 /* SHARED */, ref iid, out IntPtr factory);
        return hr >= 0 ? factory : IntPtr.Zero;
    }

    public static IntPtr CreateHwndRenderTarget(IntPtr factory, IntPtr hwnd, uint pixelWidth, uint pixelHeight, float dpi)
    {
        var rtProps = new D2D1_RENDER_TARGET_PROPERTIES
        {
            Type = 0,
            PixelFormat = new D2D1_PIXEL_FORMAT
            {
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                AlphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED
            },
            DpiX = dpi,
            DpiY = dpi
        };

        var hwndProps = new D2D1_HWND_RENDER_TARGET_PROPERTIES
        {
            Hwnd = hwnd,
            PixelSize = new D2D1_SIZE_U(Math.Max(1u, pixelWidth), Math.Max(1u, pixelHeight)),
            PresentOptions = 0
        };

        IntPtr rt = IntPtr.Zero;
        void** vtbl = *(void***)factory;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_RENDER_TARGET_PROPERTIES*, D2D1_HWND_RENDER_TARGET_PROPERTIES*, IntPtr*, int>)vtbl[14])(
            factory, &rtProps, &hwndProps, &rt);

        if (hr < 0 || rt == IntPtr.Zero) return IntPtr.Zero;

        // Grayscale antialiasing renders crisply over translucent DWM Acrylic without color fringing.
        SetTextAntialiasMode(rt, D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        return rt;
    }

    public static IntPtr CreatePathGeometry(IntPtr factory)
    {
        IntPtr geom = IntPtr.Zero;
        void** vtbl = *(void***)factory;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[10])(factory, &geom);
        return hr >= 0 ? geom : IntPtr.Zero;
    }

    public static IntPtr CreateRoundStrokeStyle(IntPtr factory)
    {
        var props = new D2D1_STROKE_STYLE_PROPERTIES
        {
            StartCap = D2D1_CAP_STYLE_ROUND,
            EndCap = D2D1_CAP_STYLE_ROUND,
            DashCap = D2D1_CAP_STYLE_ROUND,
            LineJoin = 2,
            MiterLimit = 10f,
            DashStyle = 0,
            DashOffset = 0f
        };
        IntPtr style = IntPtr.Zero;
        void** vtbl = *(void***)factory;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_STROKE_STYLE_PROPERTIES*, float*, uint, IntPtr*, int>)vtbl[11])(
            factory, &props, null, 0, &style);
        return hr >= 0 ? style : IntPtr.Zero;
    }

    public static IntPtr CreateSolidColorBrush(IntPtr rt, ArgbColor color)
    {
        var c = D2D1_COLOR_F.FromArgb(color);
        IntPtr brush = IntPtr.Zero;
        void** vtbl = *(void***)rt;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_COLOR_F*, D2D1_BRUSH_PROPERTIES*, IntPtr*, int>)vtbl[8])(
            rt, &c, null, &brush);
        return hr >= 0 ? brush : IntPtr.Zero;
    }

    public static void SetSolidColorBrushColor(IntPtr brush, ArgbColor color)
    {
        var c = D2D1_COLOR_F.FromArgb(color);
        void** vtbl = *(void***)brush;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_COLOR_F*, void>)vtbl[8])(brush, &c);
    }

    public static IntPtr CreateNoiseBitmapBrush(IntPtr rt, float dpi)
    {
        // Generate a deterministic 128x128 monochrome Acrylic grain tile (matching WinUI 3's 2% noise overlay)
        // directly in memory so we never load windowscodecs.dll (WIC) at runtime.
        const int size = 128;
        byte[] pixels = new byte[size * size * 4];
        uint state = 0x5A17C3E1u;
        for (int i = 0; i < size * size; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            byte v = (byte)(state & 0xFF);
            int idx = i * 4;
            pixels[idx] = v;
            pixels[idx + 1] = v;
            pixels[idx + 2] = v;
            pixels[idx + 3] = 0xFF;
        }

        var bmpProps = new D2D1_BITMAP_PROPERTIES
        {
            PixelFormat = new D2D1_PIXEL_FORMAT
            {
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                AlphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED
            },
            DpiX = dpi,
            DpiY = dpi
        };

        IntPtr bitmap = IntPtr.Zero;
        fixed (byte* pData = pixels)
        {
            void** vtbl = *(void***)rt;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_SIZE_U, void*, uint, D2D1_BITMAP_PROPERTIES*, IntPtr*, int>)vtbl[4])(
                rt, new D2D1_SIZE_U(size, size), pData, size * 4u, &bmpProps, &bitmap);
            if (hr < 0 || bitmap == IntPtr.Zero) return IntPtr.Zero;
        }

        try
        {
            var bbProps = new D2D1_BITMAP_BRUSH_PROPERTIES
            {
                ExtendModeX = D2D1_EXTEND_MODE_WRAP,
                ExtendModeY = D2D1_EXTEND_MODE_WRAP,
                InterpolationMode = 0
            };
            var bProps = new D2D1_BRUSH_PROPERTIES
            {
                Opacity = 0.02f,
                Transform = D2D1_MATRIX_3X2_F.Identity
            };

            IntPtr brush = IntPtr.Zero;
            void** vtbl = *(void***)rt;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, D2D1_BITMAP_BRUSH_PROPERTIES*, D2D1_BRUSH_PROPERTIES*, IntPtr*, int>)vtbl[7])(
                rt, bitmap, &bbProps, &bProps, &brush);
            return hr >= 0 ? brush : IntPtr.Zero;
        }
        finally
        {
            Release(bitmap);
        }
    }

    public static void BeginDraw(IntPtr rt)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)vtbl[48])(rt);
    }

    public static int EndDraw(IntPtr rt)
    {
        void** vtbl = *(void***)rt;
        return ((delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*, int>)vtbl[49])(rt, null, null);
    }

    public static void Clear(IntPtr rt, ArgbColor color)
    {
        var c = D2D1_COLOR_F.FromArgb(color);
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_COLOR_F*, void>)vtbl[47])(rt, &c);
    }

    public static void SetDpi(IntPtr rt, float dpiX, float dpiY)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, float, float, void>)vtbl[51])(rt, dpiX, dpiY);
    }

    public static int Resize(IntPtr rt, uint pixelWidth, uint pixelHeight)
    {
        var size = new D2D1_SIZE_U(Math.Max(1u, pixelWidth), Math.Max(1u, pixelHeight));
        void** vtbl = *(void***)rt;
        return ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_SIZE_U*, int>)vtbl[58])(rt, &size);
    }

    public static void SetTextAntialiasMode(IntPtr rt, uint mode)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)vtbl[34])(rt, mode);
    }

    public static void FillRectangle(IntPtr rt, D2D1_RECT_F rect, IntPtr brush)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_RECT_F*, IntPtr, void>)vtbl[17])(rt, &rect, brush);
    }

    public static void FillRoundedRectangle(IntPtr rt, D2D1_ROUNDED_RECT rect, IntPtr brush)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_ROUNDED_RECT*, IntPtr, void>)vtbl[19])(rt, &rect, brush);
    }

    public static void DrawRoundedRectangle(IntPtr rt, D2D1_ROUNDED_RECT rect, IntPtr brush, float strokeWidth)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_ROUNDED_RECT*, IntPtr, float, IntPtr, void>)vtbl[18])(rt, &rect, brush, strokeWidth, IntPtr.Zero);
    }

    public static void FillEllipse(IntPtr rt, D2D1_ELLIPSE ellipse, IntPtr brush)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_ELLIPSE*, IntPtr, void>)vtbl[21])(rt, &ellipse, brush);
    }

    public static void DrawEllipse(IntPtr rt, D2D1_ELLIPSE ellipse, IntPtr brush, float strokeWidth)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_ELLIPSE*, IntPtr, float, IntPtr, void>)vtbl[20])(rt, &ellipse, brush, strokeWidth, IntPtr.Zero);
    }

    public static void DrawGeometry(IntPtr rt, IntPtr geometry, IntPtr brush, float strokeWidth, IntPtr strokeStyle)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, float, IntPtr, void>)vtbl[22])(rt, geometry, brush, strokeWidth, strokeStyle);
    }

    public static void PushAxisAlignedClip(IntPtr rt, D2D1_RECT_F rect)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_RECT_F*, uint, void>)vtbl[45])(rt, &rect, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    }

    public static void PopAxisAlignedClip(IntPtr rt)
    {
        void** vtbl = *(void***)rt;
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)vtbl[46])(rt);
    }

    public static void DrawText(IntPtr rt, string text, IntPtr textFormat, D2D1_RECT_F layoutRect, IntPtr brush, uint options = D2D1_DRAW_TEXT_OPTIONS_CLIP)
    {
        if (string.IsNullOrEmpty(text) || textFormat == IntPtr.Zero || brush == IntPtr.Zero) return;
        fixed (char* pText = text)
        {
            void** vtbl = *(void***)rt;
            ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, D2D1_RECT_F*, IntPtr, uint, uint, void>)vtbl[27])(
                rt, pText, (uint)text.Length, textFormat, &layoutRect, brush, options, 0 /* NATURAL */);
        }
    }

    public static IntPtr OpenPathGeometry(IntPtr pathGeometry)
    {
        IntPtr sink = IntPtr.Zero;
        void** vtbl = *(void***)pathGeometry;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[17])(pathGeometry, &sink);
        return hr >= 0 ? sink : IntPtr.Zero;
    }

    public static void SinkBeginFigure(IntPtr sink, D2D1_POINT_2F startPoint)
    {
        void** vtbl = *(void***)sink;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_POINT_2F, uint, void>)vtbl[5])(sink, startPoint, D2D1_FIGURE_BEGIN_HOLLOW);
    }

    public static void SinkAddArc(IntPtr sink, D2D1_ARC_SEGMENT arc)
    {
        void** vtbl = *(void***)sink;
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_ARC_SEGMENT*, void>)vtbl[14])(sink, &arc);
    }

    public static void SinkEndFigure(IntPtr sink)
    {
        void** vtbl = *(void***)sink;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)vtbl[8])(sink, D2D1_FIGURE_END_OPEN);
    }

    public static void SinkClose(IntPtr sink)
    {
        void** vtbl = *(void***)sink;
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[9])(sink);
    }

    public static IntPtr CreateTextFormat(
        IntPtr dwriteFactory,
        string fontFamily,
        float fontSize,
        uint weight = DWRITE_FONT_WEIGHT_NORMAL,
        uint textAlignment = DWRITE_TEXT_ALIGNMENT_LEADING,
        uint paragraphAlignment = DWRITE_PARAGRAPH_ALIGNMENT_CENTER,
        bool noWrap = true,
        bool ellipsisTrimming = false)
    {
        IntPtr format = IntPtr.Zero;
        fixed (char* pFamily = fontFamily)
        fixed (char* pLocale = "en-US")
        {
            void** vtbl = *(void***)dwriteFactory;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, uint, uint, uint, float, char*, IntPtr*, int>)vtbl[15])(
                dwriteFactory, pFamily, IntPtr.Zero, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, fontSize, pLocale, &format);
            if (hr < 0 || format == IntPtr.Zero) return IntPtr.Zero;
        }

        void** fmtVtbl = *(void***)format;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)fmtVtbl[3])(format, textAlignment);
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)fmtVtbl[4])(format, paragraphAlignment);
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)fmtVtbl[5])(format, noWrap ? DWRITE_WORD_WRAPPING_NO_WRAP : DWRITE_WORD_WRAPPING_WRAP);

        if (ellipsisTrimming)
        {
            IntPtr sign = IntPtr.Zero;
            void** dwVtbl = *(void***)dwriteFactory;
            int hrSign = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)dwVtbl[20])(dwriteFactory, format, &sign);
            if (hrSign >= 0 && sign != IntPtr.Zero)
            {
                var trimming = new DWRITE_TRIMMING { Granularity = DWRITE_TRIMMING_GRANULARITY_CHARACTER };
                ((delegate* unmanaged[Stdcall]<IntPtr, DWRITE_TRIMMING*, IntPtr, int>)fmtVtbl[9])(format, &trimming, sign);
                Release(sign);
            }
        }

        return format;
    }

    public static float MeasureTextWidth(IntPtr dwriteFactory, string text, IntPtr textFormat, float maxWidth = 1000f)
    {
        if (string.IsNullOrEmpty(text) || dwriteFactory == IntPtr.Zero || textFormat == IntPtr.Zero) return 0f;

        IntPtr layout = IntPtr.Zero;
        fixed (char* pText = text)
        {
            void** dwVtbl = *(void***)dwriteFactory;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, float, float, IntPtr*, int>)dwVtbl[18])(
                dwriteFactory, pText, (uint)text.Length, textFormat, maxWidth, 100f, &layout);
            if (hr < 0 || layout == IntPtr.Zero) return 0f;
        }

        try
        {
            DWRITE_TEXT_METRICS metrics = default;
            void** layoutVtbl = *(void***)layout;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, DWRITE_TEXT_METRICS*, int>)layoutVtbl[60])(layout, &metrics);
            return hr >= 0 ? metrics.WidthIncludingTrailingWhitespace : 0f;
        }
        finally
        {
            Release(layout);
        }
    }
}
