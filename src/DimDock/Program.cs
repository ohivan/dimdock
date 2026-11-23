using DimDock.Interop;
using DimDock.Services;
using Velopack;

namespace DimDock;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first so Velopack install/update/uninstall hooks exit cleanly and fast.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => new StartupService().SetEnabled(true))
            .OnBeforeUninstallFastCallback(_ => new StartupService().SetEnabled(false))
            .Run();

        NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_APARTMENTTHREADED);
        try
        {
            using var host = new AppHost();
            host.Run();
        }
        finally
        {
            NativeMethods.CoUninitialize();
        }
    }
}
