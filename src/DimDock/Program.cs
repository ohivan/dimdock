using DimDock.Services;
using Velopack;

namespace DimDock;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before WPF initializes so Velopack install/update/uninstall hooks exit cleanly and fast.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => new StartupService().SetEnabled(false))
            .Run();

        // Prevent .NET 10 WPF's built-in Fluent WindowBackdropManager from overriding
        // FlyoutWindow's explicit DWMSBT_TRANSIENTWINDOW (Desktop Acrylic) system backdrop.
        AppContext.SetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", true);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
