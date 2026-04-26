using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public enum AboutReadmeLoadState
{
    NotLoaded,
    Loading,
    Loaded,
    Error
}

public sealed class SettingsViewModel : ObservableObject, ISectionViewModel
{
    private const int MaxLogPreviewBytes = 256 * 1024;
    private static readonly IReadOnlyList<string> ThemeModeOptions = new[] { AppThemeModes.Auto, AppThemeModes.Light, AppThemeModes.Dark };
    private static readonly IReadOnlyList<string> AccentModeOptions = new[] { AppAccentModes.Windows, AppAccentModes.Custom };

    private readonly SettingsStore _store;
    private readonly LogService _logService;
    private readonly SettingsProvider _provider;
    private readonly ThemeService _theme;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _readmeLoadGate = new(1, 1);

    private AppSettings _settings = new();
    private string _selectedLog = string.Empty;
    private string _selectedLogContent = string.Empty;
    private bool _isAboutExpanded;
    private FlowDocument _readmeDocument = new();
    private SolidColorBrush _accentPreviewBrush = new(Color.FromRgb(0, 120, 212));
    private AboutReadmeLoadState _readmeLoadState = AboutReadmeLoadState.NotLoaded;
    private string _readmeErrorText = string.Empty;

    public SettingsViewModel(SettingsStore store, LogService logService, SettingsProvider provider, ThemeService theme, AppPaths paths)
    {
        _store = store;
        _logService = logService;
        _provider = provider;
        _theme = theme;
        _paths = paths;

        AppDisplayName = ResolveAppDisplayName();
        AppVersion = ResolveAppVersion();
        AppDisplayWithVersion = $"{AppDisplayName} {AppVersion}";
        AboutSummary = "Local-first Windows admin utility focused on safe, reversible system operations.";

        LogFiles = new ObservableCollection<string>();
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RefreshLogsCommand = new AsyncRelayCommand(RefreshLogsAsync);
        OpenLogCommand = new AsyncRelayCommand(OpenSelectedLogAsync);
        _theme.ThemeChanged += (_, _) => RefreshAccentPreview();
    }

    public string Title => "Settings";

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RefreshLogsCommand { get; }
    public AsyncRelayCommand OpenLogCommand { get; }

    public ObservableCollection<string> LogFiles { get; }

    public IReadOnlyList<string> ThemeModes => ThemeModeOptions;
    public IReadOnlyList<string> AccentModes => AccentModeOptions;

    public string AppDisplayName { get; }
    public string AppVersion { get; }
    public string AppDisplayWithVersion { get; }
    public string AboutSummary { get; }

    public bool IsAboutExpanded
    {
        get => _isAboutExpanded;
        set
        {
            if (!SetProperty(ref _isAboutExpanded, value))
            {
                return;
            }

            Notify(nameof(AboutChevronGlyph));
            if (_isAboutExpanded)
            {
                _ = EnsureReadmeLoadedAsync();
            }
        }
    }

    public string AboutChevronGlyph => _isAboutExpanded ? "\uE70E" : "\uE70D";

    public FlowDocument ReadmeDocument
    {
        get => _readmeDocument;
        private set => SetProperty(ref _readmeDocument, value);
    }

    public AboutReadmeLoadState ReadmeLoadState
    {
        get => _readmeLoadState;
        private set
        {
            if (!SetProperty(ref _readmeLoadState, value))
            {
                return;
            }

            Notify(nameof(IsReadmeLoading));
            Notify(nameof(HasReadmeError));
            Notify(nameof(HasReadmeContent));
        }
    }

    public string ReadmeErrorText
    {
        get => _readmeErrorText;
        private set => SetProperty(ref _readmeErrorText, value);
    }

    public bool IsReadmeLoading => ReadmeLoadState == AboutReadmeLoadState.Loading;
    public bool HasReadmeError => ReadmeLoadState == AboutReadmeLoadState.Error;
    public bool HasReadmeContent => ReadmeLoadState == AboutReadmeLoadState.Loaded;

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

