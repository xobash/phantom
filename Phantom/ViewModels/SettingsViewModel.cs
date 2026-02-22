using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class SettingsViewModel : ObservableObject, ISectionViewModel
{
    private readonly SettingsStore _store;
    private readonly LogService _logService;
    private readonly SettingsProvider _provider;
    private readonly ThemeService _theme;

    private AppSettings _settings = new();

    public SettingsViewModel(SettingsStore store, LogService logService, SettingsProvider provider, ThemeService theme)
    {
        _store = store;
        _logService = logService;
        _provider = provider;
        _theme = theme;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public string Title => "Settings";

    public AsyncRelayCommand SaveCommand { get; }

    public bool UseDarkMode
    {
        get => _settings.UseDarkMode;
        set
        {
            if (_settings.UseDarkMode != value)
            {
                _settings.UseDarkMode = value;
                _theme.ApplyTheme(value);
                Notify();
            }
        }
    }

    public bool EnableDestructiveOperations
    {
        get => _settings.EnableDestructiveOperations;
        set
        {
            if (_settings.EnableDestructiveOperations != value)
            {
                _settings.EnableDestructiveOperations = value;
                Notify();
            }
        }
    }

    public bool CreateRestorePointBeforeDangerousOperations
    {
        get => _settings.CreateRestorePointBeforeDangerousOperations;
        set
        {
            if (_settings.CreateRestorePointBeforeDangerousOperations != value)
            {
                _settings.CreateRestorePointBeforeDangerousOperations = value;
                Notify();
            }
        }
    }

    public bool EnforceScriptSafetyGuards
    {
        get => _settings.EnforceScriptSafetyGuards;
        set
        {
            if (_settings.EnforceScriptSafetyGuards != value)
            {
                _settings.EnforceScriptSafetyGuards = value;
                Notify();
            }
        }
    }

    public int HomeRefreshSeconds
    {
        get => _settings.HomeRefreshSeconds;
        set
        {
            var safe = Math.Max(2, value);
            if (_settings.HomeRefreshSeconds != safe)
            {
                _settings.HomeRefreshSeconds = safe;
                Notify();
            }
        }
    }

    public int MaxLogFiles
    {
        get => _settings.MaxLogFiles;
        set
        {
            var safe = Math.Max(1, value);
            if (_settings.MaxLogFiles != safe)
            {
                _settings.MaxLogFiles = safe;
                Notify();
            }
        }
    }

    public long MaxTotalLogSizeBytes
    {
        get => _settings.MaxTotalLogSizeBytes;
        set
        {
            var safe = Math.Max(1024 * 1024, value);
            if (_settings.MaxTotalLogSizeBytes != safe)
            {
                _settings.MaxTotalLogSizeBytes = safe;
                Notify();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _provider.Update(_settings);
        _theme.ApplyTheme(_settings.UseDarkMode);
        Notify(nameof(UseDarkMode));
        Notify(nameof(EnableDestructiveOperations));
        Notify(nameof(CreateRestorePointBeforeDangerousOperations));
        Notify(nameof(EnforceScriptSafetyGuards));
        Notify(nameof(HomeRefreshSeconds));
        Notify(nameof(MaxLogFiles));
        Notify(nameof(MaxTotalLogSizeBytes));
    }

    public AppSettings Current => _settings;

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _store.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        _provider.Update(_settings);
        _theme.ApplyTheme(_settings.UseDarkMode);
        await _logService.EnforceRetentionAsync(cancellationToken).ConfigureAwait(false);
    }
}
