using System.Collections.ObjectModel;
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

    private string _search = string.Empty;
    private bool _isRefreshing;

    public ServicesViewModel(HomeDataService homeData, ConsoleStreamService console)
    {
        _homeData = homeData;
        _console = console;

        Services = new ObservableCollection<ServiceInfoRow>();
        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = FilterServices;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_isRefreshing);
    }

    public string Title => "Services";

    public ObservableCollection<ServiceInfoRow> Services { get; }
    public ICollectionView ServicesView { get; }
    public AsyncRelayCommand RefreshCommand { get; }

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
            var services = snapshot.Services.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            await Application.Current.Dispatcher.InvokeAsync(() => ReplaceCollection(Services, services));
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

    private bool FilterServices(object obj)
    {
        if (obj is not ServiceInfoRow service)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return service.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               service.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               service.Status.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               service.StartupType.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
