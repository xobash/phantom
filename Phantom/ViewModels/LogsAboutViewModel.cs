using System.Collections.ObjectModel;
using System.Windows;
using Phantom.Commands;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class LogsAboutViewModel : ObservableObject, ISectionViewModel
{
    private readonly AppPaths _paths;

    private string _aboutLogo = """
██████╗ ██╗  ██╗ █████╗ ███╗   ██╗████████╗ ██████╗ ███╗   ███╗
██╔══██╗██║  ██║██╔══██╗████╗  ██║╚══██╔══╝██╔═══██╗████╗ ████║
██████╔╝███████║███████║██╔██╗ ██║   ██║   ██║   ██║██╔████╔██║
██╔═══╝ ██╔══██║██╔══██║██║╚██╗██║   ██║   ██║   ██║██║╚██╔╝██║
██║     ██║  ██║██║  ██║██║ ╚████║   ██║   ╚██████╔╝██║ ╚═╝ ██║
╚═╝     ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝   ╚═╝    ╚═════╝ ╚═╝     ╚═╝
""";
    private string _aboutText = "Phantom portable admin utility.";
    private string _selectedLog = string.Empty;
    private string _selectedLogContent = string.Empty;

    public LogsAboutViewModel(AppPaths paths)
    {
        _paths = paths;
        LogFiles = new ObservableCollection<string>();
        RefreshLogsCommand = new AsyncRelayCommand(RefreshLogsAsync);
        OpenLogCommand = new AsyncRelayCommand(OpenSelectedLogAsync);
    }

    public string Title => "Logs/About";

    public ObservableCollection<string> LogFiles { get; }

    public string AboutLogo
    {
        get => _aboutLogo;
        set => SetProperty(ref _aboutLogo, value);
    }

    public string AboutText
    {
        get => _aboutText;
        set => SetProperty(ref _aboutText, value);
    }

    public string SelectedLog
    {
        get => _selectedLog;
        set => SetProperty(ref _selectedLog, value);
    }

    public string SelectedLogContent
    {
        get => _selectedLogContent;
        set => SetProperty(ref _selectedLogContent, value);
    }

    public AsyncRelayCommand RefreshLogsCommand { get; }
    public AsyncRelayCommand OpenLogCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshLogsAsync(cancellationToken).ConfigureAwait(false);
        AboutText = """
Phantom
Portable, self-contained Windows admin utility built with WPF and .NET 8.

What Phantom includes
- Home dashboard for live system cards and KPI metrics.
- Store for package management through winget / Chocolatey.
- Tweaks, Features, and Fixes sections for curated system changes.
- Updates controls with reversible Windows Update modes.
- Logs/About with rolling local logs and operation history.
- Settings for theme, safety gating, refresh interval, and log retention.

Design and safety principles
- Requires Administrator privileges.
- Local-first data model (settings, undo state, telemetry, logs saved next to app data).
- Dangerous operations are explicitly gated and require confirmation.
- Operation output is streamed to the in-app console for transparency.
- Offline blocking is enforced for network-required operations.

Data locations
- ./data/settings.json (UI and behavior preferences)
- ./data/state.json (undo metadata)
- ./data/telemetry-local.json (local telemetry)
- ./logs/ (rolling session logs)
""";
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
        });
    }

    private async Task OpenSelectedLogAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SelectedLog) || !File.Exists(SelectedLog))
        {
            return;
        }

        SelectedLogContent = await File.ReadAllTextAsync(SelectedLog, cancellationToken).ConfigureAwait(false);
    }
}
