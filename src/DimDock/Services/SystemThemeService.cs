using System.Windows;
using System.Windows.Media;
using DimDock.Interop;
using Microsoft.Win32;

namespace DimDock.Services;

public sealed record ThemeSnapshot(
    bool IsAppDark,
    bool IsTaskbarDark,
    Color AccentColor,
    Color AccentHoverColor,
    Color AccentPressedColor);

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

    public void ApplyToApplication(Application app)
    {
        var snapshot = Current;

#pragma warning disable WPF0001 // ThemeMode is stable in .NET 10 WPF
        app.ThemeMode = snapshot.IsAppDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var resources = app.Resources;
        resources["DimAccentBrush"] = new SolidColorBrush(snapshot.AccentColor);
        resources["DimAccentHoverBrush"] = new SolidColorBrush(snapshot.AccentHoverColor);
        resources["DimAccentPressedBrush"] = new SolidColorBrush(snapshot.AccentPressedColor);

        if (snapshot.IsAppDark)
        {
            resources["DimTextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
            resources["DimTextSecondaryBrush"] = new SolidColorBrush(Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
            resources["DimTextMutedBrush"] = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            resources["DimSliderTrackBrush"] = new SolidColorBrush(Color.FromArgb(0x75, 0xFF, 0xFF, 0xFF));
            resources["DimThumbOuterBrush"] = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45));
            resources["DimThumbBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            resources["DimDividerBrush"] = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            // Match Windows 11 Quick Settings / Notification Center deep charcoal Acrylic (~#242424 body, ~#1C1C1C footer)
            resources["DimFlyoutTintBrush"] = new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E));
            resources["DimFooterTintBrush"] = new SolidColorBrush(Color.FromArgb(0x70, 0x12, 0x12, 0x12));
            resources["DimFlyoutBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            resources["DimButtonHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            resources["DimTextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
            resources["DimTextSecondaryBrush"] = new SolidColorBrush(Color.FromArgb(0xB8, 0x00, 0x00, 0x00));
            resources["DimTextMutedBrush"] = new SolidColorBrush(Color.FromArgb(0x88, 0x00, 0x00, 0x00));
            resources["DimSliderTrackBrush"] = new SolidColorBrush(Color.FromArgb(0x4D, 0x00, 0x00, 0x00));
            resources["DimThumbOuterBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            resources["DimThumbBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
            resources["DimDividerBrush"] = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            resources["DimFlyoutTintBrush"] = new SolidColorBrush(Color.FromArgb(0xCC, 0xF3, 0xF3, 0xF3));
            resources["DimFooterTintBrush"] = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            resources["DimFlyoutBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            resources["DimButtonHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x0E, 0x00, 0x00, 0x00));
        }
    }

    private static ThemeSnapshot ReadSnapshot()
    {
        bool isAppDark = true;
        bool isTaskbarDark = true;

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
        }
        catch
        {
            // Default to Windows 11 dark appearance.
        }

        var (accent, hover, pressed) = ReadAccentColors(isAppDark);
        return new ThemeSnapshot(isAppDark, isTaskbarDark, accent, hover, pressed);
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
        catch
        {
            // Fall through to DWM colorization.
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
