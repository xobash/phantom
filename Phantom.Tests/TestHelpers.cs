using Phantom.Models;
using Phantom.Services;

namespace Phantom.Tests;

internal static class TestHelpers
{
    public static AppPaths CreateIsolatedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "phantom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new AppPaths(root);
    }

    public static LogService CreateLogService(AppPaths paths, Func<AppSettings>? settingsAccessor = null)
    {
        var log = new LogService(paths, settingsAccessor ?? (() => new AppSettings()));
        log.OpenSessionLog();
        return log;
    }
}
