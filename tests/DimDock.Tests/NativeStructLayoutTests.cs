using System.Runtime.InteropServices;
using DimDock.Interop;
using DimDock.Services;
using Xunit;

namespace DimDock.Tests;

public sealed class NativeStructLayoutTests
{
    [Fact]
    public void DisplayConfigStructs_MatchWin32BinarySizes()
    {
        Assert.Equal(8, Marshal.SizeOf<LUID>());
        Assert.Equal(20, Marshal.SizeOf<DISPLAYCONFIG_PATH_SOURCE_INFO>());
        Assert.Equal(48, Marshal.SizeOf<DISPLAYCONFIG_PATH_TARGET_INFO>());
        Assert.Equal(72, Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>());
        Assert.Equal(64, Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>());
        Assert.Equal(20, Marshal.SizeOf<DISPLAYCONFIG_DEVICE_INFO_HEADER>());
        Assert.Equal(84, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>());
        Assert.Equal(420, Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>());
    }

    [Theory]
    [InlineData(0u, 0u, 100u, 0)]
    [InlineData(50u, 0u, 100u, 50)]
    [InlineData(100u, 0u, 100u, 100)]
    [InlineData(128u, 0u, 255u, 50)]
    [InlineData(255u, 0u, 255u, 100)]
    [InlineData(50u, 10u, 90u, 50)]
    [InlineData(5u, 10u, 90u, 0)]
    [InlineData(120u, 10u, 90u, 100)]
    [InlineData(50u, 50u, 50u, 0)]
    public void RawToPercent_NormalizesAndClamps(uint current, uint min, uint max, int expected)
    {
        Assert.Equal(expected, DdcMonitorBackend.RawToPercent(current, min, max));
    }

    [Theory]
    [InlineData(0, 0u, 100u, 0u)]
    [InlineData(42, 0u, 100u, 42u)]
    [InlineData(100, 0u, 255u, 255u)]
    [InlineData(50, 0u, 255u, 128u)]
    [InlineData(50, 10u, 90u, 50u)]
    [InlineData(-20, 10u, 90u, 10u)]
    [InlineData(150, 10u, 90u, 90u)]
    public void PercentToRaw_ScalesAndClamps(int percent, uint min, uint max, uint expected)
    {
        Assert.Equal(expected, DdcMonitorBackend.PercentToRaw(percent, min, max));
    }
}
