namespace DimDock.Models;

public sealed class AppSettings
{
    public const string AllMonitorsTarget = "all";

    public int ScrollStep { get; set; } = 5;
    public bool LinkMonitors { get; set; } = false;
    public string TrayScrollTarget { get; set; } = AllMonitorsTarget;

    public AppSettings Normalize()
    {
        ScrollStep = ScrollStep switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10
        };

        if (string.IsNullOrWhiteSpace(TrayScrollTarget))
        {
            TrayScrollTarget = AllMonitorsTarget;
        }

        return this;
    }
}
