using System.IO;
using DimDock.Models;
using DimDock.Services;
using Xunit;

namespace DimDock.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DimDockSettings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void MissingFile_ReturnsDefaultSettings()
    {
        string path = Path.Combine(_tempDir, "missing.json");
        var service = new SettingsService(path);

        Assert.Equal(5, service.Current.ScrollStep);
        Assert.False(service.Current.LinkMonitors);
        Assert.Equal(AppSettings.AllMonitorsTarget, service.Current.TrayScrollTarget);
    }

    [Fact]
    public void SaveAndReload_RoundTripsSettings()
    {
        string path = Path.Combine(_tempDir, "settings.json");
        var writer = new SettingsService(path);
        writer.Current.ScrollStep = 10;
        writer.Current.LinkMonitors = true;
        writer.Current.TrayScrollTarget = @"\\?\DISPLAY#MSI1234";
        writer.Save();

        var reader = new SettingsService(path);
        Assert.Equal(10, reader.Current.ScrollStep);
        Assert.True(reader.Current.LinkMonitors);
        Assert.Equal(@"\\?\DISPLAY#MSI1234", reader.Current.TrayScrollTarget);
    }

    [Fact]
    public void CorruptedJson_RecoversDefaultsCleanly()
    {
        string path = Path.Combine(_tempDir, "corrupt.json");
        File.WriteAllText(path, "{ not valid json !!!");

        var service = new SettingsService(path);
        Assert.Equal(5, service.Current.ScrollStep);
        Assert.False(service.Current.LinkMonitors);
        Assert.Equal(AppSettings.AllMonitorsTarget, service.Current.TrayScrollTarget);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
