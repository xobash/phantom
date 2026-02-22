using System.Collections.ObjectModel;
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

    private string _search = string.Empty;
    private bool _isRefreshing;

    public AppsViewModel(HomeDataService homeData, ConsoleStreamService console)
    {
        _homeData = homeData;
        _console = console;

        InstalledApps = new ObservableCollection<InstalledAppInfo>();
        AppsView = CollectionViewSource.GetDefaultView(InstalledApps);
        AppsView.Filter = FilterApps;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_isRefreshing);
    }

    public string Title => "Apps";

    public ObservableCollection<InstalledAppInfo> InstalledApps { get; }
    public ICollectionView AppsView { get; }
    public AsyncRelayCommand RefreshCommand { get; }

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
            var apps = snapshot.Apps.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            await Application.Current.Dispatcher.InvokeAsync(() => ReplaceCollection(InstalledApps, apps));
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

    private bool FilterApps(object obj)
    {
        if (obj is not InstalledAppInfo app)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return app.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               app.Publisher.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               app.Version.Contains(Search, StringComparison.OrdinalIgnoreCase);
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
