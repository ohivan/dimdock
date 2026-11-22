using System.Collections.Concurrent;
using System.IO;
using DimDock.Models;
using DimDock.Services;
using Xunit;

namespace DimDock.Tests;

public sealed class BrightnessControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public BrightnessControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DimDockTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    [Fact]
    public async Task StepTrayBrightness_AllMonitorsUnlinked_PreservesRelativeOffsetsAndClamps()
    {
        var backend = new FakeMonitorBackend(
            new MonitorInfo("m1", "LG HDR 4K", @"\\.\DISPLAY1", true, 42, 0, 100, false),
            new MonitorInfo("m2", "XB270HU", @"\\.\DISPLAY2", false, 96, 0, 100, false),
            new MonitorInfo("m3", "DELL U2415", @"\\.\DISPLAY3", false, 39, 0, 100, false));

        using var controller = new BrightnessController(backend, new SettingsService(_settingsPath), TimeSpan.Zero);
        await controller.RefreshMonitorsAsync();

        int? summary = controller.StepTrayBrightness(+1); // default +5%
        await controller.FlushPendingWritesAsync();

        Assert.Equal(47, controller.Monitors[0].Brightness);
        Assert.Equal(100, controller.Monitors[1].Brightness);
        Assert.Equal(44, controller.Monitors[2].Brightness);
        Assert.Equal(64, summary); // round((47+100+44)/3 = 63.67)
        Assert.Equal(
            $"LG HDR 4K: 47%{Environment.NewLine}XB270HU: 100%{Environment.NewLine}DELL U2415: 44%",
            controller.FormatTrayTooltip());

        Assert.Equal(47, backend.LastWritten["m1"]);
        Assert.Equal(100, backend.LastWritten["m2"]);
        Assert.Equal(44, backend.LastWritten["m3"]);
    }

    [Fact]
    public async Task SetMonitorBrightness_WhenLinked_SyncsAllMonitors()
    {
        var backend = new FakeMonitorBackend(
            new MonitorInfo("m1", "LG HDR 4K", @"\\.\DISPLAY1", true, 20, 0, 100, false),
            new MonitorInfo("m2", "DELL U2415", @"\\.\DISPLAY2", false, 80, 0, 100, false));

        using var controller = new BrightnessController(backend, new SettingsService(_settingsPath), TimeSpan.Zero);
        await controller.RefreshMonitorsAsync();
        controller.SetLinkMonitors(true);

        controller.SetMonitorBrightness("m1", 65);
        await controller.FlushPendingWritesAsync();

        Assert.Equal(65, controller.Monitors[0].Brightness);
        Assert.Equal(65, controller.Monitors[1].Brightness);
        Assert.Equal(65, backend.LastWritten["m1"]);
        Assert.Equal(65, backend.LastWritten["m2"]);
    }

    [Fact]
    public async Task StepTrayBrightness_SpecificTarget_OnlyAdjustsSelectedMonitor()
    {
        var backend = new FakeMonitorBackend(
            new MonitorInfo("m1", "LG HDR 4K", @"\\.\DISPLAY1", true, 40, 0, 100, false),
            new MonitorInfo("m2", "DELL U2415", @"\\.\DISPLAY2", false, 70, 0, 100, false));

        using var controller = new BrightnessController(backend, new SettingsService(_settingsPath), TimeSpan.Zero);
        await controller.RefreshMonitorsAsync();
        controller.SetTrayScrollTarget("m2");

        int? summary = controller.StepTrayBrightness(-2); // -10%
        await controller.FlushPendingWritesAsync();

        Assert.Equal(40, controller.Monitors[0].Brightness);
        Assert.Equal(60, controller.Monitors[1].Brightness);
        Assert.Equal(60, summary);
        Assert.Equal("DELL U2415: 60%", controller.FormatTrayTooltip());
        Assert.False(backend.LastWritten.ContainsKey("m1"));
        Assert.Equal(60, backend.LastWritten["m2"]);
    }

    [Fact]
    public async Task RapidSliderBurst_CoalescesWritesAndAppliesFinalValue()
    {
        var backend = new FakeMonitorBackend(
            new MonitorInfo("m1", "MAG 271QPX E2", @"\\.\DISPLAY1", true, 50, 0, 100, false))
        {
            SimulatedWriteDelayMs = 15
        };

        using var controller = new BrightnessController(backend, new SettingsService(_settingsPath), TimeSpan.FromMilliseconds(15));
        await controller.RefreshMonitorsAsync();

        for (int v = 51; v <= 90; v++)
        {
            controller.SetMonitorBrightness("m1", v);
        }

        await controller.FlushPendingWritesAsync();

        Assert.Equal(90, controller.Monitors[0].Brightness);
        Assert.Equal(90, backend.LastWritten["m1"]);
        // Coalescing ensures far fewer hardware writes than the 40 UI ticks.
        Assert.True(backend.TotalWrites < 20, $"Expected coalesced writes, got {backend.TotalWrites}");
    }

    [Fact]
    public async Task TransientDdcFailure_RetriesOnceAndSucceeds()
    {
        var backend = new FakeMonitorBackend(
            new MonitorInfo("m1", "MAG 271QPX E2", @"\\.\DISPLAY1", true, 50, 0, 100, false))
        {
            FailNextWrites = 1
        };

        using var controller = new BrightnessController(backend, new SettingsService(_settingsPath), TimeSpan.Zero);
        await controller.RefreshMonitorsAsync();

        controller.SetMonitorBrightness("m1", 75);
        await controller.FlushPendingWritesAsync();

        Assert.Equal(75, backend.LastWritten["m1"]);
        Assert.Equal(2, backend.TotalWrites);
    }

    [Fact]
    public async Task ConcurrentWritesDuringRefreshAndDispose_DoNotThrow()
    {
        var backend = new FakeMonitorBackend(
            new MonitorInfo("m1", "LG HDR 4K", @"\\.\DISPLAY1", true, 50, 0, 100, false),
            new MonitorInfo("m2", "DELL U2415", @"\\.\DISPLAY2", false, 50, 0, 100, false))
        {
            SimulatedWriteDelayMs = 2
        };

        var controller = new BrightnessController(backend, new SettingsService(_settingsPath), TimeSpan.FromMilliseconds(1));
        await controller.RefreshMonitorsAsync();

        using var cts = new CancellationTokenSource();
        var writerTask = Task.Run(() =>
        {
            int val = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                controller.SetMonitorBrightness("m1", val % 101);
                controller.SetMonitorBrightness("m2", (val + 50) % 101);
                val++;
            }
        });

        for (int i = 0; i < 5; i++)
        {
            await controller.RefreshMonitorsAsync();
            await Task.Delay(5);
        }

        controller.Dispose();
        cts.Cancel();
        await writerTask;
    }

    [Fact]
    public async Task StepTrayBrightness_WhenLinked_SyncsAroundAverage()
    {
        var backend = new FakeMonitorBackend(
            new MonitorInfo("m1", "LG HDR 4K", @"\\.\DISPLAY1", true, 30, 0, 100, false),
            new MonitorInfo("m2", "DELL U2415", @"\\.\DISPLAY2", false, 70, 0, 100, false));

        using var controller = new BrightnessController(backend, new SettingsService(_settingsPath), TimeSpan.Zero);
        await controller.RefreshMonitorsAsync();
        controller.SetLinkMonitors(true);
        controller.SetScrollStep(10);

        int? summary = controller.StepTrayBrightness(+1); // average 50 + 10 = 60
        await controller.FlushPendingWritesAsync();

        Assert.Equal(60, summary);
        Assert.Equal(60, controller.Monitors[0].Brightness);
        Assert.Equal(60, controller.Monitors[1].Brightness);
        Assert.Equal(60, backend.LastWritten["m1"]);
        Assert.Equal(60, backend.LastWritten["m2"]);
    }

    [Fact]
    public async Task StepMonitorBrightness_StepsSingleMonitorByConfiguredStep()
    {
        var backend = new FakeMonitorBackend(
            new MonitorInfo("m1", "LG HDR 4K", @"\\.\DISPLAY1", true, 98, 0, 100, false),
            new MonitorInfo("m2", "DELL U2415", @"\\.\DISPLAY2", false, 40, 0, 100, false));

        using var controller = new BrightnessController(backend, new SettingsService(_settingsPath), TimeSpan.Zero);
        await controller.RefreshMonitorsAsync();
        controller.SetScrollStep(5);

        controller.StepMonitorBrightness("m1", +1);
        await controller.FlushPendingWritesAsync();

        Assert.Equal(100, controller.Monitors[0].Brightness);
        Assert.Equal(40, controller.Monitors[1].Brightness);
        Assert.Equal(100, backend.LastWritten["m1"]);
        Assert.False(backend.LastWritten.ContainsKey("m2"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class FakeMonitorBackend : IMonitorBackend
    {
        private readonly List<MonitorInfo> _monitors;
        private int _totalWrites;

        public FakeMonitorBackend(params MonitorInfo[] monitors)
        {
            _monitors = [.. monitors];
        }

        public ConcurrentDictionary<string, int> LastWritten { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int SimulatedWriteDelayMs { get; set; }
        public int FailNextWrites { get; set; }
        public int TotalWrites => Volatile.Read(ref _totalWrites);

        public IReadOnlyList<MonitorInfo> EnumerateMonitors() => _monitors;

        public bool SetBrightness(string monitorId, int brightnessPercent)
        {
            Interlocked.Increment(ref _totalWrites);

            if (SimulatedWriteDelayMs > 0)
            {
                Thread.Sleep(SimulatedWriteDelayMs);
            }

            if (FailNextWrites > 0)
            {
                FailNextWrites--;
                return false;
            }

            LastWritten[monitorId] = brightnessPercent;
            return true;
        }

        public void Dispose() { }
    }
}
