using System.Runtime.InteropServices;
using DimDock.Interop;
using DimDock.Models;

namespace DimDock.Services;

public sealed class DdcMonitorBackend : IMonitorBackend
{
    private sealed record HandleEntry(
        IntPtr PhysicalHandle,
        uint MinRaw,
        uint MaxRaw,
        bool UseVcpFallback);

    private readonly object _gate = new();
    private readonly Dictionary<string, HandleEntry> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PHYSICAL_MONITOR> _ownedPhysicalMonitors = [];
    private bool _disposed;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        lock (_gate)
        {
            if (_disposed) return [];
            ReleaseHandlesLocked();

            var friendlyNames = QueryDisplayFriendlyNames();
            var hMonitors = new List<(IntPtr Handle, MONITORINFOEX Info)>();

            NativeMethods.EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
                {
                    var info = new MONITORINFOEX
                    {
                        cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>()
                    };
                    if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
                    {
                        hMonitors.Add((hMonitor, info));
                    }
                    return true;
                },
                IntPtr.Zero);

            // Primary first, then left-to-right, top-to-bottom.
            hMonitors.Sort((a, b) =>
            {
                bool aPrimary = (a.Info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                bool bPrimary = (b.Info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                if (aPrimary != bPrimary) return aPrimary ? -1 : 1;

                int cmpX = a.Info.rcMonitor.Left.CompareTo(b.Info.rcMonitor.Left);
                if (cmpX != 0) return cmpX;
                return a.Info.rcMonitor.Top.CompareTo(b.Info.rcMonitor.Top);
            });

            var results = new List<MonitorInfo>();
            int displayIndex = 0;

            foreach (var (hMonitor, info) in hMonitors)
            {
                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                {
                    continue;
                }

                var physicals = new PHYSICAL_MONITOR[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
                {
                    continue;
                }

                bool isPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                friendlyNames.TryGetValue(info.szDevice, out var configEntries);

                for (int i = 0; i < physicals.Length; i++)
                {
                    var phys = physicals[i];
                    if (!TryReadBrightness(phys.hPhysicalMonitor, out uint minRaw, out uint currentRaw, out uint maxRaw, out bool useVcp))
                    {
                        // Non-DDC/CI monitor: release immediately so we don't leak its handle.
                        NativeMethods.DestroyPhysicalMonitors(1, [phys]);
                        continue;
                    }

                    displayIndex++;
                    var configMatch = configEntries is not null && i < configEntries.Count
                        ? configEntries[i]
                        : null;

                    string friendlyName = ResolveDisplayName(configMatch?.FriendlyName, phys.szPhysicalMonitorDescription, displayIndex);
                    string stableId = !string.IsNullOrWhiteSpace(configMatch?.DevicePath)
                        ? configMatch.DevicePath
                        : $"{info.szDevice}#{i}";

                    // Ensure uniqueness even if two identical adapters report the same fallback path.
                    if (_handles.ContainsKey(stableId))
                    {
                        stableId = $"{stableId}#{displayIndex}";
                    }

                    int percent = RawToPercent(currentRaw, minRaw, maxRaw);

                    _ownedPhysicalMonitors.Add(phys);
                    _handles[stableId] = new HandleEntry(phys.hPhysicalMonitor, minRaw, maxRaw, useVcp);

                    results.Add(new MonitorInfo(
                        id: stableId,
                        displayName: friendlyName,
                        deviceName: info.szDevice,
                        isPrimary: isPrimary,
                        brightness: percent,
                        minRaw: minRaw,
                        maxRaw: maxRaw,
                        useVcpFallback: useVcp));
                }
            }

            return results;
        }
    }

    public bool SetBrightness(string monitorId, int brightnessPercent)
    {
        lock (_gate)
        {
            if (_disposed || !_handles.TryGetValue(monitorId, out var entry))
            {
                return false;
            }

            uint rawValue = PercentToRaw(brightnessPercent, entry.MinRaw, entry.MaxRaw);

            if (!entry.UseVcpFallback)
            {
                if (NativeMethods.SetMonitorBrightness(entry.PhysicalHandle, rawValue))
                {
                    return true;
                }
            }

            return NativeMethods.SetVCPFeature(entry.PhysicalHandle, NativeMethods.VCP_LUMINANCE, rawValue);
        }
    }

    internal static int RawToPercent(uint current, uint min, uint max)
    {
        if (max <= min) return 0;
        if (current <= min) return 0;
        if (current >= max) return 100;
        return Math.Clamp((int)Math.Round((current - min) * 100.0 / (max - min)), 0, 100);
    }

    internal static uint PercentToRaw(int percent, uint min, uint max)
    {
        if (max <= min) return min;
        int clamped = Math.Clamp(percent, 0, 100);
        return min + (uint)Math.Round(clamped * (max - min) / 100.0);
    }

    private static bool TryReadBrightness(
        IntPtr physicalHandle,
        out uint minRaw,
        out uint currentRaw,
        out uint maxRaw,
        out bool useVcpFallback)
    {
        // High-level API first (fast path on most monitors)
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (NativeMethods.GetMonitorBrightness(physicalHandle, out minRaw, out currentRaw, out maxRaw) && maxRaw > minRaw)
            {
                useVcpFallback = false;
                return true;
            }

            if (attempt == 0) Thread.Sleep(25);
        }

        // Low-level VCP 0x10 (Luminance) fallback
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                physicalHandle,
                NativeMethods.VCP_LUMINANCE,
                out _,
                out currentRaw,
                out maxRaw) && maxRaw > 0)
            {
                minRaw = 0;
                useVcpFallback = true;
                return true;
            }

            if (attempt == 0) Thread.Sleep(25);
        }

        minRaw = 0;
        currentRaw = 0;
        maxRaw = 0;
        useVcpFallback = false;
        return false;
    }

    private static string ResolveDisplayName(string? edidFriendly, string? physicalDescription, int index)
    {
        if (!string.IsNullOrWhiteSpace(edidFriendly))
        {
            return edidFriendly.Trim();
        }

        if (!string.IsNullOrWhiteSpace(physicalDescription) &&
            !physicalDescription.Contains("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase) &&
            !physicalDescription.Contains("Generic Monitor", StringComparison.OrdinalIgnoreCase))
        {
            return physicalDescription.Trim();
        }

        return $"Display {index}";
    }

    private sealed record DisplayConfigName(string FriendlyName, string DevicePath);

    private static Dictionary<string, List<DisplayConfigName>> QueryDisplayFriendlyNames()
    {
        var map = new Dictionary<string, List<DisplayConfigName>>(StringComparer.OrdinalIgnoreCase);

        if (NativeMethods.GetDisplayConfigBufferSizes(
            NativeMethods.QDC_ONLY_ACTIVE_PATHS,
            out uint pathCount,
            out uint modeCount) != 0 || pathCount == 0)
        {
            return map;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        if (NativeMethods.QueryDisplayConfig(
            NativeMethods.QDC_ONLY_ACTIVE_PATHS,
            ref pathCount,
            paths,
            ref modeCount,
            modes,
            IntPtr.Zero) != 0)
        {
            return map;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var path = paths[i];

            var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id
                }
            };

            if (NativeMethods.DisplayConfigGetDeviceInfo(ref sourceName) != 0)
            {
                continue;
            }

            var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id
                }
            };

            if (NativeMethods.DisplayConfigGetDeviceInfo(ref targetName) != 0)
            {
                continue;
            }

            string gdiDevice = sourceName.GetDeviceName();
            if (string.IsNullOrWhiteSpace(gdiDevice)) continue;

            if (!map.TryGetValue(gdiDevice, out var list))
            {
                list = [];
                map[gdiDevice] = list;
            }

            list.Add(new DisplayConfigName(
                targetName.GetFriendlyName().Trim(),
                targetName.GetDevicePath().Trim()));
        }

        return map;
    }

    private void ReleaseHandlesLocked()
    {
        if (_ownedPhysicalMonitors.Count > 0)
        {
            NativeMethods.DestroyPhysicalMonitors(
                (uint)_ownedPhysicalMonitors.Count,
                _ownedPhysicalMonitors.ToArray());
            _ownedPhysicalMonitors.Clear();
        }

        _handles.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseHandlesLocked();
        }
    }
}
