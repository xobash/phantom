using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class HomeViewModel : ObservableObject, ISectionViewModel
{
    private readonly HomeDataService _homeData;
    private readonly TelemetryStore _telemetryStore;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly ConsoleStreamService _console;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _fastMetricsTimer;

    private string _appSearch = string.Empty;
    private string _processSearch = string.Empty;
    private string _serviceSearch = string.Empty;
    private bool _isRefreshing;
    private bool _isFastMetricsRefreshing;

    public HomeViewModel(HomeDataService homeData, TelemetryStore telemetryStore, Func<AppSettings> settingsAccessor, ConsoleStreamService console)
    {
        _homeData = homeData;
        _telemetryStore = telemetryStore;
        _settingsAccessor = settingsAccessor;
        _console = console;

        TopCards = new ObservableCollection<HomeCard>();
        KpiTiles = new ObservableCollection<KpiTile>();
        InstalledApps = new ObservableCollection<InstalledAppInfo>();
        Processes = new ObservableCollection<ProcessInfoRow>();
        Services = new ObservableCollection<ServiceInfoRow>();

        AppsView = CollectionViewSource.GetDefaultView(InstalledApps);
        AppsView.Filter = FilterApps;
        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.Filter = FilterProcesses;
        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = FilterServices;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_isRefreshing);
        RunWinsatCommand = new AsyncRelayCommand(RunWinsatAsync, () => !_isRefreshing);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(Math.Max(2, _settingsAccessor().HomeRefreshSeconds))
        };
        _timer.Tick += async (_, _) =>
        {
            if (!_isRefreshing)
            {
                await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
        };

        _fastMetricsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _fastMetricsTimer.Tick += async (_, _) =>
        {
            if (!_isFastMetricsRefreshing)
            {
                await RefreshCpuMemoryTilesAsync(CancellationToken.None).ConfigureAwait(false);
            }
        };
    }

    public string Title => "Home";

    public ObservableCollection<HomeCard> TopCards { get; }
    public ObservableCollection<KpiTile> KpiTiles { get; }
    public ObservableCollection<InstalledAppInfo> InstalledApps { get; }
    public ObservableCollection<ProcessInfoRow> Processes { get; }
    public ObservableCollection<ServiceInfoRow> Services { get; }

    public ICollectionView AppsView { get; }
    public ICollectionView ProcessesView { get; }
    public ICollectionView ServicesView { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RunWinsatCommand { get; }

    public string AppSearch
    {
        get => _appSearch;
        set
        {
            if (SetProperty(ref _appSearch, value))
            {
                AppsView.Refresh();
            }
        }
    }

    public string ProcessSearch
    {
        get => _processSearch;
        set
        {
            if (SetProperty(ref _processSearch, value))
            {
                ProcessesView.Refresh();
            }
        }
    }

    public string ServiceSearch
    {
        get => _serviceSearch;
        set
        {
            if (SetProperty(ref _serviceSearch, value))
            {
                ServicesView.Refresh();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(2, _settingsAccessor().HomeRefreshSeconds));
        _timer.Start();
        _fastMetricsTimer.Start();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void StopTimer()
    {
        _timer.Stop();
        _fastMetricsTimer.Stop();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _isRefreshing = true;
        RefreshCommand.RaiseCanExecuteChanged();
        RunWinsatCommand.RaiseCanExecuteChanged();

        try
        {
            var snapshot = await _homeData.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var telemetry = await _telemetryStore.LoadAsync(cancellationToken).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                TopCards.Clear();
                TopCards.Add(new HomeCard { Title = "System", Value = snapshot.Motherboard });
                TopCards.Add(new HomeCard { Title = "Graphics", Value = snapshot.Graphics, Tooltip = $"Driver {snapshot.GraphicsDriverVersion} ({snapshot.GraphicsDriverDate})" });
                TopCards.Add(new HomeCard { Title = "Storage", Value = snapshot.Storage });
                TopCards.Add(new HomeCard { Title = "Uptime", Value = snapshot.Uptime });
                TopCards.Add(new HomeCard { Title = "Processor", Value = snapshot.Processor });
                TopCards.Add(new HomeCard { Title = "Memory", Value = snapshot.Memory });
                TopCards.Add(new HomeCard { Title = "Windows", Value = snapshot.Windows });
                TopCards.Add(new HomeCard { Title = "Performance", Value = snapshot.PerformanceScore });

                KpiTiles.Clear();
                KpiTiles.Add(new KpiTile { Title = "Apps count", Value = snapshot.AppsCount.ToString() });
                KpiTiles.Add(new KpiTile { Title = "Processes count", Value = snapshot.ProcessesCount.ToString() });
                KpiTiles.Add(new KpiTile { Title = "Services count", Value = snapshot.ServicesCount.ToString() });
                KpiTiles.Add(new KpiTile { Title = "Space cleaned total", Value = FormatBytes(telemetry.SpaceCleanedBytes) });
                KpiTiles.Add(new KpiTile { Title = "CPU %", Value = snapshot.CpuUsage.ToString("F2") });
                KpiTiles.Add(new KpiTile { Title = "GPU %", Value = snapshot.GpuUsage.ToString("F2") });
                KpiTiles.Add(new KpiTile { Title = "Memory %", Value = snapshot.MemoryUsage.ToString("F2") });
                KpiTiles.Add(new KpiTile { Title = "Network", Value = snapshot.NetworkUsage });

                ReplaceCollection(InstalledApps, snapshot.Apps.OrderBy(x => x.Name).ToList());
                ReplaceCollection(Processes, snapshot.Processes.OrderByDescending(x => x.Cpu).ToList());
                ReplaceCollection(Services, snapshot.Services.OrderBy(x => x.Name).ToList());
            });
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Home refresh failed: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
            RefreshCommand.RaiseCanExecuteChanged();
            RunWinsatCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RunWinsatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var score = await _homeData.RunWinsatScoreAsync(cancellationToken).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var index = TopCards.ToList().FindIndex(c => c.Title == "Performance");
                if (index >= 0)
                {
                    TopCards[index] = new HomeCard { Title = "Performance", Value = score };
                }
            });
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"WinSAT failed: {ex.Message}");
        }
    }

    private async Task RefreshCpuMemoryTilesAsync(CancellationToken cancellationToken)
    {
        _isFastMetricsRefreshing = true;
        try
        {
            var (cpu, memory, gpu, uptime, network) = await _homeData.GetLiveMetricsAsync(cancellationToken).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var cpuIndex = KpiTiles.ToList().FindIndex(t => string.Equals(t.Title, "CPU %", StringComparison.OrdinalIgnoreCase));
                if (cpuIndex >= 0)
                {
                    KpiTiles[cpuIndex] = new KpiTile { Title = "CPU %", Value = cpu.ToString("F2") };
                }

                var memoryIndex = KpiTiles.ToList().FindIndex(t => string.Equals(t.Title, "Memory %", StringComparison.OrdinalIgnoreCase));
                if (memoryIndex >= 0)
                {
                    KpiTiles[memoryIndex] = new KpiTile { Title = "Memory %", Value = memory.ToString("F2") };
                }

                var gpuIndex = KpiTiles.ToList().FindIndex(t => string.Equals(t.Title, "GPU %", StringComparison.OrdinalIgnoreCase));
                if (gpuIndex >= 0)
                {
                    KpiTiles[gpuIndex] = new KpiTile { Title = "GPU %", Value = gpu.ToString("F2") };
                }

                var networkIndex = KpiTiles.ToList().FindIndex(t => string.Equals(t.Title, "Network", StringComparison.OrdinalIgnoreCase));
                if (networkIndex >= 0)
                {
                    KpiTiles[networkIndex] = new KpiTile { Title = "Network", Value = network };
                }

                var uptimeIndex = TopCards.ToList().FindIndex(t => string.Equals(t.Title, "Uptime", StringComparison.OrdinalIgnoreCase));
                if (uptimeIndex >= 0)
                {
                    TopCards[uptimeIndex] = new HomeCard { Title = "Uptime", Value = uptime };
                }
            });
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Live metrics refresh failed: {ex.Message}");
        }
        finally
        {
            _isFastMetricsRefreshing = false;
        }
    }

    private bool FilterApps(object obj)
    {
        if (obj is not InstalledAppInfo app)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(AppSearch))
        {
            return true;
        }

        return app.Name.Contains(AppSearch, StringComparison.OrdinalIgnoreCase) ||
               app.Publisher.Contains(AppSearch, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterProcesses(object obj)
    {
        if (obj is not ProcessInfoRow process)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(ProcessSearch) || process.Name.Contains(ProcessSearch, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterServices(object obj)
    {
        if (obj is not ServiceInfoRow service)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(ServiceSearch) ||
               service.Name.Contains(ServiceSearch, StringComparison.OrdinalIgnoreCase) ||
               service.DisplayName.Contains(ServiceSearch, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:F2} {units[index]}";
    }
}
