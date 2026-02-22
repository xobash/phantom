using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class ServicesViewModel : ObservableObject, ISectionViewModel
{
    private readonly HomeDataService _homeData;
    private readonly ConsoleStreamService _console;
    private readonly IPowerShellRunner _runner;

    private string _search = string.Empty;
    private bool _isRefreshing;
    private string _servicesCountLabel = "0 services";

    public ServicesViewModel(HomeDataService homeData, ConsoleStreamService console, IPowerShellRunner runner)
    {
        _homeData = homeData;
        _console = console;
        _runner = runner;

        Services = new ObservableCollection<ServiceInfoRow>();
        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = FilterService;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_isRefreshing);
        StopServiceCommand = new AsyncRelayCommand<ServiceInfoRow>(StopServiceAsync);
        RestartServiceCommand = new AsyncRelayCommand<ServiceInfoRow>(RestartServiceAsync);
        BrowseServiceLocationCommand = new AsyncRelayCommand<ServiceInfoRow>(BrowseServiceLocationAsync);
        SearchOnlineCommand = new AsyncRelayCommand<ServiceInfoRow>(SearchOnlineAsync);
        SetAutomaticModeCommand = new AsyncRelayCommand<ServiceInfoRow>((service, token) => SetModeAsync(service, "Automatic", token));
        SetManualModeCommand = new AsyncRelayCommand<ServiceInfoRow>((service, token) => SetModeAsync(service, "Manual", token));
        SetDisabledModeCommand = new AsyncRelayCommand<ServiceInfoRow>((service, token) => SetModeAsync(service, "Disabled", token));
    }

    public string Title => "Services";

    public ObservableCollection<ServiceInfoRow> Services { get; }
    public ICollectionView ServicesView { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand<ServiceInfoRow> StopServiceCommand { get; }
    public AsyncRelayCommand<ServiceInfoRow> RestartServiceCommand { get; }
    public AsyncRelayCommand<ServiceInfoRow> BrowseServiceLocationCommand { get; }
    public AsyncRelayCommand<ServiceInfoRow> SearchOnlineCommand { get; }
    public AsyncRelayCommand<ServiceInfoRow> SetAutomaticModeCommand { get; }
    public AsyncRelayCommand<ServiceInfoRow> SetManualModeCommand { get; }
    public AsyncRelayCommand<ServiceInfoRow> SetDisabledModeCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                ServicesView.Refresh();
            }
        }
    }

    public string ServicesCountLabel
    {
        get => _servicesCountLabel;
        set => SetProperty(ref _servicesCountLabel, value);
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
                Services.Clear();
                foreach (var service in snapshot.Services.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    service.Summary = ResolveSummary(service);
                    Services.Add(service);
                }

                ServicesCountLabel = $"{Services.Count} services";
                ServicesView.Refresh();
            });
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Services refresh failed: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private Task StopServiceAsync(ServiceInfoRow? service, CancellationToken cancellationToken)
    {
        if (service is null)
        {
            return Task.CompletedTask;
        }

        var name = EscapeSingleQuotes(service.Name);
        return ExecuteScriptAsync("services.stop", service.Name, $"Stop-Service -Name '{name}' -Force -ErrorAction Stop", cancellationToken, refreshAfter: true);
    }

    private Task RestartServiceAsync(ServiceInfoRow? service, CancellationToken cancellationToken)
    {
        if (service is null)
        {
            return Task.CompletedTask;
        }

        var name = EscapeSingleQuotes(service.Name);
        return ExecuteScriptAsync("services.restart", service.Name, $"Restart-Service -Name '{name}' -Force -ErrorAction Stop", cancellationToken, refreshAfter: true);
    }

    private Task BrowseServiceLocationAsync(ServiceInfoRow? service, CancellationToken cancellationToken)
    {
        if (service is null)
        {
            return Task.CompletedTask;
        }

        var executable = ExtractExecutablePath(service.PathName);
        if (string.IsNullOrWhiteSpace(executable))
        {
            _console.Publish("Warning", $"Service location unavailable: {service.DisplayName}.");
            return Task.CompletedTask;
        }

        var folder = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(folder))
        {
            _console.Publish("Warning", $"Service folder unavailable: {service.DisplayName}.");
            return Task.CompletedTask;
        }

        return ExecuteScriptAsync("services.browse", service.Name, $"Start-Process -FilePath 'explorer.exe' -ArgumentList '{EscapeSingleQuotes(folder)}'", cancellationToken, refreshAfter: false);
    }

    private Task SearchOnlineAsync(ServiceInfoRow? service, CancellationToken cancellationToken)
    {
        if (service is null)
        {
            return Task.CompletedTask;
        }

        var query = Uri.EscapeDataString($"{service.DisplayName} {service.Name} windows service");
        var url = $"https://www.bing.com/search?q={query}";
        return ExecuteScriptAsync("services.search", service.Name, $"Start-Process -FilePath 'explorer.exe' -ArgumentList '{EscapeSingleQuotes(url)}'", cancellationToken, refreshAfter: false);
    }

    private Task SetModeAsync(ServiceInfoRow? service, string mode, CancellationToken cancellationToken)
    {
        if (service is null)
        {
            return Task.CompletedTask;
        }

        var name = EscapeSingleQuotes(service.Name);
        var script = $"Set-Service -Name '{name}' -StartupType {mode}";
        return ExecuteScriptAsync($"services.mode.{mode.ToLowerInvariant()}", service.Name, script, cancellationToken, refreshAfter: true);
    }

    private async Task ExecuteScriptAsync(string operationId, string serviceName, string script, CancellationToken cancellationToken, bool refreshAfter)
    {
        var result = await _runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = operationId,
            StepName = serviceName,
            Script = script,
            DryRun = false
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            _console.Publish("Error", $"{operationId} failed for {serviceName}.");
        }

        if (refreshAfter)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool FilterService(object obj)
    {
        if (obj is not ServiceInfoRow service)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return service.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || service.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || service.Status.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || service.StartupType.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || service.PathName.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractExecutablePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();
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

    private static string ResolveSummary(ServiceInfoRow service)
    {
        if (!string.IsNullOrWhiteSpace(service.Summary))
        {
            return service.Summary;
        }

        if (!string.IsNullOrWhiteSpace(service.Description))
        {
            return service.Description;
        }

        if (!string.IsNullOrWhiteSpace(service.PathName))
        {
            return $"Path: {service.PathName}";
        }

        return "No description is available for this service.";
    }
}
