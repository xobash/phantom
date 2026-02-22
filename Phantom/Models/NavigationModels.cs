namespace Phantom.Models;

public enum AppSection
{
    Home,
    Apps,
    Services,
    Store,
    Tweaks,
    Features,
    Fixes,
    Updates,
    Automation,
    LogsAbout,
    Settings
}

public sealed class NavigationItem
{
    public AppSection Section { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
}
