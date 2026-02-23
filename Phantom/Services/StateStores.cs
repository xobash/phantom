using Phantom.Models;
using Microsoft.Win32;

namespace Phantom.Services;

public sealed class SettingsStore
{
    private readonly JsonFileStore _store;
    private readonly AppPaths _paths;

    public SettingsStore(JsonFileStore store, AppPaths paths)
    {
        _store = store;
        _paths = paths;
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        return _store.LoadAsync(_paths.SettingsFile, CreateDefaultSettings, cancellationToken);
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        return _store.SaveAsync(_paths.SettingsFile, settings, cancellationToken);
    }

    private static AppSettings CreateDefaultSettings()
    {
        var settings = new AppSettings();
        settings.UseDarkMode = GetWindowsDarkModePreference();
        return settings;
    }

    private static bool GetWindowsDarkModePreference()
    {
        try
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return true;
            }

            const string personalizePath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            using var key = Registry.CurrentUser.OpenSubKey(personalizePath, writable: false);
            var raw = key?.GetValue("AppsUseLightTheme");
            if (raw is int intValue)
            {
                return intValue == 0;
            }
        }
        catch
        {
        }

        return true;
    }
}

public sealed class TelemetryStore
{
    private readonly JsonFileStore _store;
    private readonly AppPaths _paths;

    public TelemetryStore(JsonFileStore store, AppPaths paths)
    {
        _store = store;
        _paths = paths;
    }

    public Task<TelemetryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        return _store.LoadAsync(_paths.TelemetryFile, () => new TelemetryState(), cancellationToken);
    }

    public Task SaveAsync(TelemetryState telemetry, CancellationToken cancellationToken = default)
    {
        return _store.SaveAsync(_paths.TelemetryFile, telemetry, cancellationToken);
    }
}

public sealed class UndoStateStore
{
    private readonly JsonFileStore _store;
    private readonly AppPaths _paths;

    public UndoStateStore(JsonFileStore store, AppPaths paths)
    {
        _store = store;
        _paths = paths;
    }

    public Task<UndoStateDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        return _store.LoadAsync(_paths.UndoStateFile, () => new UndoStateDocument(), cancellationToken);
    }

    public Task SaveAsync(UndoStateDocument doc, CancellationToken cancellationToken = default)
    {
        return _store.SaveAsync(_paths.UndoStateFile, doc, cancellationToken);
    }
}
