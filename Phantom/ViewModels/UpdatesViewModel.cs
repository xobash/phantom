using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class UpdatesViewModel : ObservableObject, ISectionViewModel
{
    private readonly OperationEngine _operationEngine;
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly IUserPromptService _promptService;
    private readonly ConsoleStreamService _console;
    private readonly PowerShellQueryService _queryService;
    private readonly Func<AppSettings> _settingsAccessor;

    private string _selectedMode = "Security";
    private string _serviceStatus = "Unknown";
    private string _policySummary = "Unknown";

    public UpdatesViewModel(
        OperationEngine operationEngine,
        ExecutionCoordinator executionCoordinator,
        IUserPromptService promptService,
        ConsoleStreamService console,
        PowerShellQueryService queryService,
        Func<AppSettings> settingsAccessor)
    {
        _operationEngine = operationEngine;
        _executionCoordinator = executionCoordinator;
        _promptService = promptService;
        _console = console;
        _queryService = queryService;
        _settingsAccessor = settingsAccessor;

        ApplyModeCommand = new AsyncRelayCommand(ApplyModeAsync);
        RestoreDefaultCommand = new AsyncRelayCommand(RestoreDefaultAsync);
        RefreshStatusCommand = new AsyncRelayCommand(ct => RefreshStatusAsync(ct, echoQueryToConsole: true));
        ResetUpdateComponentsCommand = new AsyncRelayCommand(ResetUpdateComponentsAsync);
    }

    public string Title => "Updates";

    public string[] Modes { get; } = ["Default", "Security", "Disable All"];

    public string SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value);
    }

    public string ServiceStatus
    {
        get => _serviceStatus;
        set => SetProperty(ref _serviceStatus, value);
    }

    public string PolicySummary
    {
        get => _policySummary;
        set => SetProperty(ref _policySummary, value);
    }

    public AsyncRelayCommand ApplyModeCommand { get; }
    public AsyncRelayCommand RestoreDefaultCommand { get; }
    public AsyncRelayCommand RefreshStatusCommand { get; }
    public AsyncRelayCommand ResetUpdateComponentsCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshStatusAsync(cancellationToken, echoQueryToConsole: false).ConfigureAwait(false);
    }

    private async Task ApplyModeAsync(CancellationToken cancellationToken)
    {
        OperationDefinition operation = SelectedMode switch
        {
            "Default" => BuildDefaultModeOperation(),
            "Security" => BuildSecurityModeOperation(),
            "Disable All" => BuildDisableAllModeOperation(),
            _ => BuildSecurityModeOperation()
        };

        await ExecuteUpdateOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken, echoQueryToConsole: false).ConfigureAwait(false);
    }

    private async Task RestoreDefaultAsync(CancellationToken cancellationToken)
    {
        await ExecuteUpdateOperationAsync(BuildDefaultModeOperation(), cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken, echoQueryToConsole: false).ConfigureAwait(false);
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken, bool echoQueryToConsole)
    {
        var service = await _queryService.InvokeAsync("Get-Service wuauserv | Select-Object -ExpandProperty Status", cancellationToken, echoToConsole: echoQueryToConsole).ConfigureAwait(false);
        var bits = await _queryService.InvokeAsync("Get-Service bits | Select-Object -ExpandProperty Status", cancellationToken, echoToConsole: echoQueryToConsole).ConfigureAwait(false);
        var policy = await _queryService.InvokeAsync("$p='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate'; if(Test-Path $p){Get-ItemProperty -Path $p | Select-Object DeferFeatureUpdatesPeriodInDays, DeferQualityUpdatesPeriodInDays | ConvertTo-Json -Compress}else{'{}'}", cancellationToken, echoToConsole: echoQueryToConsole).ConfigureAwait(false);

        ServiceStatus = $"wuauserv: {service.Stdout.Trim()} | BITS: {bits.Stdout.Trim()}";
        PolicySummary = string.IsNullOrWhiteSpace(policy.Stdout) ? "No explicit policy" : policy.Stdout.Trim();
    }

    private async Task ResetUpdateComponentsAsync(CancellationToken cancellationToken)
    {
        var confirmed = await _promptService
            .ConfirmDangerousAsync("Reset update components renames SoftwareDistribution and catroot2 caches before recreating them. Continue? (Y/N)")
            .ConfigureAwait(false);
        if (!confirmed)
        {
            _console.Publish("Info", "Reset update components cancelled.");
            return;
        }

        var operation = new OperationDefinition
        {
            Id = "updates.reset.components",
            Title = "Reset update components",
            Description = "Resets Windows Update components and cache.",
            RiskTier = RiskTier.Dangerous,
            Reversible = false,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "reset-components",
                    Script = "$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'; Stop-Service wuauserv,bits,cryptsvc -Force -ErrorAction Stop; $softwareDistribution = Join-Path $env:SystemRoot 'SoftwareDistribution'; $catroot2 = Join-Path $env:SystemRoot 'System32\\catroot2'; if (Test-Path $softwareDistribution) { Rename-Item -Path $softwareDistribution -NewName (\"SoftwareDistribution.phantom.bak.\" + $stamp) -ErrorAction Stop }; if (Test-Path $catroot2) { Rename-Item -Path $catroot2 -NewName (\"catroot2.phantom.bak.\" + $stamp) -ErrorAction Stop }; New-Item -Path $softwareDistribution -ItemType Directory -Force | Out-Null; New-Item -Path $catroot2 -ItemType Directory -Force | Out-Null; Start-Service cryptsvc,bits,wuauserv -ErrorAction Stop"
                }
            ]
        };

        await ExecuteUpdateOperationAsync(operation, cancellationToken, forceDangerous: true).ConfigureAwait(false);
    }

    private async Task ExecuteUpdateOperationAsync(OperationDefinition operation, CancellationToken externalToken, bool forceDangerous = false)
    {
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
            var precheck = await _operationEngine.RunBatchPrecheckAsync([operation], linked.Token).ConfigureAwait(false);
            if (!precheck.IsSuccess)
            {
                _console.Publish("Error", precheck.Message);
                return;
            }

            var batch = await _operationEngine.ExecuteBatchAsync(new OperationRequest
            {
                Operations = [operation],
                Undo = false,
                DryRun = false,
                EnableDestructiveOperations = _settingsAccessor().EnableDestructiveOperations,
                ForceDangerous = forceDangerous,
                ConfirmDangerousAsync = _promptService.ConfirmDangerousAsync
            }, linked.Token).ConfigureAwait(false);

            foreach (var result in batch.Results)
            {
                _console.Publish(result.Success ? "Info" : "Error", $"{result.OperationId}: {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", "Update operation cancelled.");
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Update operation failed: {ex.Message}");
        }
        finally
        {
            _executionCoordinator.Complete();
        }
    }

    private static OperationDefinition BuildDefaultModeOperation()
    {
        return new OperationDefinition
        {
            Id = "updates.mode.default",
            Title = "Restore default Windows Update behavior",
            Description = "Undo custom update policies and service configuration.",
            RiskTier = RiskTier.Basic,
            Reversible = true,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "restore-default",
                    Script = "$wu='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate'; $au='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'; if (Test-Path $au) { Remove-Item -Path $au -Recurse -Force -ErrorAction Stop }; if (Test-Path $wu) { Remove-Item -Path $wu -Recurse -Force -ErrorAction Stop }; Set-Service wuauserv -StartupType Manual; Set-Service bits -StartupType Manual; Start-Service wuauserv -ErrorAction Stop; Start-Service bits -ErrorAction Stop"
                }
            ],
            UndoScripts =
            [
                new PowerShellStep
                {
                    Name = "undo-to-security",
                    Script = "New-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Force | Out-Null; Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Name DeferFeatureUpdatesPeriodInDays -Value 365 -Type DWord; Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Name DeferQualityUpdatesPeriodInDays -Value 4 -Type DWord"
                }
            ]
        };
    }

    private static OperationDefinition BuildSecurityModeOperation()
    {
        return new OperationDefinition
        {
            Id = "updates.mode.security",
            Title = "Set Security mode",
            Description = "Delay feature updates by 365 days and quality updates by 4 days.",
            RiskTier = RiskTier.Basic,
            Reversible = true,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "apply-security",
                    Script = "New-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Force | Out-Null; New-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU' -Force | Out-Null; Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Name DeferFeatureUpdatesPeriodInDays -Value 365 -Type DWord; Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Name DeferQualityUpdatesPeriodInDays -Value 4 -Type DWord; Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU' -Name NoAutoUpdate -Value 0 -Type DWord"
                }
            ],
            UndoScripts =
            [
                new PowerShellStep
                {
                    Name = "undo-default",
                    Script = BuildDefaultModeOperation().RunScripts[0].Script
                }
            ]
        };
    }

    private static OperationDefinition BuildDisableAllModeOperation()
    {
        return new OperationDefinition
        {
            Id = "updates.mode.disableall",
            Title = "Disable ALL updates",
            Description = "Disables Windows Update services and policies.",
            RiskTier = RiskTier.Dangerous,
            Reversible = true,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "disable-updates",
                    Script = "New-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU' -Force | Out-Null; Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU' -Name NoAutoUpdate -Value 1 -Type DWord; Stop-Service wuauserv -Force -ErrorAction Stop; Stop-Service bits -Force -ErrorAction Stop; Set-Service wuauserv -StartupType Disabled; Set-Service bits -StartupType Disabled"
                }
            ],
            UndoScripts =
            [
                new PowerShellStep
                {
                    Name = "undo-default",
                    Script = BuildDefaultModeOperation().RunScripts[0].Script
                }
            ]
        };
    }
}
