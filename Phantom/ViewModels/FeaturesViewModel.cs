using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class FeaturesViewModel : ObservableObject, ISectionViewModel
{
    private readonly DefinitionCatalogService _catalogService;
    private readonly OperationEngine _operationEngine;
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly IUserPromptService _promptService;
    private readonly ConsoleStreamService _console;
    private readonly PowerShellQueryService _queryService;
    private readonly IPowerShellRunner _runner;
    private readonly Func<AppSettings> _settingsAccessor;

    private string _search = string.Empty;
    private bool _fastStartupEnabled;
    private bool _hibernationEnabled;
    private bool _driveOptimizationEnabled;
    private bool _storageSenseEnabled;
    private string _hibernationPercent = "0%";
    private bool _cleanupExpanded;
    private bool _repairExpanded;
    private bool _optionalFeaturesExpanded;
    private bool _loadingSystemState;

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

        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);
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

            _ = SetFastStartupAsync(value);
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

            _ = SetHibernationAsync(value);
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

            _ = SetDriveOptimizationAsync(value);
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

            _ = SetStorageSenseAsync(value);
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

        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        await RefreshSystemStateAsync(cancellationToken).ConfigureAwait(false);
        await RefreshOptionalFeatureStatesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplySelectedAsync(CancellationToken cancellationToken)
    {
        var operations = Features
            .Where(f => f.Selected)
            .Select(f => new OperationDefinition
            {
                Id = $"feature.{f.Id}",
                Title = $"Enable {f.Name}",
                Description = f.Description,
                RiskTier = RiskTier.Advanced,
                Reversible = true,
                RequiresReboot = true,
                RunScripts = [new PowerShellStep { Name = "enable", Script = $"Enable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -All -NoRestart -ErrorAction Stop" }],
                UndoScripts = [new PowerShellStep { Name = "disable", Script = $"Disable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -NoRestart -ErrorAction Stop" }]
            })
            .ToList();

        await RunOperationsAsync(operations, undo: false, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UndoSelectedAsync(CancellationToken cancellationToken)
    {
        var operations = Features
            .Where(f => f.Selected)
            .Select(f => new OperationDefinition
            {
                Id = $"feature.{f.Id}",
                Title = $"Disable {f.Name}",
                Description = f.Description,
                RiskTier = RiskTier.Advanced,
                Reversible = true,
                RequiresReboot = true,
                RunScripts = [new PowerShellStep { Name = "disable", Script = $"Disable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -NoRestart -ErrorAction Stop" }],
                UndoScripts = [new PowerShellStep { Name = "enable", Script = $"Enable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -All -NoRestart -ErrorAction Stop" }]
            })
            .ToList();

        await RunOperationsAsync(operations, undo: false, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task SetFastStartupAsync(bool enabled)
    {
        var script = enabled
            ? "powercfg /hibernate on | Out-Null; New-Item -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power' -Force | Out-Null; Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power' -Name HiberbootEnabled -Type DWord -Value 1"
            : "Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power' -Name HiberbootEnabled -Type DWord -Value 0";
        await ExecuteScriptAsync("feature.fast-startup", "toggle", script, CancellationToken.None, refreshSystemState: true).ConfigureAwait(false);
    }

    private async Task SetHibernationAsync(bool enabled)
    {
        var script = enabled
            ? "powercfg /hibernate on"
            : "powercfg /hibernate off";
        await ExecuteScriptAsync("feature.hibernation", "toggle", script, CancellationToken.None, refreshSystemState: true).ConfigureAwait(false);
    }

    private async Task SetDriveOptimizationAsync(bool enabled)
    {
        var script = enabled
            ? "Enable-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Defrag\\' -TaskName 'ScheduledDefrag' -ErrorAction Stop"
            : "Disable-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Defrag\\' -TaskName 'ScheduledDefrag' -ErrorAction Stop";
        await ExecuteScriptAsync("feature.drive-optimization", "toggle", script, CancellationToken.None, refreshSystemState: true).ConfigureAwait(false);
    }

    private async Task SetStorageSenseAsync(bool enabled)
    {
        var value = enabled ? 1 : 0;
        var script = $"New-Item -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\StorageSense\\Parameters\\StoragePolicy' -Force | Out-Null; Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\StorageSense\\Parameters\\StoragePolicy' -Name 01 -Type DWord -Value {value}";
        await ExecuteScriptAsync("feature.storage-sense", "toggle", script, CancellationToken.None, refreshSystemState: true).ConfigureAwait(false);
    }

    private Task OpenDriveOptimizationAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.drive-optimization", "open-ui", "Start-Process -FilePath 'dfrgui.exe'", cancellationToken);

    private Task OpenStorageSenseSettingsAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.storage-sense", "open-settings", "Start-Process -FilePath 'explorer.exe' -ArgumentList 'ms-settings:storagesense'", cancellationToken);

    private Task CleanupStorageAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.storage", "clean", "Start-Process -FilePath \"$env:SystemRoot\\System32\\cleanmgr.exe\" -ArgumentList '/VERYLOWDISK'", cancellationToken);

    private Task CleanupFileExplorerAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.file-explorer", "clean", "Remove-Item \"$env:APPDATA\\Microsoft\\Windows\\Recent\\*\" -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item \"$env:LOCALAPPDATA\\Microsoft\\Windows\\Explorer\\thumbcache_*\" -Force -ErrorAction SilentlyContinue", cancellationToken);

    private Task CleanupStoreAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.store", "reset", "Start-Process -FilePath 'wsreset.exe'", cancellationToken);

    private Task CleanupNetworkAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.network", "reset", "ipconfig /flushdns | Out-Null; netsh winsock reset | Out-Null", cancellationToken);

    private Task CleanupRestorePointsAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.cleanup.system-restore", "clean", "vssadmin Delete Shadows /For=C: /Oldest /Quiet", cancellationToken);

    private Task RepairDismAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.repair.dism", "repair", "DISM /Online /Cleanup-Image /RestoreHealth", cancellationToken);

    private Task RepairSfcAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.repair.sfc", "repair", "sfc /scannow", cancellationToken);

    private Task RepairChkdskAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.repair.chkdsk", "repair", "chkdsk C: /scan", cancellationToken);

    private Task RunMemoryDiagnosticAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.memory-diagnostic", "launch", "Start-Process -FilePath \"$env:SystemRoot\\System32\\mdsched.exe\"", cancellationToken);

    private Task RestartGraphicsDriverAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.graphics-driver", "restart", "Get-Process -Name 'dwm' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue", cancellationToken);

    private Task RebuildIconCacheAsync(CancellationToken cancellationToken)
        => ExecuteScriptAsync("feature.icons-cache", "rebuild", "Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue; Remove-Item \"$env:LOCALAPPDATA\\Microsoft\\Windows\\Explorer\\iconcache*\" -Force -ErrorAction SilentlyContinue; Start-Process explorer.exe", cancellationToken);

    private async Task RefreshSystemStateAsync(CancellationToken cancellationToken)
    {
        const string script = @"
$powerSession='HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
$power='HKLM:\SYSTEM\CurrentControlSet\Control\Power'
$storage='HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy'
$fast=(Get-ItemProperty -Path $powerSession -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
$hib=(Get-ItemProperty -Path $power -Name HibernateEnabled -ErrorAction SilentlyContinue).HibernateEnabled
$hibPct=(Get-ItemProperty -Path $power -Name HiberFileSizePercent -ErrorAction SilentlyContinue).HiberFileSizePercent
$storageSense=(Get-ItemProperty -Path $storage -Name 01 -ErrorAction SilentlyContinue).'01'
$task=Get-ScheduledTask -TaskPath '\Microsoft\Windows\Defrag\' -TaskName 'ScheduledDefrag' -ErrorAction SilentlyContinue

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

        var result = await _queryService.InvokeAsync(script, cancellationToken).ConfigureAwait(false);
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

    private async Task RefreshOptionalFeatureStatesAsync(CancellationToken cancellationToken)
    {
        List<FeatureDefinition> snapshot = new();
        await Application.Current.Dispatcher.InvokeAsync(() => snapshot = Features.ToList());

        var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in snapshot)
        {
            var script = $"$f=Get-WindowsOptionalFeature -Online -FeatureName '{feature.FeatureName}' -ErrorAction SilentlyContinue; if($f){{$f.State}} else {{'Unknown'}}";
            var result = await _queryService.InvokeAsync(script, cancellationToken).ConfigureAwait(false);
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

    private async Task ExecuteScriptAsync(string operationId, string stepName, string script, CancellationToken cancellationToken, bool refreshSystemState = false)
    {
        var result = await _runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = operationId,
            StepName = stepName,
            Script = script,
            DryRun = false
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            _console.Publish("Error", $"{operationId}: command failed.");
        }

        if (refreshSystemState)
        {
            await RefreshSystemStateAsync(cancellationToken).ConfigureAwait(false);
        }
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
}
