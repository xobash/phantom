using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class SettingsViewModel : ObservableObject, ISectionViewModel
{
    private static readonly IReadOnlyList<string> ThemeModeOptions = new[] { AppThemeModes.Auto, AppThemeModes.Light, AppThemeModes.Dark };

    private readonly SettingsStore _store;
    private readonly LogService _logService;
    private readonly SettingsProvider _provider;
    private readonly ThemeService _theme;
    private readonly AppPaths _paths;

    private AppSettings _settings = new();
    private string _selectedLog = string.Empty;
    private string _selectedLogContent = string.Empty;

    public SettingsViewModel(SettingsStore store, LogService logService, SettingsProvider provider, ThemeService theme, AppPaths paths)
    {
        _store = store;
        _logService = logService;
        _provider = provider;
        _theme = theme;
        _paths = paths;
        LogFiles = new ObservableCollection<string>();
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RefreshLogsCommand = new AsyncRelayCommand(RefreshLogsAsync);
        OpenLogCommand = new AsyncRelayCommand(OpenSelectedLogAsync);
    }

    public string Title => "Settings";

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RefreshLogsCommand { get; }
    public AsyncRelayCommand OpenLogCommand { get; }

    public ObservableCollection<string> LogFiles { get; }

    public IReadOnlyList<string> ThemeModes => ThemeModeOptions;

    public string SelectedThemeMode
    {
        get => AppThemeModes.Normalize(_settings.ThemeMode);
        set
        {
            var normalized = AppThemeModes.Normalize(value);
            if (!string.Equals(_settings.ThemeMode, normalized, StringComparison.Ordinal))
            {
                _settings.ThemeMode = normalized;
                ApplyThemeSelection();
                Notify();
                Notify(nameof(UseDarkMode));
            }
        }
    }

    public bool UseDarkMode
    {
        get => _settings.UseDarkMode;
        set
        {
            if (_settings.UseDarkMode != value)
            {
                _settings.UseDarkMode = value;
                _settings.ThemeMode = value ? AppThemeModes.Dark : AppThemeModes.Light;
                _theme.ApplyThemeMode(_settings.ThemeMode);
                Notify();
                Notify(nameof(SelectedThemeMode));
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

    public string SelectedLog
    {
        get => _selectedLog;
        set
        {
            if (!SetProperty(ref _selectedLog, value))
            {
                return;
            }

            _ = LoadSelectedLogContentAsync(value);
        }
    }

    public string SelectedLogContent
    {
        get => _selectedLogContent;
        set => SetProperty(ref _selectedLogContent, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _settings = await _store.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(_settings.ThemeMode))
        {
            _settings.ThemeMode = _settings.UseDarkMode ? AppThemeModes.Dark : AppThemeModes.Light;
        }

        ApplyThemeSelection();
        _provider.Update(_settings);
        Notify(nameof(SelectedThemeMode));
        Notify(nameof(UseDarkMode));
        Notify(nameof(EnableDestructiveOperations));
        Notify(nameof(CreateRestorePointBeforeDangerousOperations));
        Notify(nameof(HomeRefreshSeconds));
        Notify(nameof(MaxLogFiles));
        Notify(nameof(MaxTotalLogSizeBytes));
        await RefreshLogsAsync(cancellationToken).ConfigureAwait(false);
    }

    public AppSettings Current => _settings;

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        ApplyThemeSelection();
        await _store.SaveAsync(_settings, cancellationToken);
        _provider.Update(_settings);
        await _logService.EnforceRetentionAsync(cancellationToken);
        await RefreshLogsAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ApplyThemeSelection()
    {
        _settings.ThemeMode = AppThemeModes.Normalize(_settings.ThemeMode);
        _theme.ApplyThemeMode(_settings.ThemeMode);
        _settings.UseDarkMode = _theme.IsDarkMode;
    }

    private async Task RefreshLogsAsync(CancellationToken cancellationToken)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LogFiles.Clear();
            if (Directory.Exists(_paths.LogsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log").OrderByDescending(x => x))
                {
                    LogFiles.Add(file);
                }
            }

            if (string.IsNullOrWhiteSpace(SelectedLog) || !File.Exists(SelectedLog))
            {
                SelectedLog = LogFiles.FirstOrDefault() ?? string.Empty;
            }
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);
    }

    private async Task OpenSelectedLogAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        try
        {
            var hasSelectedLog = !string.IsNullOrWhiteSpace(SelectedLog) && File.Exists(SelectedLog);
            var argument = hasSelectedLog
                ? $"/select,\"{SelectedLog}\""
                : $"\"{_paths.LogsDirectory}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = argument,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                SelectedLogContent = $"Failed to open log location:{Environment.NewLine}{ex.Message}");
        }
    }

    private async Task LoadSelectedLogContentAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SelectedLogContent = string.Empty;
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() => SelectedLogContent = content);
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                SelectedLogContent = $"Failed to load log file:{Environment.NewLine}{ex.Message}");
        }
    }
}