    public string SelectedAccentMode
    {
        get => AppAccentModes.Normalize(_settings.AccentMode);
        set
        {
            var normalized = AppAccentModes.Normalize(value);
            if (string.Equals(_settings.AccentMode, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _settings.AccentMode = normalized;
            if (string.Equals(normalized, AppAccentModes.Custom, StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(_settings.CustomAccentColor))
            {
                _settings.CustomAccentColor = _theme.CurrentAccentHex;
                Notify(nameof(CustomAccentColor));
            }

            ApplyThemeSelection();
            Notify();
            Notify(nameof(IsCustomAccentSelected));
        }
    }

    public string CustomAccentColor
    {
        get => _settings.CustomAccentColor;
        set
        {
            var colorText = value?.Trim() ?? string.Empty;
            if (string.Equals(_settings.CustomAccentColor, colorText, StringComparison.Ordinal))
            {
                return;
            }

            _settings.CustomAccentColor = colorText;
            if (string.Equals(SelectedAccentMode, AppAccentModes.Custom, StringComparison.Ordinal) &&
                ThemeService.TryParseAccentColor(colorText, out _, out _))
            {
                ApplyThemeSelection();
            }

            Notify();
        }
    }

    public bool IsCustomAccentSelected => string.Equals(SelectedAccentMode, AppAccentModes.Custom, StringComparison.Ordinal);

    public Brush AccentPreviewBrush => _accentPreviewBrush;
    public string CurrentAccentColor => _theme.CurrentAccentHex;

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
        Notify(nameof(SelectedAccentMode));
        Notify(nameof(CustomAccentColor));
        Notify(nameof(IsCustomAccentSelected));
        RefreshAccentPreview();
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
        _settings.AccentMode = AppAccentModes.Normalize(_settings.AccentMode);
        _theme.ApplyThemeMode(_settings.ThemeMode, _settings.AccentMode, _settings.CustomAccentColor);
        if (string.Equals(_settings.AccentMode, AppAccentModes.Custom, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_theme.CurrentCustomAccentColor))
        {
            _settings.CustomAccentColor = _theme.CurrentCustomAccentColor;
            Notify(nameof(CustomAccentColor));
        }

        _settings.UseDarkMode = _theme.IsDarkMode;
        RefreshAccentPreview();
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
            var fileInfo = new FileInfo(path);
            var content = fileInfo.Length <= MaxLogPreviewBytes
                ? await File.ReadAllTextAsync(path).ConfigureAwait(false)
                : await ReadTailPreviewAsync(path, fileInfo.Length).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() => SelectedLogContent = content);
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                SelectedLogContent = $"Failed to load log file:{Environment.NewLine}{ex.Message}");
        }
    }

    private static async Task<string> ReadTailPreviewAsync(string path, long fileLength)
    {
        var previewBytes = (int)Math.Min(MaxLogPreviewBytes, fileLength);
        var buffer = new byte[previewBytes];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 8192, useAsync: true);
        var start = Math.Max(0, fileLength - previewBytes);
        stream.Seek(start, SeekOrigin.Begin);

        var offset = 0;
        while (offset < previewBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, previewBytes - offset)).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var tailText = Encoding.UTF8.GetString(buffer, 0, offset);
        var totalKb = fileLength / 1024d;
        return $"[Preview truncated. Showing last {MaxLogPreviewBytes / 1024} KB of {totalKb:N1} KB file.]"
               + Environment.NewLine
               + Environment.NewLine
               + tailText;
    }

    private async Task EnsureReadmeLoadedAsync()
    {
        if (ReadmeLoadState == AboutReadmeLoadState.Loaded || ReadmeLoadState == AboutReadmeLoadState.Loading)
        {
            return;
        }

        await _readmeLoadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReadmeLoadState == AboutReadmeLoadState.Loaded || ReadmeLoadState == AboutReadmeLoadState.Loading)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() => ReadmeLoadState = AboutReadmeLoadState.Loading);

            var readmePath = Path.Combine(AppContext.BaseDirectory, "README.md");
            if (!File.Exists(readmePath))
            {
                await SetReadmeErrorAsync("README not available.", $"README file was not found at {readmePath}.").ConfigureAwait(false);
                return;
            }

            var markdown = await File.ReadAllTextAsync(readmePath).ConfigureAwait(false);
            var document = await Application.Current.Dispatcher.InvokeAsync(() =>
                ReadmeMarkdownRenderer.Render(markdown, OpenReadmeLink));

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ReadmeDocument = document;
                ReadmeErrorText = string.Empty;
                ReadmeLoadState = AboutReadmeLoadState.Loaded;
            });
        }
        catch (Exception ex)
        {
            await SetReadmeErrorAsync("README not available.", $"README load failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _readmeLoadGate.Release();
        }
    }

    private async Task SetReadmeErrorAsync(string userMessage, string diagnostic)
    {
        try
        {
            await _logService.WriteAsync("Error", diagnostic, echoToConsole: false).ConfigureAwait(false);
        }
        catch
        {
            // Do not fail the Settings UI if log persistence fails.
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ReadmeErrorText = userMessage;
            ReadmeLoadState = AboutReadmeLoadState.Error;
        });
    }

    private void OpenReadmeLink(Uri uri)
    {
        if (!IsAllowedReadmeLink(uri))
        {
            _ = _logService.WriteAsync("Warning", $"Blocked unsupported README link scheme: {uri}", echoToConsole: false);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _ = _logService.WriteAsync("Error", $"Failed to open README link '{uri}': {ex.Message}", echoToConsole: false);
        }
    }

    private static bool IsAllowedReadmeLink(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAppDisplayName()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SettingsViewModel).Assembly;
        return assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
               ?? assembly.GetName().Name
               ?? "Phantom";
    }

    private static string ResolveAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SettingsViewModel).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return infoVersion.Split('+')[0].Trim();
        }

        var version = assembly.GetName().Version;
        if (version is null)
        {
            return "0.0.0";
        }

        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private void RefreshAccentPreview()
    {
        var brush = new SolidColorBrush(_theme.CurrentAccentColor);
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        _accentPreviewBrush = brush;
        Notify(nameof(AccentPreviewBrush));
        Notify(nameof(CurrentAccentColor));
    }
}
