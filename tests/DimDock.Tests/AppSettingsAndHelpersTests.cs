using System.IO;
using DimDock.Models;
using DimDock.Services;
using Xunit;

namespace DimDock.Tests;

public sealed class AppSettingsAndHelpersTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalLogPath;

    public AppSettingsAndHelpersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DimDockHelpers_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalLogPath = AppLogger.LogPath;
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(5, 5)]
    [InlineData(6, 10)]
    [InlineData(50, 10)]
    public void AppSettingsNormalize_QuantizesScrollStep(int input, int expected)
    {
        var settings = new AppSettings { ScrollStep = input, TrayScrollTarget = "  " }.Normalize();

        Assert.Equal(expected, settings.ScrollStep);
        Assert.Equal(AppSettings.AllMonitorsTarget, settings.TrayScrollTarget);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    [InlineData(47, 45)]
    [InlineData(48, 50)]
    [InlineData(98, 100)]
    [InlineData(120, 100)]
    public void TrayIconQuantizeBucket_RoundsToNearest5AndClamps(int percent, int expectedBucket)
    {
        Assert.Equal(expectedBucket, TrayIconService.QuantizeBucket(percent));
    }

    [Theory]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("shell_secondarytraywnd", true)]
    [InlineData("NotifyIconOverflowWindow", true)]
    [InlineData("TopLevelWindowForOverflowXamlIsland", true)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("", false)]
    public void TrayScrollHook_IsTaskbarClassName_MatchesExpectedWindowClasses(string className, bool expected)
    {
        Assert.Equal(expected, TrayScrollHookService.IsTaskbarClassName(className.AsSpan()));
    }

    [Fact]
    public void AppLogger_WritesFormattedLineAndRotatesOversizedLog()
    {
        string logPath = Path.Combine(_tempDir, "dimdock.log");
        AppLogger.ConfigurePathForTests(logPath);

        AppLogger.Warn("Test warning", new InvalidOperationException("boom"));

        string text = File.ReadAllText(logPath);
        Assert.Contains("[WARN] Test warning :: InvalidOperationException: boom", text);

        // Exceed 256 KB rotation threshold and verify rollover to .old
        File.WriteAllBytes(logPath, new byte[260 * 1024]);
        AppLogger.Info("After rotation");

        Assert.True(File.Exists(logPath + ".old"));
        Assert.Contains("[INFO] After rotation", File.ReadAllText(logPath));
    }

    public void Dispose()
    {
        AppLogger.ConfigurePathForTests(_originalLogPath);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
