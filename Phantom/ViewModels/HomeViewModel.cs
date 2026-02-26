using System.Collections.ObjectModel;
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
    private int _fastRefreshQueued;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _fastRefreshCts;
    private long? _uptimeSeconds;
    private long? _uptimeSeedSeconds;
    private DateTimeOffset? _uptimeSeededAt;

    public HomeViewModel(HomeDataService homeData, TelemetryStore telemetryStore, Func<AppSettings> settingsAccessor, ConsoleStreamService console)
    {
        _homeData = homeData;
        _telemetryStore = telemetryStore;
        _settingsAccessor = settingsAccessor;
        _console = console;

        TopCards = new ObservableCollection<HomeCard>();
        KpiTiles = new ObservableCollection<KpiTile>();

        RefreshCommand = new RelayCommand(() => RequestRefresh(forceIfBusy: true));
        RefreshCardCommand = new RelayCommand<string>(card => RequestCardRefresh(card));
        RunWinsatCommand = new AsyncRelayCommand(RunWinsatAsync, () => !_isRefreshing);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(Math.Max(2, _settingsAccessor().HomeRefreshSeconds))
        };
        _timer.Tick += (_, _) => RequestRefresh(forceIfBusy: false);

        _fastMetricsTimer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _fastMetricsTimer.Tick += (_, _) =>
        {
            TickUptimeDisplay();
            _ = RefreshCpuMemoryTilesAsync(CancellationToken.None);
        };
    }

    public string Title => "Home";

    public ObservableCollection<HomeCard> TopCards { get; }
    public ObservableCollection<KpiTile> KpiTiles { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand<string> RefreshCardCommand { get; }
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
        if (forceIfBusy && _isRefreshing)
        {
            _refreshCts?.Cancel();
        }

        _ = RefreshAsync(CancellationToken.None, queueIfBusy: forceIfBusy);
    }

    private void RequestCardRefresh(string? cardTitle)
    {
        if (string.IsNullOrWhiteSpace(cardTitle))
        {
            return;
        }

        var normalized = cardTitle.Trim();
        if (string.Equals(normalized, "CPU %", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Memory %", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "GPU %", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Network", StringComparison.OrdinalIgnoreCase))
        {
            if (_isFastMetricsRefreshing)
            {
                _fastRefreshCts?.Cancel();
            }

            _ = RefreshCpuMemoryTilesAsync(CancellationToken.None);
            return;
        }

        if (string.Equals(normalized, "Uptime", StringComparison.OrdinalIgnoreCase))
        {
            var displayed = GetDisplayedUptimeSeconds();
            if (displayed.HasValue)
            {
                UpdateUptimeCard(displayed.Value);
            }

            return;
        }

        RequestRefresh(forceIfBusy: true);
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
        _refreshCts?.Dispose();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var refreshToken = _refreshCts.Token;

        try
        {
            var snapshot = await _homeData.GetSnapshotAsync(refreshToken, includeDetails: false).ConfigureAwait(false);
            var telemetry = await _telemetryStore.LoadAsync(refreshToken).ConfigureAwait(false);
            var snapshotUptime = TryParseUptimeSeconds(snapshot.Uptime);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (snapshotUptime.HasValue)
                {
                    SeedUptimeFromSample(snapshotUptime.Value);
                }

                var displayedUptime = GetDisplayedUptimeSeconds();
                UpsertTopCard("System", snapshot.Motherboard);
                UpsertTopCard("Graphics", snapshot.Graphics, $"Driver {snapshot.GraphicsDriverVersion} ({snapshot.GraphicsDriverDate})");
                UpsertTopCard("Storage", snapshot.Storage);
                UpsertTopCard("Uptime", displayedUptime.HasValue ? FormatUptime(displayedUptime.Value) : snapshot.Uptime);
                UpsertTopCard("Processor", snapshot.Processor);
                UpsertTopCard("Memory", snapshot.Memory);
                UpsertTopCard("Windows", snapshot.Windows);
                UpsertTopCard("Performance", snapshot.PerformanceScore, PerformanceTooltipText);

                UpsertKpiTile("Apps", snapshot.AppsCount.ToString());
                UpsertKpiTile("Processes", snapshot.ProcessesCount.ToString());
                UpsertKpiTile("Services", snapshot.ServicesCount.ToString());
                UpsertKpiTile("Space cleaned", FormatBytes(telemetry.SpaceCleanedBytes));

                // Live KPI values are driven by the 1-second timer.
                if (!ContainsKpiTile("CPU %"))
                {
                    UpsertKpiTile("CPU %", FormatPercent(snapshot.CpuUsage));
                }

                if (!ContainsKpiTile("GPU %"))
                {
                    UpsertKpiTile("GPU %", FormatPercent(snapshot.GpuUsage));
                }

                if (!ContainsKpiTile("Memory %"))
                {
                    UpsertKpiTile("Memory %", FormatPercent(snapshot.MemoryUsage));
                }

                if (!ContainsKpiTile("Network"))
                {
                    var (rates, total) = FormatNetworkTile(snapshot.NetworkUsage);
                    UpsertKpiTile("Network", rates, total);
                }
            });
        }
        catch (OperationCanceledException) when (refreshToken.IsCancellationRequested)
        {
            // Forced refresh superseded the current in-flight request.
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Home refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshCts?.Dispose();
            _refreshCts = null;
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
        if (_isFastMetricsRefreshing)
        {
            Interlocked.Exchange(ref _fastRefreshQueued, 1);
            return;
        }

        _isFastMetricsRefreshing = true;
        _fastRefreshCts?.Dispose();
        _fastRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fastRefreshToken = _fastRefreshCts.Token;
        try
        {
            var (cpu, memory, gpu, network) = await _homeData.GetLiveMetricsAsync(fastRefreshToken).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpsertKpiTile("CPU %", FormatPercent(cpu));
                UpsertKpiTile("Memory %", FormatPercent(memory));
                UpsertKpiTile("GPU %", FormatPercent(gpu));
                var (rates, total) = FormatNetworkTile(network);
                UpsertKpiTile("Network", rates, total);

                var displayedUptime = GetDisplayedUptimeSeconds();
                if (displayedUptime.HasValue)
                {
                    UpdateUptimeCard(displayedUptime.Value);
                }
            });
        }
        catch (OperationCanceledException) when (fastRefreshToken.IsCancellationRequested)
        {
            // New manual refresh superseded this in-flight metrics request.
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Live metrics refresh failed: {ex.Message}");
        }
        finally
        {
            _fastRefreshCts?.Dispose();
            _fastRefreshCts = null;
            _isFastMetricsRefreshing = false;
            if (Interlocked.Exchange(ref _fastRefreshQueued, 0) == 1)
            {
                _ = RefreshCpuMemoryTilesAsync(CancellationToken.None);
            }
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

    private void UpsertKpiTile(string title, string value, string secondaryValue = "")
    {
        var index = KpiTiles.ToList().FindIndex(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase));
        var next = new KpiTile
        {
            Title = title,
            Value = value,
            SecondaryValue = secondaryValue
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

    private static string FormatPercent(double value)
    {
        var rounded = (int)Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero);
        return rounded.ToString();
    }

    private static (string Rates, string Total) FormatNetworkTile(string networkText)
    {
        if (string.IsNullOrWhiteSpace(networkText))
        {
            return ("↑0B/s ↓0B/s •0B", string.Empty);
        }

        var lines = networkText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (lines.Count == 0)
        {
            return ("↑0B/s ↓0B/s •0B", string.Empty);
        }

        if (lines[0].Contains('↑') || lines[0].Contains('↓'))
        {
            var total = lines.Count > 1 ? lines[1] : "0 B";
            var compactRates = CompactNetworkRates(lines[0]);
            return ($"{compactRates} •{CompactNetworkValue(total)}", string.Empty);
        }

        var upload = ExtractNetworkValue(lines, "Upload:");
        var download = ExtractNetworkValue(lines, "Download:");
        var session = ExtractNetworkValue(lines, "Session:");
        if (session.Length == 0)
        {
            session = lines.Count > 2 ? lines[2] : "0 B";
        }

        return ($"↑{CompactNetworkValue(upload)} ↓{CompactNetworkValue(download)} •{CompactNetworkValue(session)}", string.Empty);
    }

    private static string ExtractNetworkValue(IReadOnlyList<string> lines, string key)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[key.Length..].Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return key.Contains("Session", StringComparison.OrdinalIgnoreCase) ? "0 B" : "0 B/s";
    }

    private static string CompactNetworkRates(string ratesText)
    {
        var raw = ratesText.Replace("Upload:", "↑", StringComparison.OrdinalIgnoreCase)
            .Replace("Download:", "↓", StringComparison.OrdinalIgnoreCase)
            .Replace("Session:", "•", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim();

        return CompactNetworkValue(raw)
            .Replace("↑ ", "↑", StringComparison.Ordinal)
            .Replace("↓ ", "↓", StringComparison.Ordinal)
            .Replace(" /s", "/s", StringComparison.OrdinalIgnoreCase)
            .Replace(" • ", " •", StringComparison.Ordinal);
    }

    private static string CompactNetworkValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0B";
        }

        return value
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("Bytes", "B", StringComparison.OrdinalIgnoreCase);
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
        var displayed = GetDisplayedUptimeSeconds();
        if (!displayed.HasValue)
        {
            return;
        }

        UpdateUptimeCard(displayed.Value);
    }

    private void SeedUptimeFromSample(long sampledSeconds)
    {
        var sampled = Math.Max(0, sampledSeconds);
        if (!_uptimeSeedSeconds.HasValue || !_uptimeSeededAt.HasValue)
        {
            _uptimeSeedSeconds = sampled;
            _uptimeSeededAt = DateTimeOffset.UtcNow;
            _uptimeSeconds = sampled;
            return;
        }

        var displayed = GetDisplayedUptimeSeconds() ?? sampled;

        // Uptime should monotonically increase. Re-seed if sample drift is significant.
        if (sampled + 120 < displayed || sampled > displayed + 120)
        {
            _uptimeSeedSeconds = sampled;
            _uptimeSeededAt = DateTimeOffset.UtcNow;
            _uptimeSeconds = sampled;
        }
    }

    private long? GetDisplayedUptimeSeconds()
    {
        if (!_uptimeSeedSeconds.HasValue || !_uptimeSeededAt.HasValue)
        {
            return _uptimeSeconds;
        }

        var elapsed = Math.Max(0, (long)(DateTimeOffset.UtcNow - _uptimeSeededAt.Value).TotalSeconds);
        var displayed = Math.Max(0, _uptimeSeedSeconds.Value + elapsed);
        _uptimeSeconds = displayed;
        return displayed;
    }

    private void UpdateUptimeCard(long uptimeSeconds)
    {
        var safeSeconds = Math.Max(0, uptimeSeconds);
        _uptimeSeconds = safeSeconds;
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
