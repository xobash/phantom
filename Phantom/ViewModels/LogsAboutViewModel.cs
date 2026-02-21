using System.Collections.ObjectModel;
using System.Windows;
using Phantom.Commands;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class LogsAboutViewModel : ObservableObject, ISectionViewModel
{
    private readonly AppPaths _paths;

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
        AboutText = "Phantom\nPortable admin-only utility\nAll changes are executed by PowerShell operations\nOffline-first safety enforced.";
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
