using System.Text.Json;
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
    private string _serviceStatus = string.Empty;
    private string _policySummary = string.Empty;
    private string _policySource = $"Source: {UpdateModeOperationFactory.RegistryPolicyRootPath.Replace(":", string.Empty)} (Registry64, machine scope).";
    private string _policyExplanation = "Read-only snapshot. Preset buttons update these policy values and related Windows Update service behavior.";

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
        ApplyDefaultModeCommand = new AsyncRelayCommand(ct => ApplyModeByNameAsync("Default", ct));
        ApplySecurityModeCommand = new AsyncRelayCommand(ct => ApplyModeByNameAsync("Security", ct));
        ApplyDisableAllModeCommand = new AsyncRelayCommand(ct => ApplyModeByNameAsync("Disable All", ct));
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

    public string PolicySource
    {
        get => _policySource;
        set => SetProperty(ref _policySource, value);
    }

    public string PolicyExplanation
    {
        get => _policyExplanation;
        set => SetProperty(ref _policyExplanation, value);
    }

    public AsyncRelayCommand ApplyModeCommand { get; }
    public AsyncRelayCommand ApplyDefaultModeCommand { get; }
    public AsyncRelayCommand ApplySecurityModeCommand { get; }
    public AsyncRelayCommand ApplyDisableAllModeCommand { get; }
    public AsyncRelayCommand RestoreDefaultCommand { get; }
    public AsyncRelayCommand RefreshStatusCommand { get; }
    public AsyncRelayCommand ResetUpdateComponentsCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshStatusAsync(cancellationToken, echoQueryToConsole: false).ConfigureAwait(false);
    }

    private async Task ApplyModeAsync(CancellationToken cancellationToken)
    {
        await ApplyModeByNameAsync(SelectedMode, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyModeByNameAsync(string mode, CancellationToken cancellationToken)
    {
        mode = UpdateModeOperationFactory.NormalizeMode(mode);
        SelectedMode = mode;

        if (string.Equals(mode, "Disable All", StringComparison.OrdinalIgnoreCase))
        {
            var confirmed = await ConfirmDisableAllModeAsync(cancellationToken).ConfigureAwait(false);
            if (!confirmed)
            {
                _console.Publish("Info", "Disable All mode cancelled.");
                return;
            }
        }

        var operation = UpdateModeOperationFactory.BuildModeOperation(mode);

        await ExecuteUpdateOperationAsync(
                operation,
                cancellationToken,
                forceDangerous: string.Equals(mode, "Disable All", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken, echoQueryToConsole: false).ConfigureAwait(false);
    }

    private static string BuildPolicySummary(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return "No explicit Windows Update policy values are set.";
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var feature = GetPolicyValue(root, "DeferFeatureUpdatesPeriodInDays");
            var quality = GetPolicyValue(root, "DeferQualityUpdatesPeriodInDays");
            var noAuto = GetPolicyValue(root, "NoAutoUpdate");

            return
                "Current machine policy values (read-only):" + Environment.NewLine +
                $"- DeferFeatureUpdatesPeriodInDays: {feature}" + Environment.NewLine +
                $"- DeferQualityUpdatesPeriodInDays: {quality}" + Environment.NewLine +
                $"- AU\\NoAutoUpdate: {noAuto}" + Environment.NewLine + Environment.NewLine +
                "How this is used:" + Environment.NewLine +
                "- Default Settings clears policy values and restores update services." + Environment.NewLine +
                "- Security Settings writes 365/4 defer values and keeps automatic updates enabled." + Environment.NewLine +
                "- Disable All Updates sets NoAutoUpdate=1 and disables update services.";
        }
        catch
        {
            return $"Policy query output:{Environment.NewLine}{rawJson.Trim()}";
        }
    }

    private static string GetPolicyValue(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "Not set";
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString() ?? "Not set",
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => value.GetRawText()
        };
    }

    private async Task RestoreDefaultAsync(CancellationToken cancellationToken)
    {
        await ExecuteUpdateOperationAsync(UpdateModeOperationFactory.BuildDefaultModeOperation(), cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken, echoQueryToConsole: false).ConfigureAwait(false);
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken, bool echoQueryToConsole)
    {
        var service = await _queryService.InvokeAsync("Get-Service wuauserv | Select-Object -ExpandProperty Status", cancellationToken, echoToConsole: echoQueryToConsole).ConfigureAwait(false);
        var bits = await _queryService.InvokeAsync("Get-Service bits | Select-Object -ExpandProperty Status", cancellationToken, echoToConsole: echoQueryToConsole).ConfigureAwait(false);
        var policy = await _queryService.InvokeAsync(
                UpdateModeOperationFactory.BuildPolicySummaryQueryScript(),
                cancellationToken,
                echoToConsole: echoQueryToConsole)
            .ConfigureAwait(false);

        ServiceStatus = $"wuauserv: {service.Stdout.Trim()} | BITS: {bits.Stdout.Trim()}";
        PolicySummary = BuildPolicySummary(policy.Stdout);
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
                    Script = "$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'; $services=@('cryptsvc','bits','wuauserv'); Stop-Service -Name $services -Force -ErrorAction Stop; $softwareDistribution = Join-Path $env:SystemRoot 'SoftwareDistribution'; $catroot2 = Join-Path $env:SystemRoot 'System32\\catroot2'; if (Test-Path $softwareDistribution) { Rename-Item -Path $softwareDistribution -NewName (\"SoftwareDistribution.phantom.bak.\" + $stamp) -ErrorAction Stop }; if (Test-Path $catroot2) { Rename-Item -Path $catroot2 -NewName (\"catroot2.phantom.bak.\" + $stamp) -ErrorAction Stop }; New-Item -Path $softwareDistribution -ItemType Directory -Force | Out-Null; New-Item -Path $catroot2 -ItemType Directory -Force | Out-Null; $delay=1; foreach($svc in $services){ $started=$false; for($attempt=1;$attempt -le 5;$attempt++){ try { Start-Service -Name $svc -ErrorAction Stop; if((Get-Service -Name $svc -ErrorAction Stop).Status -eq 'Running'){ $started=$true; break } } catch {} Start-Sleep -Seconds $delay; $delay=[Math]::Min($delay*2,8) }; $final=(Get-Service -Name $svc -ErrorAction SilentlyContinue).Status; Write-Output \"$svc final state: $final\"; if(-not $started){ throw \"Service '$svc' failed to reach Running state.\" } }"
                }
            ]
        };

        await ExecuteUpdateOperationAsync(operation, cancellationToken, forceDangerous: true).ConfigureAwait(false);
    }

    private async Task<bool> ConfirmDisableAllModeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var first = await _promptService
            .ConfirmDangerousAsync("Disable ALL updates stops and disables Windows Update services. Enter Y to continue.")
            .ConfigureAwait(false);

        if (!first)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await _promptService
            .ConfirmDangerousAsync("Final confirmation: this can leave the system unpatched. Enter Y again to apply Disable ALL mode.")
            .ConfigureAwait(false);
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

}
