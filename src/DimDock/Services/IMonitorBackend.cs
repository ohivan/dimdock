using DimDock.Models;

namespace DimDock.Services;

public interface IMonitorBackend : IDisposable
{
    IReadOnlyList<MonitorInfo> EnumerateMonitors();
    bool SetBrightness(string monitorId, int brightnessPercent);
}
