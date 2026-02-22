using System.Collections.ObjectModel;
using System.Windows;
using Phantom.Commands;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class LogsAboutViewModel : ObservableObject, ISectionViewModel
{
    private readonly AppPaths _paths;

    private string _aboutBanner = string.Empty;
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

    public string AboutText
    {
        get => _aboutText;
        set => SetProperty(ref _aboutText, value);
    }

    public string AboutBanner
    {
        get => _aboutBanner;
        set => SetProperty(ref _aboutBanner, value);
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
        AboutBanner = BuildAboutBanner();
        AboutText = await BuildAboutFromReadmeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildAboutBanner()
    {
        return """
██████╗ ██╗  ██╗ █████╗ ███╗   ██╗████████╗ ██████╗ ███╗   ███╗
██╔══██╗██║  ██║██╔══██╗████╗  ██║╚══██╔══╝██╔═══██╗████╗ ████║
██████╔╝███████║███████║██╔██╗ ██║   ██║   ██║   ██║██╔████╔██║
██╔═══╝ ██╔══██║██╔══██║██║╚██╗██║   ██║   ██║   ██║██║╚██╔╝██║
██║     ██║  ██║██║  ██║██║ ╚████║   ██║   ╚██████╔╝██║ ╚═╝ ██║
╚═╝     ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝   ╚═╝    ╚═════╝ ╚═╝     ╚═╝
""";
    }

    private async Task<string> BuildAboutFromReadmeAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return """
# Phantom

A portable, self-contained Windows admin utility built with WPF and .NET 8. Phantom provides a unified interface for system monitoring, tweaks, app management, Windows Update control, and automation — all from a single elevated window with a persistent in-app console.


## Local Data

All data is stored relative to the executable. Nothing leaves the machine.

| Path | Contents |
|---|---|
| `./data/settings.json` | UI and behavior preferences |
| `./data/state.json` | Undo state for applied tweaks |
| `./data/telemetry-local.json` | Local stats (space cleaned, first-run date, network baselines) |
| `./logs/` | Rolling session logs |

---

## Offline Behavior

Operations that require network access (`RequiresNetwork: true`) are blocked before execution if the machine is offline, with a clear error message in the console. No silent failures.

---


## Security Notes

- Phantom requires and verifies administrator elevation on startup — it will not auto-elevate.
- All PowerShell scripts run in-process via the official `Microsoft.PowerShell.SDK`; no `powershell.exe` child processes are spawned for core operations.
- No network calls are made automatically. The only outbound URLs in the codebase are the official winget installer (`aka.ms/getwinget`) and the Chocolatey install script (`community.chocolatey.org/install.ps1`), both triggered only by explicit user action.
- Dangerous operations require both the Settings toggle and an in-prompt confirmation.
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
