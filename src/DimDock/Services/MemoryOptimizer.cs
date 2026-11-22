using System.Runtime;
using DimDock.Interop;

namespace DimDock.Services;

/// <summary>
/// Compacts managed heaps and trims cold working-set pages when DimDock is idle in the tray.
/// </summary>
internal static class MemoryOptimizer
{
    public static void TrimMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            NativeMethods.SetProcessWorkingSetSizeEx(
                NativeMethods.GetCurrentProcess(),
                new IntPtr(-1),
                new IntPtr(-1),
                0);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Idle memory trim failed", ex);
        }
    }
}
