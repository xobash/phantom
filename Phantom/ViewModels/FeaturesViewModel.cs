using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class FeaturesViewModel : ObservableObject, ISectionViewModel, IDisposable
{
    private static readonly Regex ProgressPercentRegex = new(@"(?<!\d)(100|[1-9]?\d(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    private readonly DefinitionCatalogService _catalogService;
    private readonly OperationEngine _operationEngine;
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly IUserPromptService _promptService;
    private readonly ConsoleStreamService _console;
    private readonly PowerShellQueryService _queryService;
    private readonly IPowerShellRunner _runner;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly EventHandler<PowerShellOutputEvent> _consoleMessageReceivedHandler;
    private readonly object _repairOutputGate = new();

    private string _search = string.Empty;
    private bool _fastStartupEnabled;
    private bool _hibernationEnabled;
    private bool _driveOptimizationEnabled;
    private bool _storageSenseEnabled;
    private string _hibernationPercent = "0%";
    private bool _cleanupExpanded;
    private bool _repairExpanded;
    private bool _optionalFeaturesExpanded;
    private bool _repairInProgress;
    private int _repairProgressPercent;
    private string _repairProgressMessage = string.Empty;
    private string _repairTransientOutput = string.Empty;
    private bool _loadingSystemState;
    private bool _disposed;
    private CancellationTokenSource? _repairOutputResetCts;
    private readonly SemaphoreSlim _toggleSemaphore = new(1, 1);

    public FeaturesViewModel(
        DefinitionCatalogService catalogService,
        OperationEngine operationEngine,
        ExecutionCoordinator executionCoordinator,
        IUserPromptService promptService,
        ConsoleStreamService console,
        PowerShellQueryService queryService,
        IPowerShellRunner runner,
        Func<AppSettings> settingsAccessor)
    {
        _catalogService = catalogService;
        _operationEngine = operationEngine;
        _executionCoordinator = executionCoordinator;
        _promptService = promptService;
        _console = console;
        _queryService = queryService;
        _runner = runner;
        _settingsAccessor = settingsAccessor;

        Features = new ObservableCollection<FeatureDefinition>();
        FeaturesView = CollectionViewSource.GetDefaultView(Features);
        FeaturesView.Filter = FilterFeature;

        RefreshStatusCommand = new AsyncRelayCommand(ct => RefreshStatusAsync(ct, echoQueryToConsole: true));
        ApplySelectedCommand = new AsyncRelayCommand(ApplySelectedAsync);
        UndoSelectedCommand = new AsyncRelayCommand(UndoSelectedAsync);
        OpenDriveOptimizationCommand = new AsyncRelayCommand(OpenDriveOptimizationAsync);
        OpenStorageSenseSettingsCommand = new AsyncRelayCommand(OpenStorageSenseSettingsAsync);
        CleanupStorageCommand = new AsyncRelayCommand(CleanupStorageAsync);
        CleanupFileExplorerCommand = new AsyncRelayCommand(CleanupFileExplorerAsync);
        CleanupStoreCommand = new AsyncRelayCommand(CleanupStoreAsync);
        CleanupNetworkCommand = new AsyncRelayCommand(CleanupNetworkAsync);
        CleanupRestorePointsCommand = new AsyncRelayCommand(CleanupRestorePointsAsync);
        RepairDismCommand = new AsyncRelayCommand(RepairDismAsync);
        RepairSfcCommand = new AsyncRelayCommand(RepairSfcAsync);
        RepairChkdskCommand = new AsyncRelayCommand(RepairChkdskAsync);
        RunMemoryDiagnosticCommand = new AsyncRelayCommand(RunMemoryDiagnosticAsync);
        RestartGraphicsDriverCommand = new AsyncRelayCommand(RestartGraphicsDriverAsync);
        RebuildIconCacheCommand = new AsyncRelayCommand(RebuildIconCacheAsync);

        _consoleMessageReceivedHandler = (_, evt) => HandleConsoleMessage(evt);
        _console.MessageReceived += _consoleMessageReceivedHandler;

        OptionalFeaturesExpanded = true;
    }

    public string Title => "Features";

    public ObservableCollection<FeatureDefinition> Features { get; }
    public ICollectionView FeaturesView { get; }

    public AsyncRelayCommand RefreshStatusCommand { get; }
    public AsyncRelayCommand ApplySelectedCommand { get; }
    public AsyncRelayCommand UndoSelectedCommand { get; }
    public AsyncRelayCommand OpenDriveOptimizationCommand { get; }
    public AsyncRelayCommand OpenStorageSenseSettingsCommand { get; }
    public AsyncRelayCommand CleanupStorageCommand { get; }
    public AsyncRelayCommand CleanupFileExplorerCommand { get; }
    public AsyncRelayCommand CleanupStoreCommand { get; }
    public AsyncRelayCommand CleanupNetworkCommand { get; }
    public AsyncRelayCommand CleanupRestorePointsCommand { get; }
    public AsyncRelayCommand RepairDismCommand { get; }
    public AsyncRelayCommand RepairSfcCommand { get; }
    public AsyncRelayCommand RepairChkdskCommand { get; }
    public AsyncRelayCommand RunMemoryDiagnosticCommand { get; }
    public AsyncRelayCommand RestartGraphicsDriverCommand { get; }
    public AsyncRelayCommand RebuildIconCacheCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                FeaturesView.Refresh();
            }
        }
    }

    public bool FastStartupEnabled
    {
        get => _fastStartupEnabled;
        set
        {
            if (!SetProperty(ref _fastStartupEnabled, value) || _loadingSystemState)
            {
                return;
            }

            QueueSystemToggle("Fast startup", ct => SetFastStartupAsync(value, ct));
        }
    }

    public bool HibernationEnabled
    {
        get => _hibernationEnabled;
        set
        {
            if (!SetProperty(ref _hibernationEnabled, value) || _loadingSystemState)
            {
                return;
            }

            QueueSystemToggle("Hibernation", ct => SetHibernationAsync(value, ct));
        }
    }

    public bool DriveOptimizationEnabled
    {
        get => _driveOptimizationEnabled;
        set
        {
            if (!SetProperty(ref _driveOptimizationEnabled, value) || _loadingSystemState)
            {
                return;
            }

            QueueSystemToggle("Drive optimization", ct => SetDriveOptimizationAsync(value, ct));
        }
    }

    public bool StorageSenseEnabled
    {
        get => _storageSenseEnabled;
        set
        {
            if (!SetProperty(ref _storageSenseEnabled, value) || _loadingSystemState)
            {
                return;
            }

            QueueSystemToggle("Storage Sense", ct => SetStorageSenseAsync(value, ct));
        }
    }

    public string HibernationPercent
    {
        get => _hibernationPercent;
        set => SetProperty(ref _hibernationPercent, value);
    }

    public bool CleanupExpanded
    {
        get => _cleanupExpanded;
        set => SetProperty(ref _cleanupExpanded, value);
    }

    public bool RepairExpanded
    {
        get => _repairExpanded;
        set => SetProperty(ref _repairExpanded, value);
    }

    public bool OptionalFeaturesExpanded
    {
        get => _optionalFeaturesExpanded;
        set => SetProperty(ref _optionalFeaturesExpanded, value);
    }

    public bool RepairInProgress
    {
        get => _repairInProgress;
        set => SetProperty(ref _repairInProgress, value);
    }

    public string RepairProgressMessage
    {
        get => _repairProgressMessage;
        set => SetProperty(ref _repairProgressMessage, value);
    }

    public int RepairProgressPercent
    {
        get => _repairProgressPercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _repairProgressPercent, clamped))
            {
                return;
            }

            Notify(nameof(RepairProgressPercentLabel));
        }
    }

    public string RepairProgressPercentLabel => $"{RepairProgressPercent}%";

    public string RepairTransientOutput
    {
        get => _repairTransientOutput;
        set => SetProperty(ref _repairTransientOutput, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var features = await _catalogService.LoadFeaturesAsync(cancellationToken).ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Features.Clear();
            foreach (var feature in features)
            {
                Features.Add(feature);
            }
        });

        await RefreshStatusAsync(cancellationToken, echoQueryToConsole: false).ConfigureAwait(false);
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken, bool echoQueryToConsole)
    {
        await RefreshSystemStateAsync(cancellationToken, echoQueryToConsole).ConfigureAwait(false);
        await RefreshOptionalFeatureStatesAsync(cancellationToken, echoQueryToConsole).ConfigureAwait(false);
    }

    private async Task ApplySelectedAsync(CancellationToken cancellationToken)
    {
        var operations = new List<OperationDefinition>();
        foreach (var feature in Features.Where(f => f.Selected))
        {
            try
            {
                operations.Add(BuildEnableFeatureOperation(feature));
            }
            catch (ArgumentException ex)
            {
                _console.Publish("Error", ex.Message);
            }
        }

        await RunOperationsAsync(operations, undo: false, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken, echoQueryToConsole: false).ConfigureAwait(false);
    }

    private async Task UndoSelectedAsync(CancellationToken cancellationToken)
    {
        var operations = new List<OperationDefinition>();
        foreach (var feature in Features.Where(f => f.Selected))
        {
            try
            {
                operations.Add(BuildDisableFeatureOperation(feature));
            }
            catch (ArgumentException ex)
            {
                _console.Publish("Error", ex.Message);
            }
        }

        await RunOperationsAsync(operations, undo: false, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken, echoQueryToConsole: false).ConfigureAwait(false);
    }

    private async Task RunOperationsAsync(IReadOnlyList<OperationDefinition> operations, bool undo, CancellationToken externalToken)
    {
        if (operations.Count == 0)
        {
            _console.Publish("Info", "No features selected.");
            return;
        }

        CancellationToken token;
        try
        {
            token = _executionCoordinator.Begin();
        }
        catch (InvalidOperationException ex)
        {
            _console.Publish("Warning", ex.Message);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, externalToken);

        try
        {
            var precheck = await _operationEngine.RunBatchPrecheckAsync(operations, linked.Token).ConfigureAwait(false);
            if (!precheck.IsSuccess)
            {
                _console.Publish("Error", precheck.Message);
                return;
            }

            var batch = await _operationEngine.ExecuteBatchAsync(new OperationRequest
            {
                Operations = operations,
                Undo = undo,
                DryRun = false,
                EnableDestructiveOperations = _settingsAccessor().EnableDestructiveOperations,
                ForceDangerous = false,
                ConfirmDangerousAsync = _promptService.ConfirmDangerousAsync
            }, linked.Token).ConfigureAwait(false);

            foreach (var op in batch.Results)
            {
                _console.Publish(op.Success ? "Info" : "Error", $"{op.OperationId}: {op.Message}");
            }

            if (batch.RequiresReboot)
            {
                var rebootNow = await _promptService.PromptRebootAsync().ConfigureAwait(false);
                if (rebootNow)
                {
                    await _runner.ExecuteAsync(new PowerShellExecutionRequest
                    {
                        OperationId = "system.reboot",
                        StepName = "restart",
                        Script = "Restart-Computer -Force",
                        DryRun = false
                    }, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    _console.Publish("Info", "Reboot postponed by user choice.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", "Feature operation cancelled.");
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Feature operation failed: {ex.Message}");
        }
        finally
        {
            _executionCoordinator.Complete();
        }
    }

    private void QueueSystemToggle(string toggleName, Func<CancellationToken, Task> action)
    {
        _ = QueueSystemToggleAsync(toggleName, action);
    }

    private async Task QueueSystemToggleAsync(string toggleName, Func<CancellationToken, Task> action)
    {
        await _toggleSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await action(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"{toggleName} toggle failed: {ex.Message}");
            await RefreshSystemStateAsync(CancellationToken.None, echoQueryToConsole: false).ConfigureAwait(false);
        }
        finally
        {
            _toggleSemaphore.Release();
        }
    }

    private async Task SetFastStartupAsync(bool enabled, CancellationToken cancellationToken)
    {
        var script = enabled
            ? "powercfg /hibernate on | Out-Null; New-Item -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power' -Force | Out-Null; Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power' -Name HiberbootEnabled -Type DWord -Value 1"
            : "Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power' -Name HiberbootEnabled -Type DWord -Value 0";
        await ExecuteScriptAsync("feature.fast-startup", "toggle", script, cancellationToken, refreshSystemState: true).ConfigureAwait(false);
    }

    private async Task SetHibernationAsync(bool enabled, CancellationToken cancellationToken)
    {
        var script = enabled
            ? "powercfg /hibernate on"
            : "powercfg /hibernate off";
        await ExecuteScriptAsync("feature.hibernation", "toggle", script, cancellationToken, refreshSystemState: true).ConfigureAwait(false);
    }

    private async Task SetDriveOptimizationAsync(bool enabled, CancellationToken cancellationToken)
    {
        var script = enabled
            ? "Enable-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Defrag\\' -TaskName 'ScheduledDefrag' -ErrorAction Stop"
            : "Disable-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Defrag\\' -TaskName 'ScheduledDefrag' -ErrorAction Stop";
        await ExecuteScriptAsync("feature.drive-optimization", "toggle", script, cancellationToken, refreshSystemState: true).ConfigureAwait(false);
    }

    private async Task SetStorageSenseAsync(bool enabled, CancellationToken cancellationToken)
    {
        var value = enabled ? 1 : 0;
        var script = $"New-Item -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\StorageSense\\Parameters\\StoragePolicy' -Force | Out-Null; Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\StorageSense\\Parameters\\StoragePolicy' -Name 01 -Type DWord -Value {value}";
        await ExecuteScriptAsync("feature.storage-sense", "toggle", script, cancellationToken, refreshSystemState: true).ConfigureAwait(false);
    }

    private Task OpenDriveOptimizationAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.drive-optimization", "open-ui", "Start-Process -FilePath 'dfrgui.exe'", cancellationToken);

    private Task OpenStorageSenseSettingsAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.storage-sense", "open-settings", "Start-Process -FilePath 'explorer.exe' -ArgumentList 'ms-settings:storagesense'", cancellationToken);

    private Task CleanupStorageAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.storage", "clean", "Start-Process -FilePath \"$env:SystemRoot\\System32\\cleanmgr.exe\" -ArgumentList '/VERYLOWDISK'", cancellationToken);

    private Task CleanupFileExplorerAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.file-explorer", "clean", "Remove-Item \"$env:APPDATA\\Microsoft\\Windows\\Recent\\*\" -Recurse -Force -ErrorAction Stop; Remove-Item \"$env:LOCALAPPDATA\\Microsoft\\Windows\\Explorer\\thumbcache_*\" -Force -ErrorAction Stop", cancellationToken);

    private Task CleanupStoreAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.store", "reset", "Start-Process -FilePath 'wsreset.exe'", cancellationToken);

    private Task CleanupNetworkAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.network", "reset", "ipconfig /flushdns | Out-Null; netsh winsock reset | Out-Null", cancellationToken);

    private Task CleanupRestorePointsAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.system-restore", "clean", "vssadmin Delete Shadows /For=C: /Oldest /Quiet", cancellationToken);

    private async Task RepairDismAsync(CancellationToken cancellationToken)
    {
        await RunRepairAsync(
            "feature.repair.dism",
            "repair",
            "DISM restore health is running...",
            "1 of 1: Repairing component store using DISM 0%",
            "DISM /Online /Cleanup-Image /RestoreHealth",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RepairSfcAsync(CancellationToken cancellationToken)
    {
        await RunRepairAsync(
            "feature.repair.sfc",
            "repair",
            "System file checker is running...",
            "1 of 1: Checking system files using SFC 0%",
            "sfc /scannow",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RepairChkdskAsync(CancellationToken cancellationToken)
    {
        await RunRepairAsync(
            "feature.repair.chkdsk",
            "repair",
            "CHKDSK scan is running...",
            "1 of 1: Scanning volume using CHKDSK 0%",
            "chkdsk C: /scan",
            cancellationToken).ConfigureAwait(false);
    }

    private Task RunMemoryDiagnosticAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.memory-diagnostic", "launch", "Start-Process -FilePath \"$env:SystemRoot\\System32\\mdsched.exe\"", cancellationToken);

    private Task RestartGraphicsDriverAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.graphics-driver", "restart", "Get-Process -Name 'dwm' -ErrorAction Stop | Stop-Process -Force -ErrorAction Stop", cancellationToken);

    private Task RebuildIconCacheAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.icons-cache", "rebuild", "Stop-Process -Name explorer -Force -ErrorAction Stop; Remove-Item \"$env:LOCALAPPDATA\\Microsoft\\Windows\\Explorer\\iconcache*\" -Force -ErrorAction Stop; Start-Process explorer.exe", cancellationToken);

    private async Task RefreshSystemStateAsync(CancellationToken cancellationToken, bool echoQueryToConsole)
    {
        const string script = @"
$powerSession='HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
$power='HKLM:\SYSTEM\CurrentControlSet\Control\Power'
$storage='HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy'
$fast=(Get-ItemProperty -Path $powerSession -Name HiberbootEnabled -ErrorAction Stop).HiberbootEnabled
$hib=(Get-ItemProperty -Path $power -Name HibernateEnabled -ErrorAction Stop).HibernateEnabled
$hibPct=(Get-ItemProperty -Path $power -Name HiberFileSizePercent -ErrorAction Stop).HiberFileSizePercent
$storageSense=(Get-ItemProperty -Path $storage -Name 01 -ErrorAction Stop).'01'
$task=Get-ScheduledTask -TaskPath '\Microsoft\Windows\Defrag\' -TaskName 'ScheduledDefrag' -ErrorAction Stop

if ($null -eq $fast) { $fast = 0 }
if ($null -eq $hib) { $hib = 0 }
if ($null -eq $hibPct) { $hibPct = 0 }
if ($null -eq $storageSense) { $storageSense = 0 }

[PSCustomObject]@{
  FastStartup = [bool]([int]$fast -eq 1)
  Hibernation = [bool]([int]$hib -eq 1)
  HibernationPercent = [int]$hibPct
  DriveOptimization = if ($task) { [bool]$task.Settings.Enabled } else { $false }
  StorageSense = [bool]([int]$storageSense -eq 1)
} | ConvertTo-Json -Compress";

        var result = await _queryService.InvokeAsync(script, cancellationToken, echoToConsole: echoQueryToConsole).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
        {
            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                _console.Publish("Error", result.Stderr.Trim());
            }
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            var root = doc.RootElement;
            var fastStartup = root.TryGetProperty("FastStartup", out var fastProp) && fastProp.GetBoolean();
            var hibernation = root.TryGetProperty("Hibernation", out var hibProp) && hibProp.GetBoolean();
            var hibernationPercent = root.TryGetProperty("HibernationPercent", out var percentProp) ? percentProp.GetInt32() : 0;
            var driveOptimization = root.TryGetProperty("DriveOptimization", out var driveProp) && driveProp.GetBoolean();
            var storageSense = root.TryGetProperty("StorageSense", out var storageProp) && storageProp.GetBoolean();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _loadingSystemState = true;
                try
                {
                    FastStartupEnabled = fastStartup;
                    HibernationEnabled = hibernation;
                    HibernationPercent = $"{Math.Clamp(hibernationPercent, 0, 100)}%";
                    DriveOptimizationEnabled = driveOptimization;
                    StorageSenseEnabled = storageSense;
                }
                finally
                {
                    _loadingSystemState = false;
                }
            });
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Feature status parse failed: {ex.Message}");
        }
    }

    private async Task RefreshOptionalFeatureStatesAsync(CancellationToken cancellationToken, bool echoQueryToConsole)
    {
        List<FeatureDefinition> snapshot = new();
        await Application.Current.Dispatcher.InvokeAsync(() => snapshot = Features.ToList());

        var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in snapshot)
        {
            string script;
            try
            {
                script = BuildFeatureStateScript(feature);
            }
            catch (ArgumentException ex)
            {
                _console.Publish("Error", ex.Message);
                statuses[feature.Id] = "Invalid catalog entry";
                continue;
            }

            var result = await _queryService.InvokeAsync(script, cancellationToken, echoToConsole: echoQueryToConsole).ConfigureAwait(false);
            statuses[feature.Id] = result.ExitCode == 0 ? result.Stdout.Trim() : "Managed / Restricted";
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var feature in Features)
            {
                if (statuses.TryGetValue(feature.Id, out var status))
                {
                    feature.Status = status;
                }
            }

            FeaturesView.Refresh();
            Notify(nameof(Features));
        });
    }

    private async Task ExecuteScriptAsync(
        string operationId,
        string stepName,
        string script,
        CancellationToken cancellationToken,
        bool refreshSystemState = false,
        bool forceProcessMode = false)
    {
        CancellationToken coordinatorToken;
        try
        {
            coordinatorToken = _executionCoordinator.Begin();
        }
        catch (InvalidOperationException ex)
        {
            _console.Publish("Warning", ex.Message);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(coordinatorToken, cancellationToken);

        try
        {
            var result = await _runner.ExecuteAsync(new PowerShellExecutionRequest
            {
                OperationId = operationId,
                StepName = stepName,
                Script = script,
                DryRun = false,
                PreferProcessMode = forceProcessMode
            }, linked.Token).ConfigureAwait(false);

            if (!result.Success)
            {
                _console.Publish("Error", $"{operationId}: command failed.");
            }

            if (refreshSystemState)
            {
                await RefreshSystemStateAsync(linked.Token, echoQueryToConsole: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", $"{operationId}: operation cancelled.");
        }
        finally
        {
            _executionCoordinator.Complete();
        }
    }

    private async Task RunRepairAsync(
        string operationId,
        string stepName,
        string progressMessage,
        string consoleProgressMessage,
        string script,
        CancellationToken cancellationToken)
    {
        await SetRepairStateAsync(true, progressMessage).ConfigureAwait(false);
        _console.Publish("Progress", consoleProgressMessage);
        try
        {
            await ExecuteScriptAsync(operationId, stepName, script, cancellationToken, forceProcessMode: true).ConfigureAwait(false);
        }
        finally
        {
            await SetRepairStateAsync(false, string.Empty).ConfigureAwait(false);
        }
    }

    private void HandleConsoleMessage(PowerShellOutputEvent evt)
    {
        if (!RepairInProgress || !IsRepairStream(evt.Stream))
        {
            return;
        }

        var line = evt.Text?.Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (TryExtractProgressPercent(line, out var percent))
        {
            _ = SetOnUiThreadAsync(() =>
            {
                RepairProgressPercent = percent;
                RepairProgressMessage = line;
            });
            return;
        }

        _ = ShowRepairTransientOutputAsync(line);
    }

    private async Task ShowRepairTransientOutputAsync(string text)
    {
        CancellationToken clearToken;
        lock (_repairOutputGate)
        {
            _repairOutputResetCts?.Cancel();
            _repairOutputResetCts?.Dispose();
            _repairOutputResetCts = new CancellationTokenSource();
            clearToken = _repairOutputResetCts.Token;
        }

        await SetOnUiThreadAsync(() => RepairTransientOutput = text).ConfigureAwait(false);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), clearToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SetOnUiThreadAsync(() =>
        {
            if (RepairInProgress)
            {
                RepairTransientOutput = string.Empty;
            }
        }).ConfigureAwait(false);
    }

    private static bool IsRepairStream(string stream)
    {
        return stream.Equals("Progress", StringComparison.OrdinalIgnoreCase) ||
               stream.Equals("Output", StringComparison.OrdinalIgnoreCase) ||
               stream.Equals("Warning", StringComparison.OrdinalIgnoreCase) ||
               stream.Equals("Error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractProgressPercent(string text, out int percent)
    {
        percent = 0;
        var match = ProgressPercentRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        percent = Math.Clamp((int)Math.Round(parsed, MidpointRounding.AwayFromZero), 0, 100);
        return true;
    }

    private static Task SetOnUiThreadAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private Task SetRepairStateAsync(bool running, string message)
        => SetOnUiThreadAsync(() =>
        {
            RepairInProgress = running;
            RepairProgressMessage = message;
            RepairProgressPercent = running ? 0 : 100;
            if (!running)
            {
                RepairTransientOutput = string.Empty;
            }
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _console.MessageReceived -= _consoleMessageReceivedHandler;
        lock (_repairOutputGate)
        {
            _repairOutputResetCts?.Cancel();
            _repairOutputResetCts?.Dispose();
            _repairOutputResetCts = null;
        }

        _toggleSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool FilterFeature(object obj)
    {
        if (obj is not FeatureDefinition feature)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return feature.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               feature.Description.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               feature.FeatureName.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private static OperationDefinition BuildEnableFeatureOperation(FeatureDefinition feature)
    {
        return new OperationDefinition
        {
            Id = $"feature.{feature.Id}",
            Title = $"Enable {feature.Name}",
            Description = feature.Description,
            RiskTier = RiskTier.Advanced,
            Reversible = true,
            RequiresReboot = true,
            Compatibility = feature.Compatibility ?? Array.Empty<string>(),
            RunScripts = [new PowerShellStep { Name = "enable", Script = BuildEnableFeatureScript(feature) }],
            UndoScripts = [new PowerShellStep { Name = "disable", Script = BuildDisableFeatureScript(feature) }]
        };
    }

    private static OperationDefinition BuildDisableFeatureOperation(FeatureDefinition feature)
    {
        return new OperationDefinition
        {
            Id = $"feature.{feature.Id}",
            Title = $"Disable {feature.Name}",
            Description = feature.Description,
            RiskTier = RiskTier.Advanced,
            Reversible = true,
            RequiresReboot = true,
            Compatibility = feature.Compatibility ?? Array.Empty<string>(),
            RunScripts = [new PowerShellStep { Name = "disable", Script = BuildDisableFeatureScript(feature) }],
            UndoScripts = [new PowerShellStep { Name = "enable", Script = BuildEnableFeatureScript(feature) }]
        };
    }

    private static string BuildEnableFeatureScript(FeatureDefinition feature)
    {
        return $"Enable-WindowsOptionalFeature -Online -FeatureName {GetFeatureNameLiteral(feature)} -All -NoRestart -ErrorAction Stop";
    }

    private static string BuildDisableFeatureScript(FeatureDefinition feature)
    {
        return $"Disable-WindowsOptionalFeature -Online -FeatureName {GetFeatureNameLiteral(feature)} -NoRestart -ErrorAction Stop";
    }

    private static string BuildFeatureStateScript(FeatureDefinition feature)
    {
        return $"$f=Get-WindowsOptionalFeature -Online -FeatureName {GetFeatureNameLiteral(feature)} -ErrorAction Stop; if($f){{$f.State}} else {{'Unknown'}}";
    }

    private static string GetFeatureNameLiteral(FeatureDefinition feature)
    {
        var safeFeatureName = PowerShellInputSanitizer.EnsureFeatureName(feature.FeatureName, $"feature '{feature.Id}'");
        return PowerShellInputSanitizer.ToSingleQuotedLiteral(safeFeatureName);
    }
}
