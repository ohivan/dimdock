using DimDock.Interop;
using DimDock.Models;
using Microsoft.Win32;

namespace DimDock.Services;

public sealed record ThemeSnapshot(
    bool IsAppDark,
    bool IsTaskbarDark,
    bool IsTransparencyEnabled,
    ArgbColor AccentColor,
    ArgbColor AccentHoverColor,
    ArgbColor AccentPressedColor);

internal readonly record struct FlyoutSurfacePalette(
    ArgbColor BodyTint,
    ArgbColor Divider,
    ArgbColor FooterTint);

internal readonly record struct ThemePalette(
    bool IsDark,
    ArgbColor Accent,
    ArgbColor AccentHover,
    ArgbColor AccentPressed,
    ArgbColor AccentForeground,
    ArgbColor TextPrimary,
    ArgbColor TextSecondary,
    ArgbColor TextMuted,
    ArgbColor ButtonBg,
    ArgbColor ButtonHover,
    ArgbColor ButtonPressed,
    ArgbColor ButtonBorder,
    ArgbColor SliderTrack,
    ArgbColor SliderThumbOuter,
    ArgbColor SliderThumbBorder,
    ArgbColor ScrollThumb,
    ArgbColor MenuPopupBg,
    ArgbColor MenuPopupBorder,
    ArgbColor BodyTint,
    ArgbColor Divider,
    ArgbColor FooterTint);

