using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DimDock.Interop;
using Microsoft.Win32;

namespace DimDock.Services;

public sealed record ThemeSnapshot(
    bool IsAppDark,
    bool IsTaskbarDark,
    bool IsTransparencyEnabled,
    Color AccentColor,
    Color AccentHoverColor,
    Color AccentPressedColor);

internal readonly record struct FlyoutSurfacePalette(
    Color BodyTint,
    Color Divider,
    Color FooterTint);

public sealed class SystemThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private static readonly Lazy<Brush> NoiseBrush = new(CreateNoiseBrush);

    public ThemeSnapshot Current { get; private set; }

    public SystemThemeService()
    {
        Current = ReadSnapshot();
    }

    public ThemeSnapshot Refresh()
    {
        Current = ReadSnapshot();
        return Current;
    }

    public void ApplyToApplication(Application app, bool useDwmBackdrop = true)
    {
        var snapshot = Current;

        // Avoid setting app.ThemeMode so PresentationFramework.Fluent.dll and its full
        // control theme dictionaries are never loaded into memory; FluentOverrides.xaml
        // provides lightweight Windows 11 Quick Settings styles for all controls DimDock uses.
        var resources = app.Resources;
        resources["DimAccentBrush"] = CreateFrozenBrush(snapshot.AccentColor);
        resources["DimAccentHoverBrush"] = CreateFrozenBrush(snapshot.AccentHoverColor);
        resources["DimAccentPressedBrush"] = CreateFrozenBrush(snapshot.AccentPressedColor);
        resources["DimAcrylicNoiseBrush"] = NoiseBrush.Value;

        ApplyFlyoutSurfaceBrushes(app, useDwmBackdrop);

        if (snapshot.IsAppDark)
        {
            // WinUI 3 Dark primitives (TextFillColorPrimary/Secondary/Tertiary, ControlStrongFillColorDefault, ControlSolidFillColorDefault)
            resources["DimTextPrimaryBrush"] = CreateFrozenBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            resources["DimTextSecondaryBrush"] = CreateFrozenBrush(Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
            resources["DimTextMutedBrush"] = CreateFrozenBrush(Color.FromArgb(0x87, 0xFF, 0xFF, 0xFF));
            resources["DimButtonBgBrush"] = CreateFrozenBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
            resources["DimButtonHoverBrush"] = CreateFrozenBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            resources["DimButtonPressedBrush"] = CreateFrozenBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
            resources["DimButtonBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            resources["DimSliderTrackBrush"] = CreateFrozenBrush(Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF));
            resources["DimSliderThumbOuterBrush"] = CreateFrozenBrush(Color.FromRgb(0x45, 0x45, 0x45));
            resources["DimSliderThumbBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
            resources["DimScrollThumbBrush"] = CreateFrozenBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            resources["DimMenuPopupBgBrush"] = CreateFrozenBrush(Color.FromRgb(0x2C, 0x2C, 0x2C));
            resources["DimMenuPopupBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            resources["DimAccentForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0x0A, 0x0A, 0x0A));
        }
        else
        {
            // WinUI 3 Light primitives
            resources["DimTextPrimaryBrush"] = CreateFrozenBrush(Color.FromArgb(0xE4, 0x00, 0x00, 0x00));
            resources["DimTextSecondaryBrush"] = CreateFrozenBrush(Color.FromArgb(0x9E, 0x00, 0x00, 0x00));
            resources["DimTextMutedBrush"] = CreateFrozenBrush(Color.FromArgb(0x72, 0x00, 0x00, 0x00));
            resources["DimButtonBgBrush"] = CreateFrozenBrush(Color.FromArgb(0x08, 0x00, 0x00, 0x00));
            resources["DimButtonHoverBrush"] = CreateFrozenBrush(Color.FromArgb(0x0E, 0x00, 0x00, 0x00));
            resources["DimButtonPressedBrush"] = CreateFrozenBrush(Color.FromArgb(0x05, 0x00, 0x00, 0x00));
            resources["DimButtonBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x18, 0x00, 0x00, 0x00));
            resources["DimSliderTrackBrush"] = CreateFrozenBrush(Color.FromArgb(0x72, 0x00, 0x00, 0x00));
            resources["DimSliderThumbOuterBrush"] = CreateFrozenBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            resources["DimSliderThumbBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            resources["DimScrollThumbBrush"] = CreateFrozenBrush(Color.FromArgb(0x44, 0x00, 0x00, 0x00));
            resources["DimMenuPopupBgBrush"] = CreateFrozenBrush(Color.FromRgb(0xF9, 0xF9, 0xF9));
            resources["DimMenuPopupBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            resources["DimAccentForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        }
    }

    public void ApplyFlyoutSurfaceBrushes(Application app, bool useDwmBackdrop)
    {
        var palette = ComputeFlyoutPalette(Current.IsAppDark, useDwmBackdrop, Current.IsTransparencyEnabled);
        var resources = app.Resources;
        resources["DimFlyoutTintBrush"] = CreateFrozenBrush(palette.BodyTint);
        resources["DimDividerBrush"] = CreateFrozenBrush(palette.Divider);
        resources["DimFooterTintBrush"] = CreateFrozenBrush(palette.FooterTint);
    }

    internal static FlyoutSurfacePalette ComputeFlyoutPalette(bool isDark, bool useDwmBackdrop, bool isTransparencyEnabled = true)
    {
        if (useDwmBackdrop)
        {
            if (isDark)
            {
                // Match Windows 11 native Control Center (WinUI 3 AcrylicBackgroundFillColorDefault + Base + 2% noise):
                // Over DWMSBT_TRANSIENTWINDOW (~#555555 over white / ~#4E4E4E over dark), pure-black translucent veils
                // land on native Control Center's exact #323234 body, #28292B divider, and #2A2A2A footer over white
                // while preserving ~57% body / ~47% footer live blurred Acrylic pass-through.
                return isTransparencyEnabled
                    ? new FlyoutSurfacePalette(
                        BodyTint: Color.FromArgb(0x6F, 0x00, 0x00, 0x00),
                        Divider: Color.FromArgb(0x8A, 0x00, 0x00, 0x00),
                        FooterTint: Color.FromArgb(0x88, 0x00, 0x00, 0x00))
                    : new FlyoutSurfacePalette(
                        BodyTint: Colors.Transparent,
                        Divider: Color.FromArgb(0x36, 0x00, 0x00, 0x00),
                        FooterTint: Color.FromArgb(0x45, 0x00, 0x00, 0x00));
            }

            return isTransparencyEnabled
                ? new FlyoutSurfacePalette(
                    BodyTint: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
                    Divider: Color.FromArgb(0x14, 0x00, 0x00, 0x00),
                    FooterTint: Color.FromArgb(0x0E, 0x00, 0x00, 0x00))
                : new FlyoutSurfacePalette(
                    BodyTint: Colors.Transparent,
                    Divider: Color.FromArgb(0x14, 0x00, 0x00, 0x00),
                    FooterTint: Color.FromArgb(0x0E, 0x00, 0x00, 0x00));
        }

        // Solid WinUI 3 Acrylic fallback colors (#2C2C2C / #202020 dark, #F9F9F9 / #EEEEEE light).
        return isDark
            ? new FlyoutSurfacePalette(
                BodyTint: Color.FromRgb(0x2C, 0x2C, 0x2C),
                Divider: Color.FromRgb(0x1D, 0x1D, 0x1D),
                FooterTint: Color.FromRgb(0x20, 0x20, 0x20))
            : new FlyoutSurfacePalette(
                BodyTint: Color.FromRgb(0xF9, 0xF9, 0xF9),
                Divider: Color.FromArgb(0x14, 0x00, 0x00, 0x00),
                FooterTint: Color.FromRgb(0xEE, 0xEE, 0xEE));
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static Brush CreateNoiseBrush()
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri("pack://application:,,,/Assets/NoiseAsset_256x256.png", UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                Opacity = 0.02
            };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Failed to load Acrylic noise asset; continuing without noise overlay", ex);
            return Brushes.Transparent;
        }
    }

    private static ThemeSnapshot ReadSnapshot()
    {
        bool isAppDark = true;
        bool isTaskbarDark = true;
        bool isTransparencyEnabled = true;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            if (key?.GetValue("AppsUseLightTheme") is int appsLight)
            {
                isAppDark = appsLight == 0;
            }
            if (key?.GetValue("SystemUsesLightTheme") is int sysLight)
            {
                isTaskbarDark = sysLight == 0;
            }
            if (key?.GetValue("EnableTransparency") is int enableTransparency)
            {
                isTransparencyEnabled = enableTransparency != 0;
            }
        }
        catch (Exception ex)
        {
            // Default to Windows 11 dark appearance.
            AppLogger.Warn("Failed to read Windows theme personalization registry keys", ex);
        }

        var (accent, hover, pressed) = ReadAccentColors(isAppDark);
        return new ThemeSnapshot(isAppDark, isTaskbarDark, isTransparencyEnabled, accent, hover, pressed);
    }

    private static (Color Accent, Color Hover, Color Pressed) ReadAccentColors(bool isDark)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AccentKey, writable: false);
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 28)
            {
                // AccentPalette layout: [0]=Light3, [1]=Light2, [2]=Light1, [3]=Base, [4]=Dark1, [5]=Dark2, [6]=Dark3
                int mainIdx = isDark ? 1 : 4;
                int hoverIdx = isDark ? 0 : 5;
                int pressedIdx = isDark ? 2 : 6;

                Color mainColor = Color.FromRgb(
                    palette[mainIdx * 4],
                    palette[mainIdx * 4 + 1],
                    palette[mainIdx * 4 + 2]);
                Color hoverColor = Color.FromRgb(
                    palette[hoverIdx * 4],
                    palette[hoverIdx * 4 + 1],
                    palette[hoverIdx * 4 + 2]);
                Color pressedColor = Color.FromRgb(
                    palette[pressedIdx * 4],
                    palette[pressedIdx * 4 + 1],
                    palette[pressedIdx * 4 + 2]);

                return (mainColor, hoverColor, pressedColor);
            }
        }
        catch (Exception ex)
        {
            // Fall through to DWM colorization.
            AppLogger.Warn("Failed to read AccentPalette from registry", ex);
        }

        try
        {
            if (NativeMethods.DwmGetColorizationColor(out uint argb, out _) == 0)
            {
                byte r = (byte)((argb >> 16) & 0xFF);
                byte g = (byte)((argb >> 8) & 0xFF);
                byte b = (byte)(argb & 0xFF);
                var baseColor = Color.FromRgb(r, g, b);
                return (baseColor, baseColor, baseColor);
            }
        }
        catch
        {
            // Fall through to default Windows 11 Fluent blue.
        }

        return isDark
            ? (Color.FromRgb(0x4C, 0xC2, 0xFF), Color.FromRgb(0x6C, 0xD0, 0xFF), Color.FromRgb(0x37, 0xA8, 0xE6))
            : (Color.FromRgb(0x00, 0x5F, 0xB8), Color.FromRgb(0x00, 0x4E, 0x98), Color.FromRgb(0x00, 0x3E, 0x7A));
    }
}
