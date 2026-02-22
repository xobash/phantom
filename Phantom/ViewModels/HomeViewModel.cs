using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class HomeViewModel : ObservableObject, ISectionViewModel
{
    private const string PerformanceTooltipText = "Windows Experience Index (WinSAT). Base score is the lowest subscore, max 9.9.";

    private readonly HomeDataService _homeData;
    private readonly TelemetryStore _telemetryStore;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly ConsoleStreamService _console;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _fastMetricsTimer;

    private bool _isRefreshing;
    private bool _isFastMetricsRefreshing;
    private int _refreshQueued;
    private long? _uptimeBaselineSeconds;
    private long _uptimeBaselineTimestamp;
    private long? _lastRenderedUptimeSeconds;

    public HomeViewModel(HomeDataService homeData, TelemetryStore telemetryStore, Func<AppSettings> settingsAccessor, ConsoleStreamService console)
    {
        _homeData = homeData;
        _telemetryStore = telemetryStore;
        _settingsAccessor = settingsAccessor;
        _console = console;

        TopCards = new ObservableCollection<HomeCard>();
        KpiTiles = new ObservableCollection<KpiTile>();

        RefreshCommand = new RelayCommand(() => RequestRefresh(forceIfBusy: true));
        RunWinsatCommand = new AsyncRelayCommand(RunWinsatAsync, () => !_isRefreshing);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(Math.Max(2, _settingsAccessor().HomeRefreshSeconds))
        };
        _timer.Tick += (_, _) => RequestRefresh(forceIfBusy: false);

        _fastMetricsTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _fastMetricsTimer.Tick += async (_, _) =>
        {
            TickUptimeDisplay();

            if (!_isFastMetricsRefreshing)
            {
                await RefreshCpuMemoryTilesAsync(CancellationToken.None).ConfigureAwait(false);
            }
        };
    }

    public string Title => "Home";

    public ObservableCollection<HomeCard> TopCards { get; }
    public ObservableCollection<KpiTile> KpiTiles { get; }

    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RunWinsatCommand { get; }

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

    private void RequestRefresh(bool forceIfBusy)
    {
        _ = RefreshAsync(CancellationToken.None, queueIfBusy: forceIfBusy);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken, bool queueIfBusy = true)
    {
        if (_isRefreshing)
        {
            if (queueIfBusy)
            {
                Interlocked.Exchange(ref _refreshQueued, 1);
            }

            return;
        }

        _isRefreshing = true;
        RunWinsatCommand.RaiseCanExecuteChanged();

        try
        {
            var snapshot = await _homeData.GetSnapshotAsync(cancellationToken, includeDetails: false).ConfigureAwait(false);
            var telemetry = await _telemetryStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var snapshotUptime = TryParseUptimeSeconds(snapshot.Uptime);
            if (snapshotUptime.HasValue)
            {
                MaybeResyncUptimeBaseline(snapshotUptime.Value);
            }

            var displayedUptime = GetDisplayedUptimeSeconds();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpsertTopCard("System", snapshot.Motherboard);
                UpsertTopCard("Graphics", snapshot.Graphics, $"Driver {snapshot.GraphicsDriverVersion} ({snapshot.GraphicsDriverDate})");
                UpsertTopCard("Storage", snapshot.Storage);
                UpsertTopCard("Uptime", displayedUptime.HasValue ? FormatUptime(displayedUptime.Value) : snapshot.Uptime);
                UpsertTopCard("Processor", snapshot.Processor);
                UpsertTopCard("Memory", snapshot.Memory);
                UpsertTopCard("Windows", snapshot.Windows);
                UpsertTopCard("Performance", snapshot.PerformanceScore, PerformanceTooltipText);

                UpsertKpiTile("Apps count", snapshot.AppsCount.ToString());
                UpsertKpiTile("Processes count", snapshot.ProcessesCount.ToString());
                UpsertKpiTile("Services count", snapshot.ServicesCount.ToString());
                UpsertKpiTile("Space cleaned total", FormatBytes(telemetry.SpaceCleanedBytes));

                // Live KPI values are driven by the 1-second timer.
                if (!ContainsKpiTile("CPU %"))
                {
                    UpsertKpiTile("CPU %", snapshot.CpuUsage.ToString("F2"));
                }

                if (!ContainsKpiTile("GPU %"))
                {
                    UpsertKpiTile("GPU %", snapshot.GpuUsage.ToString("F2"));
                }

                if (!ContainsKpiTile("Memory %"))
                {
                    UpsertKpiTile("Memory %", snapshot.MemoryUsage.ToString("F2"));
                }

                if (!ContainsKpiTile("Network"))
                {
                    UpsertKpiTile("Network", snapshot.NetworkUsage);
                }
            });
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Home refresh failed: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
            RunWinsatCommand.RaiseCanExecuteChanged();

            if (Interlocked.Exchange(ref _refreshQueued, 0) == 1)
            {
                _ = RefreshAsync(CancellationToken.None, queueIfBusy: true);
            }
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
                    TopCards[index] = new HomeCard { Title = "Performance", Value = score, Tooltip = PerformanceTooltipText };
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
            var (cpu, memory, gpu, uptimeSeconds, network) = await _homeData.GetLiveMetricsAsync(cancellationToken).ConfigureAwait(false);

            if (uptimeSeconds > 0)
            {
                MaybeResyncUptimeBaseline(uptimeSeconds);
            }

            var displayedUptime = GetDisplayedUptimeSeconds();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpsertKpiTile("CPU %", cpu.ToString("F2"));
                UpsertKpiTile("Memory %", memory.ToString("F2"));
                UpsertKpiTile("GPU %", gpu.ToString("F2"));
                UpsertKpiTile("Network", network);

                if (displayedUptime.HasValue)
                {
                    UpdateUptimeCard(displayedUptime.Value);
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

    private bool ContainsKpiTile(string title)
    {
        return KpiTiles.Any(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase));
    }

    private void UpsertTopCard(string title, string value, string? tooltip = null)
    {
        var index = TopCards.ToList().FindIndex(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase));
        var next = new HomeCard
        {
            Title = title,
            Value = value,
            Tooltip = tooltip
        };

        if (index >= 0)
        {
            TopCards[index] = next;
            return;
        }

        TopCards.Add(next);
    }

    private void UpsertKpiTile(string title, string value)
    {
        var index = KpiTiles.ToList().FindIndex(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase));
        var next = new KpiTile
        {
            Title = title,
            Value = value
        };

        if (index >= 0)
        {
            KpiTiles[index] = next;
            return;
        }

        KpiTiles.Add(next);
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

    private static long? TryParseUptimeSeconds(string uptime)
    {
        if (string.IsNullOrWhiteSpace(uptime))
        {
            return null;
        }

        var parts = uptime.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        if (!long.TryParse(parts[0], out var hours) ||
            !long.TryParse(parts[1], out var minutes) ||
            !long.TryParse(parts[2], out var seconds))
        {
            return null;
        }

        if (hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or > 59)
        {
            return null;
        }

        return (hours * 3600) + (minutes * 60) + seconds;
    }

    private static string FormatUptime(long uptimeSeconds)
    {
        var safe = Math.Max(0, uptimeSeconds);
        var hours = safe / 3600;
        var minutes = (safe % 3600) / 60;
        var seconds = safe % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }

    private void TickUptimeDisplay()
    {
        var uptimeSeconds = GetDisplayedUptimeSeconds();
        if (!uptimeSeconds.HasValue)
        {
            return;
        }

        UpdateUptimeCard(uptimeSeconds.Value);
    }

    private void SyncUptimeBaseline(long uptimeSeconds, bool resetRendered = false)
    {
        _uptimeBaselineSeconds = Math.Max(0, uptimeSeconds);
        _uptimeBaselineTimestamp = Stopwatch.GetTimestamp();
        if (resetRendered)
        {
            _lastRenderedUptimeSeconds = null;
        }
    }

    private void MaybeResyncUptimeBaseline(long sampledSeconds)
    {
        var sampled = Math.Max(0, sampledSeconds);
        if (!_uptimeBaselineSeconds.HasValue)
        {
            SyncUptimeBaseline(sampled, resetRendered: true);
            return;
        }

        var displayed = GetDisplayedUptimeSecondsRaw();
        if (!displayed.HasValue)
        {
            SyncUptimeBaseline(sampled, resetRendered: true);
            return;
        }

        var drift = sampled - displayed.Value;
        if (drift > 2)
        {
            SyncUptimeBaseline(sampled);
            return;
        }

        // If uptime drops by a large amount, treat it as reboot and reset the baseline.
        if (drift < -120)
        {
            SyncUptimeBaseline(sampled, resetRendered: true);
        }
    }

    private long? GetDisplayedUptimeSeconds()
    {
        var raw = GetDisplayedUptimeSecondsRaw();
        if (!raw.HasValue)
        {
            return null;
        }

        if (_lastRenderedUptimeSeconds.HasValue && raw.Value < _lastRenderedUptimeSeconds.Value)
        {
            return _lastRenderedUptimeSeconds.Value;
        }

        return raw.Value;
    }

    private long? GetDisplayedUptimeSecondsRaw()
    {
        if (!_uptimeBaselineSeconds.HasValue)
        {
            return null;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - _uptimeBaselineTimestamp;
        if (elapsedTicks < 0)
        {
            elapsedTicks = 0;
        }

        var elapsedSeconds = (long)(elapsedTicks / (double)Stopwatch.Frequency);
        return _uptimeBaselineSeconds.Value + elapsedSeconds;
    }

    private void UpdateUptimeCard(long uptimeSeconds)
    {
        var safeSeconds = Math.Max(0, uptimeSeconds);
        if (_lastRenderedUptimeSeconds.HasValue && safeSeconds < _lastRenderedUptimeSeconds.Value)
        {
            safeSeconds = _lastRenderedUptimeSeconds.Value;
        }

        _lastRenderedUptimeSeconds = safeSeconds;
        var uptimeIndex = TopCards.ToList().FindIndex(t => string.Equals(t.Title, "Uptime", StringComparison.OrdinalIgnoreCase));
        if (uptimeIndex < 0)
        {
            return;
        }

        TopCards[uptimeIndex] = new HomeCard
        {
            Title = "Uptime",
            Value = FormatUptime(safeSeconds)
        };
    }
}
