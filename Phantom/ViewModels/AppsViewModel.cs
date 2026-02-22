using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class AppsViewModel : ObservableObject, ISectionViewModel
{
    private readonly HomeDataService _homeData;
    private readonly ConsoleStreamService _console;
    private readonly IPowerShellRunner _runner;

    private string _search = string.Empty;
    private bool _isRefreshing;
    private string _appsCountLabel = "0 apps";

    public AppsViewModel(HomeDataService homeData, ConsoleStreamService console, IPowerShellRunner runner)
    {
        _homeData = homeData;
        _console = console;
        _runner = runner;

        Apps = new ObservableCollection<InstalledAppInfo>();
        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_isRefreshing);
        BrowseCommand = new AsyncRelayCommand<InstalledAppInfo>(BrowseAsync);
        SearchOnlineCommand = new AsyncRelayCommand<InstalledAppInfo>(SearchOnlineAsync);
        UninstallCommand = new AsyncRelayCommand<InstalledAppInfo>(UninstallAsync);
    }

    public string Title => "Apps";

    public ObservableCollection<InstalledAppInfo> Apps { get; }
    public ICollectionView AppsView { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand<InstalledAppInfo> BrowseCommand { get; }
    public AsyncRelayCommand<InstalledAppInfo> SearchOnlineCommand { get; }
    public AsyncRelayCommand<InstalledAppInfo> UninstallCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                AppsView.Refresh();
            }
        }
    }

    public string AppsCountLabel
    {
        get => _appsCountLabel;
        set => SetProperty(ref _appsCountLabel, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _isRefreshing = true;
        RefreshCommand.RaiseCanExecuteChanged();

        try
        {
            var snapshot = await _homeData.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Apps.Clear();
                foreach (var app in snapshot.Apps.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Apps.Add(app);
                }

                AppsCountLabel = $"{Apps.Count} apps";
                AppsView.Refresh();
            });
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Apps refresh failed: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task BrowseAsync(InstalledAppInfo? app, CancellationToken cancellationToken)
    {
        if (app is null)
        {
            return;
        }

        var path = ResolveInstallFolder(app);
        if (string.IsNullOrWhiteSpace(path))
        {
            _console.Publish("Warning", $"Browse failed for {app.Name}: location not available.");
            return;
        }

        var escapedPath = EscapeSingleQuotes(path);
        await ExecuteScriptAsync("apps.browse", app.Name, $"Start-Process -FilePath 'explorer.exe' -ArgumentList '{escapedPath}'", cancellationToken, refreshAfter: false).ConfigureAwait(false);
    }

    private async Task SearchOnlineAsync(InstalledAppInfo? app, CancellationToken cancellationToken)
    {
        if (app is null)
        {
            return;
        }

        var query = Uri.EscapeDataString($"{app.Name} {app.Publisher} download");
        var url = $"https://www.bing.com/search?q={query}";
        await ExecuteScriptAsync("apps.search", app.Name, $"Start-Process -FilePath 'explorer.exe' -ArgumentList '{EscapeSingleQuotes(url)}'", cancellationToken, refreshAfter: false).ConfigureAwait(false);
    }

    private async Task UninstallAsync(InstalledAppInfo? app, CancellationToken cancellationToken)
    {
        if (app is null)
        {
            return;
        }

        var uninstallCommand = NormalizeUninstallCommand(app.UninstallCommand);
        if (string.IsNullOrWhiteSpace(uninstallCommand))
        {
            _console.Publish("Warning", $"Uninstall unavailable for {app.Name}: uninstall command not found.");
            return;
        }

        var escaped = EscapeSingleQuotes(uninstallCommand);
        await ExecuteScriptAsync("apps.uninstall", app.Name, $"$cmd='{escaped}'; Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $cmd -Wait", cancellationToken, refreshAfter: true).ConfigureAwait(false);
    }

    private async Task ExecuteScriptAsync(string operationId, string appName, string script, CancellationToken cancellationToken, bool refreshAfter)
    {
        var result = await _runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = operationId,
            StepName = appName,
            Script = script,
            DryRun = false
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            _console.Publish("Error", $"{operationId} failed for {appName}.");
        }

        if (refreshAfter)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool FilterApp(object obj)
    {
        if (obj is not InstalledAppInfo app)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return app.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || app.Publisher.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || app.Version.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || app.InstallDate.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveInstallFolder(InstalledAppInfo app)
    {
        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            return app.InstallLocation.Trim('"');
        }

        var iconPath = NormalizePathWithOptionalIndex(app.DisplayIcon);
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            var iconDirectory = Path.GetDirectoryName(iconPath);
            if (!string.IsNullOrWhiteSpace(iconDirectory))
            {
                return iconDirectory;
            }
        }

        var uninstallExe = ExtractExecutablePath(app.UninstallCommand);
        if (!string.IsNullOrWhiteSpace(uninstallExe))
        {
            return Path.GetDirectoryName(uninstallExe) ?? string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeUninstallCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var normalized = command.Trim();
        if (normalized.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Regex.Replace(normalized, "(?i)/I(?=\\s|\\{)", "/X");
        }

        return normalized;
    }

    private static string NormalizePathWithOptionalIndex(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.Trim().Trim('"');
        var commaIndex = cleaned.IndexOf(',');
        if (commaIndex > 0)
        {
            cleaned = cleaned[..commaIndex];
        }

        return cleaned.Trim().Trim('"');
    }

    private static string ExtractExecutablePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1)
            {
                return text[1..end];
            }
        }

        var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            return text[..(exeIndex + 4)].Trim('"');
        }

        return text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    }

    private static string EscapeSingleQuotes(string text) => text.Replace("'", "''");
}