public sealed class SystemThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

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

    internal ThemePalette BuildPalette(bool useDwmBackdrop = true)
    {
        var snapshot = Current;
        var surface = ComputeFlyoutPalette(snapshot.IsAppDark, useDwmBackdrop, snapshot.IsTransparencyEnabled);

        if (snapshot.IsAppDark)
        {
            return new ThemePalette(
                IsDark: true,
                Accent: snapshot.AccentColor,
                AccentHover: snapshot.AccentHoverColor,
                AccentPressed: snapshot.AccentPressedColor,
                AccentForeground: ArgbColor.FromRgb(0x0A, 0x0A, 0x0A),
                TextPrimary: ArgbColor.FromRgb(0xFF, 0xFF, 0xFF),
                TextSecondary: ArgbColor.FromArgb(0xC5, 0xFF, 0xFF, 0xFF),
                TextMuted: ArgbColor.FromArgb(0x87, 0xFF, 0xFF, 0xFF),
                ButtonBg: ArgbColor.FromArgb(0x0F, 0xFF, 0xFF, 0xFF),
                ButtonHover: ArgbColor.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                ButtonPressed: ArgbColor.FromArgb(0x0A, 0xFF, 0xFF, 0xFF),
                ButtonBorder: ArgbColor.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
                SliderTrack: ArgbColor.FromArgb(0x8B, 0xFF, 0xFF, 0xFF),
                SliderThumbOuter: ArgbColor.FromRgb(0x45, 0x45, 0x45),
                SliderThumbBorder: ArgbColor.FromArgb(0x12, 0xFF, 0xFF, 0xFF),
                ScrollThumb: ArgbColor.FromArgb(0x55, 0xFF, 0xFF, 0xFF),
                MenuPopupBg: ArgbColor.FromRgb(0x2C, 0x2C, 0x2C),
                MenuPopupBorder: ArgbColor.FromArgb(0x20, 0xFF, 0xFF, 0xFF),
                BodyTint: surface.BodyTint,
                Divider: surface.Divider,
                FooterTint: surface.FooterTint);
        }

        return new ThemePalette(
            IsDark: false,
            Accent: snapshot.AccentColor,
            AccentHover: snapshot.AccentHoverColor,
            AccentPressed: snapshot.AccentPressedColor,
            AccentForeground: ArgbColor.FromRgb(0xFF, 0xFF, 0xFF),
            TextPrimary: ArgbColor.FromArgb(0xE4, 0x00, 0x00, 0x00),
            TextSecondary: ArgbColor.FromArgb(0x9E, 0x00, 0x00, 0x00),
            TextMuted: ArgbColor.FromArgb(0x72, 0x00, 0x00, 0x00),
            ButtonBg: ArgbColor.FromArgb(0x08, 0x00, 0x00, 0x00),
            ButtonHover: ArgbColor.FromArgb(0x0E, 0x00, 0x00, 0x00),
            ButtonPressed: ArgbColor.FromArgb(0x05, 0x00, 0x00, 0x00),
            ButtonBorder: ArgbColor.FromArgb(0x18, 0x00, 0x00, 0x00),
            SliderTrack: ArgbColor.FromArgb(0x72, 0x00, 0x00, 0x00),
            SliderThumbOuter: ArgbColor.FromRgb(0xFF, 0xFF, 0xFF),
            SliderThumbBorder: ArgbColor.FromArgb(0x14, 0x00, 0x00, 0x00),
            ScrollThumb: ArgbColor.FromArgb(0x44, 0x00, 0x00, 0x00),
            MenuPopupBg: ArgbColor.FromRgb(0xF9, 0xF9, 0xF9),
            MenuPopupBorder: ArgbColor.FromArgb(0x1A, 0x00, 0x00, 0x00),
            BodyTint: surface.BodyTint,
            Divider: surface.Divider,
            FooterTint: surface.FooterTint);
    }

    internal static FlyoutSurfacePalette ComputeFlyoutPalette(bool isDark, bool useDwmBackdrop, bool isTransparencyEnabled = true)
    {
        if (useDwmBackdrop)
        {
            if (isDark)
            {
                return isTransparencyEnabled
                    ? new FlyoutSurfacePalette(
                        BodyTint: ArgbColor.FromArgb(0x52, 0x00, 0x00, 0x00),
                        Divider: ArgbColor.FromArgb(0x70, 0x00, 0x00, 0x00),
                        FooterTint: ArgbColor.FromArgb(0x72, 0x00, 0x00, 0x00))
                    : new FlyoutSurfacePalette(
                        BodyTint: ArgbColor.Transparent,
                        Divider: ArgbColor.FromArgb(0x36, 0x00, 0x00, 0x00),
                        FooterTint: ArgbColor.FromArgb(0x45, 0x00, 0x00, 0x00));
            }

            return isTransparencyEnabled
                ? new FlyoutSurfacePalette(
                    BodyTint: ArgbColor.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
                    Divider: ArgbColor.FromArgb(0x14, 0x00, 0x00, 0x00),
                    FooterTint: ArgbColor.FromArgb(0x0E, 0x00, 0x00, 0x00))
                : new FlyoutSurfacePalette(
                    BodyTint: ArgbColor.Transparent,
                    Divider: ArgbColor.FromArgb(0x14, 0x00, 0x00, 0x00),
                    FooterTint: ArgbColor.FromArgb(0x0E, 0x00, 0x00, 0x00));
        }

        return isDark
            ? new FlyoutSurfacePalette(
                BodyTint: ArgbColor.FromRgb(0x2C, 0x2C, 0x2C),
                Divider: ArgbColor.FromRgb(0x1D, 0x1D, 0x1D),
                FooterTint: ArgbColor.FromRgb(0x20, 0x20, 0x20))
            : new FlyoutSurfacePalette(
                BodyTint: ArgbColor.FromRgb(0xF9, 0xF9, 0xF9),
                Divider: ArgbColor.FromArgb(0x14, 0x00, 0x00, 0x00),
                FooterTint: ArgbColor.FromRgb(0xEE, 0xEE, 0xEE));
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
            AppLogger.Warn("Failed to read Windows theme personalization registry keys", ex);
        }

        var (accent, hover, pressed) = ReadAccentColors(isAppDark);
        return new ThemeSnapshot(isAppDark, isTaskbarDark, isTransparencyEnabled, accent, hover, pressed);
    }

    private static (ArgbColor Accent, ArgbColor Hover, ArgbColor Pressed) ReadAccentColors(bool isDark)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AccentKey, writable: false);
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 28)
            {
                int mainIdx = isDark ? 1 : 4;
                int hoverIdx = isDark ? 0 : 5;
                int pressedIdx = isDark ? 2 : 6;

                ArgbColor mainColor = ArgbColor.FromRgb(
                    palette[mainIdx * 4],
                    palette[mainIdx * 4 + 1],
                    palette[mainIdx * 4 + 2]);
                ArgbColor hoverColor = ArgbColor.FromRgb(
                    palette[hoverIdx * 4],
                    palette[hoverIdx * 4 + 1],
                    palette[hoverIdx * 4 + 2]);
                ArgbColor pressedColor = ArgbColor.FromRgb(
                    palette[pressedIdx * 4],
                    palette[pressedIdx * 4 + 1],
                    palette[pressedIdx * 4 + 2]);

                return (mainColor, hoverColor, pressedColor);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Failed to read AccentPalette from registry", ex);
        }

        try
        {
            if (NativeMethods.DwmGetColorizationColor(out uint argb, out _) == 0)
            {
                byte r = (byte)((argb >> 16) & 0xFF);
                byte g = (byte)((argb >> 8) & 0xFF);
                byte b = (byte)(argb & 0xFF);
                var baseColor = ArgbColor.FromRgb(r, g, b);
                return (baseColor, baseColor, baseColor);
            }
        }
        catch
        {
            // Fall through to default Windows 11 Fluent blue.
        }

        return isDark
            ? (ArgbColor.FromRgb(0x4C, 0xC2, 0xFF), ArgbColor.FromRgb(0x6C, 0xD0, 0xFF), ArgbColor.FromRgb(0x37, 0xA8, 0xE6))
            : (ArgbColor.FromRgb(0x00, 0x5F, 0xB8), ArgbColor.FromRgb(0x00, 0x4E, 0x98), ArgbColor.FromRgb(0x00, 0x3E, 0x7A));
    }
}
