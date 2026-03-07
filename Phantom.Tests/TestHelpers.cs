using Phantom.Models;
using Phantom.Services;

namespace Phantom.Tests;

internal static class TestHelpers
{
    public static AppPaths CreateIsolatedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "phantom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CopyBundledDataFiles(root);
        return new AppPaths(root);
    }

    public static LogService CreateLogService(AppPaths paths, Func<AppSettings>? settingsAccessor = null)
    {
        var log = new LogService(paths, settingsAccessor ?? (() => new AppSettings()));
        log.OpenSessionLog();
        return log;
    }

    private static void CopyBundledDataFiles(string destinationRoot)
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Phantom", "Data")),
            Path.Combine(Directory.GetCurrentDirectory(), "Phantom", "Data")
        };

        var sourceDataDir = candidates.FirstOrDefault(Directory.Exists);
        if (string.IsNullOrWhiteSpace(sourceDataDir))
        {
            return;
        }

        var destinationDataDir = Path.Combine(destinationRoot, "Data");
        Directory.CreateDirectory(destinationDataDir);
        foreach (var file in Directory.EnumerateFiles(sourceDataDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var destinationFile = Path.Combine(destinationDataDir, name);
            File.Copy(file, destinationFile, overwrite: true);
        }
    }
}
