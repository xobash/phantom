using System.Diagnostics;

namespace Phantom.Services;

public sealed class AppPaths
{
    public AppPaths(string? baseDirectory = null)
    {
        BaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        DataDirectory = Path.Combine(BaseDirectory, "data");
        LogsDirectory = Path.Combine(BaseDirectory, "logs");
        RuntimeDirectory = Path.Combine(BaseDirectory, "runtime");

        EnsureDirectory(DataDirectory);
        EnsureDirectory(LogsDirectory);
        EnsureDirectory(RuntimeDirectory);
    }

    public string BaseDirectory { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string RuntimeDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string TelemetryFile => Path.Combine(DataDirectory, "telemetry-local.json");
    public string UndoStateFile => Path.Combine(DataDirectory, "state.json");

    public string CatalogFile => Path.Combine(BaseDirectory, "Data", "catalog.apps.json");
    public string TweaksFile => Path.Combine(BaseDirectory, "Data", "tweaks.json");
    public string FeaturesFile => Path.Combine(BaseDirectory, "Data", "features.json");
    public string FixesFile => Path.Combine(BaseDirectory, "Data", "fixes.json");
    public string LegacyPanelsFile => Path.Combine(BaseDirectory, "Data", "legacy-panels.json");

    private static void EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"AppPaths failed to create directory '{path}': {ex.Message}");
        }
    }
}
