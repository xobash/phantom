using System.Diagnostics;
using System.Security.Principal;
using Phantom.Models;

namespace Phantom.Services;

public sealed class OperationRequest
{
    public required IReadOnlyList<OperationDefinition> Operations { get; init; }
    public bool Undo { get; init; }
    public bool DryRun { get; init; }
    public bool EnableDestructiveOperations { get; init; }
    public bool ForceDangerous { get; init; }
    public bool InteractiveDangerousPrompt { get; init; } = true;
    public required Func<string, Task<bool>> ConfirmDangerousAsync { get; init; }
}

public sealed class OperationBatchResult
{
    public List<OperationExecutionResult> Results { get; } = new();
    public bool RequiresReboot => Results.Any(r => r.Success && r.RequiresReboot);
    public bool Success => Results.All(r => r.Success || r.Cancelled);
}

public sealed class OperationEngine
{
    private readonly IPowerShellRunner _runner;
    private readonly UndoStateStore _undoStore;
    private readonly NetworkGuardService _network;
    private readonly ConsoleStreamService _console;
    private readonly LogService _log;
    private readonly Func<AppSettings> _settingsAccessor;

    public OperationEngine(
        IPowerShellRunner runner,
        UndoStateStore undoStore,
        NetworkGuardService network,
        ConsoleStreamService console,
        LogService log,
        Func<AppSettings> settingsAccessor)
    {
        _runner = runner;
        _undoStore = undoStore;
        _network = network;
        _console = console;
        _log = log;
        _settingsAccessor = settingsAccessor;
    }

    public async Task<PrecheckResult> RunBatchPrecheckAsync(IEnumerable<OperationDefinition> operations, CancellationToken cancellationToken)
    {
        var operationList = operations.ToList();
        _console.Publish("Trace", $"RunBatchPrecheckAsync started. operationCount={operationList.Count}");

        if (!AdminGuard.IsAdministrator())
        {
            _console.Publish("Error", "Batch precheck failed: Administrator privileges are required.");
            return PrecheckResult.Failure("Administrator privileges are required.");
        }

        if (!WindowsSupportPolicy.IsCurrentOsSupported(out var osMessage))
        {
            _console.Publish("Error", $"Batch precheck failed: {osMessage}");
            return PrecheckResult.Failure(osMessage);
        }

        var currentOsVersion = WindowsSupportPolicy.GetCurrentOsVersion();
        _console.Publish(
            "Trace",
            $"OS validation passed. version={currentOsVersion}, arch={(Environment.Is64BitOperatingSystem ? "x64" : "x86")}, registryView={WindowsSupportPolicy.PreferredRegistryView}");
        await _log.WriteAsync(
                "Security",
                $"OS validation passed. version={currentOsVersion}, arch={(Environment.Is64BitOperatingSystem ? "x64" : "x86")}, registryView={WindowsSupportPolicy.PreferredRegistryView}",
                cancellationToken)
            .ConfigureAwait(false);

        var incompatible = operationList.Where(op => !IsOperationCompatible(op, currentOsVersion)).Select(op => op.Id).ToList();
        if (incompatible.Count > 0)
        {
            var summary = string.Join(", ", incompatible.Take(8));
            var message = $"Operation compatibility check failed for current OS version {currentOsVersion}. Unsupported operation(s): {summary}";
            _console.Publish("Error", message);
            return PrecheckResult.Failure(message);
        }

        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        var drive = new DriveInfo(systemDrive);
        if (drive.AvailableFreeSpace < 500L * 1024 * 1024)
        {
            _console.Publish("Error", "Batch precheck failed: Insufficient disk space (<500MB free).");
            return PrecheckResult.Failure("Insufficient disk space (<500MB free). Operation blocked.");
        }

        var requiresNetwork = operationList.SelectMany(o => o.RunScripts).Any(s => s.RequiresNetwork);
        if (requiresNetwork && !_network.IsOnline())
        {
            _console.Publish("Error", "Batch precheck failed: Offline and operation requires network.");
            return PrecheckResult.Failure("Offline detected. Network-required operations were blocked before execution.");
        }

        await _log.WriteAsync("Info", "Batch precheck passed.", cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "RunBatchPrecheckAsync passed.");
        return PrecheckResult.Success();
    }

    public async Task<OperationBatchResult> ExecuteBatchAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        _console.Publish("Trace", $"ExecuteBatchAsync started. operations={request.Operations.Count}, undo={request.Undo}, dryRun={request.DryRun}, forceDangerous={request.ForceDangerous}");
        var result = new OperationBatchResult();
        var undoState = await _undoStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var restorePointAttempted = false;
        var restorePointReady = true;
        var successfullyApplied = new List<OperationDefinition>();

        foreach (var operation in request.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _console.Publish("Trace", $"Operation start: {operation.Id} ({operation.Title})");

            var opResult = new OperationExecutionResult
            {
                OperationId = operation.Id,
                RequiresReboot = operation.RequiresReboot,
                Success = false,
                Message = "Not executed"
            };
            var currentStepName = "precheck";

            try
            {
                if (operation.Destructive && !request.EnableDestructiveOperations)
                {
                    opResult.Message = "Blocked by settings: destructive operations disabled.";
                    _console.Publish("Warning", opResult.Message);
                    result.Results.Add(opResult);
                    continue;
                }

                var scripts = request.Undo ? operation.UndoScripts : operation.RunScripts;
                var desiredApplied = !request.Undo;

                var currentState = await EvaluateOperationStateAsync(operation, cancellationToken).ConfigureAwait(false);
                if (IsDesiredState(currentState.State, desiredApplied))
                {
                    opResult.Success = true;
                    opResult.VerificationAttempted = currentState.DetectAvailable;
                    opResult.VerificationPassed = currentState.DetectSucceeded;
                    opResult.VerificationStatus = currentState.StatusText;
                    opResult.Message = desiredApplied
                        ? "Skipped: operation is already applied."
                        : "Skipped: operation is already not applied.";
                    result.Results.Add(opResult);
                    _console.Publish("Info", $"{operation.Id}: {opResult.Message}");
                    continue;
                }

                if (currentState.DetectAvailable && !currentState.DetectSucceeded)
                {
                    _console.Publish("Warning", $"{operation.Id}: detect script failed, continuing with execution.");
                }

                if (!request.DryRun && !request.Undo && !restorePointAttempted &&
                    _settingsAccessor().CreateRestorePointBeforeDangerousOperations &&
                    (operation.RiskTier == RiskTier.Dangerous || operation.Destructive))
                {
                    restorePointAttempted = true;
                    restorePointReady = await TryCreateRestorePointAsync(operation, cancellationToken).ConfigureAwait(false);
                }

                if (!request.DryRun && !request.Undo &&
                    _settingsAccessor().CreateRestorePointBeforeDangerousOperations &&
                    (operation.RiskTier == RiskTier.Dangerous || operation.Destructive) &&
                    !restorePointReady)
                {
                    opResult.Message = "Safety gate blocked operation: restore point creation failed.";
                    _console.Publish("Error", $"{operation.Title}: {opResult.Message}");
                    await _log.WriteAsync("Error", $"{operation.Id}: blocked because restore point creation failed.", cancellationToken).ConfigureAwait(false);
                    result.Results.Add(opResult);
                    await RollbackSuccessfulOperationsAsync(successfullyApplied, result, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (scripts.Any(s => s.RequiresNetwork) && !_network.IsOnline())
                {
                    opResult.Message = "Offline detected. Operation blocked before any changes.";
                    _console.Publish("Error", $"{operation.Title}: {opResult.Message}");
                    await _log.WriteAsync("Error", $"{operation.Id}: offline blocked", cancellationToken).ConfigureAwait(false);
                    result.Results.Add(opResult);
                    continue;
                }

                var effectiveRisk = operation.RiskTier;
                var captureFailed = false;

                if (!request.Undo && operation.Reversible && operation.StateCaptureScripts.Length > 0)
                {
                    var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var capture in operation.StateCaptureScripts)
                    {
                        var captureResult = await _runner.ExecuteAsync(new PowerShellExecutionRequest
                        {
                            OperationId = operation.Id,
                            StepName = $"capture:{capture.Name}",
                            Script = capture.Script,
                            DryRun = request.DryRun,
                            SkipSafetyBackup = true
                        }, cancellationToken).ConfigureAwait(false);

                        if (!captureResult.Success)
                        {
                            captureFailed = true;
                            break;
                        }

                        captured[capture.Name] = captureResult.CombinedOutput;
                    }

                    if (captureFailed)
                    {
                        opResult.CaptureFailed = true;
                        effectiveRisk = RiskTier.Dangerous;
                        _console.Publish("Warning", $"{operation.Title}: Undo state capture failed. Undo may not be possible.");
                    }
                    else if (captured.Count > 0)
                    {
                        undoState.OperationState[operation.Id] = captured;
                        undoState.UpdatedAt = DateTimeOffset.Now;
                        await _undoStore.SaveAsync(undoState, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (effectiveRisk == RiskTier.Dangerous || !operation.Reversible || opResult.CaptureFailed)
                {
                    if (!request.ForceDangerous)
                    {
                        var confirmed = await request.ConfirmDangerousAsync("ARE YOU SURE? (Y/N)").ConfigureAwait(false);
                        if (!confirmed)
                        {
                            opResult.Message = "User rejected dangerous operation.";
                            opResult.Cancelled = true;
                            result.Results.Add(opResult);
                            continue;
                        }
                    }
                    else
                    {
                        _console.Publish("Warning", $"Dangerous operation forced by configuration: {operation.Title}");
                    }
                }

                var allSucceeded = true;
                foreach (var step in scripts)
                {
                    currentStepName = step.Name;
                    _console.Publish("Trace", $"Operation step start: {operation.Id}/{step.Name}");
                    var stepResult = await _runner.ExecuteAsync(new PowerShellExecutionRequest
                    {
                        OperationId = operation.Id,
                        StepName = step.Name,
                        Script = step.Script,
                        DryRun = request.DryRun
                    }, cancellationToken).ConfigureAwait(false);

                    if (!stepResult.Success)
                    {
                        _console.Publish("Error", $"Operation step failed: {operation.Id}/{step.Name}");
                        allSucceeded = false;
                        opResult.Message = $"Step failed: {step.Name}";
                        break;
                    }

                    _console.Publish("Trace", $"Operation step completed: {operation.Id}/{step.Name}");
                }

                if (allSucceeded && !request.DryRun)
                {
                    var verification = await VerifyOperationStateAsync(operation, desiredApplied, cancellationToken).ConfigureAwait(false);
                    opResult.VerificationAttempted = verification.Attempted;
                    opResult.VerificationPassed = verification.Passed;
                    opResult.VerificationStatus = verification.StatusText;

                    if (verification.Attempted && !verification.Passed)
                    {
                        allSucceeded = false;
                        opResult.Message = $"Verification failed: expected {(desiredApplied ? "Applied" : "Not Applied")} state but detect returned '{verification.StatusText}'.";
                        _console.Publish("Error", $"{operation.Id}: {opResult.Message}");
                        await _log.WriteAsync("Error", $"{operation.Id}: {opResult.Message}", cancellationToken).ConfigureAwait(false);
                    }
                }

                opResult.Success = allSucceeded;
                if (allSucceeded)
                {
                    opResult.Message = request.DryRun ? "Dry-run completed." : "Completed successfully.";
                    if (!request.DryRun && !request.Undo)
                    {
                        successfullyApplied.Add(operation);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                opResult.Cancelled = true;
                opResult.Message = "Cancelled";
                _console.Publish("Warning", $"Operation cancelled: {operation.Id}");
            }
            catch (Exception ex)
            {
                opResult.Message = $"Operation '{operation.Id}' failed at step '{currentStepName}': {ex.Message}";
                await _log.WriteAsync("Error", $"{operation.Id}/{currentStepName}: {ex}", cancellationToken).ConfigureAwait(false);
                _console.Publish("Error", opResult.Message);
            }

            result.Results.Add(opResult);
            _console.Publish(opResult.Success ? "Info" : "Warning", $"Operation result: {operation.Id} => {opResult.Message}");

            if (!opResult.Success && !opResult.Cancelled)
            {
                await RollbackSuccessfulOperationsAsync(successfullyApplied, result, cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        _console.Publish("Trace", $"ExecuteBatchAsync completed. success={result.Success}, requiresReboot={result.RequiresReboot}");
        return result;
    }

    private async Task RollbackSuccessfulOperationsAsync(
        IReadOnlyList<OperationDefinition> successfullyApplied,
        OperationBatchResult result,
        CancellationToken cancellationToken)
    {
        if (successfullyApplied.Count == 0)
        {
            return;
        }

        _console.Publish("Warning", $"Batch failed. Starting compensation rollback for {successfullyApplied.Count} completed operation(s).");
        await _log.WriteAsync("Warning", $"Batch failed. Starting compensation rollback for {successfullyApplied.Count} operation(s).", cancellationToken).ConfigureAwait(false);

        for (var i = successfullyApplied.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = successfullyApplied[i];
            var rollbackResult = new OperationExecutionResult
            {
                OperationId = $"{operation.Id}.rollback",
                RequiresReboot = operation.RequiresReboot,
                Success = false,
                Message = "Rollback not started"
            };

            if (!operation.Reversible || operation.UndoScripts.Length == 0)
            {
                rollbackResult.Message = "Rollback skipped: operation is not reversible.";
                result.Results.Add(rollbackResult);
                _console.Publish("Warning", $"{operation.Id}: {rollbackResult.Message}");
                continue;
            }

            var allRollbackStepsSucceeded = true;
            var failedRollbackStep = string.Empty;
            foreach (var step in operation.UndoScripts)
            {
                failedRollbackStep = step.Name;
                var rollbackStep = await _runner.ExecuteAsync(new PowerShellExecutionRequest
                {
                    OperationId = $"{operation.Id}.rollback",
                    StepName = step.Name,
                    Script = step.Script,
                    DryRun = false
                }, cancellationToken).ConfigureAwait(false);

                if (!rollbackStep.Success)
                {
                    allRollbackStepsSucceeded = false;
                    rollbackResult.Message = $"Rollback step failed: {step.Name}";
                    break;
                }
            }

            if (allRollbackStepsSucceeded)
            {
                rollbackResult.Success = true;
                rollbackResult.Message = "Rollback completed successfully.";
            }
            else
            {
                rollbackResult.Message = $"Rollback failed for step '{failedRollbackStep}'.";
            }

            result.Results.Add(rollbackResult);
            _console.Publish(rollbackResult.Success ? "Info" : "Error", $"{rollbackResult.OperationId}: {rollbackResult.Message}");
        }
    }

    private async Task<bool> TryCreateRestorePointAsync(OperationDefinition operation, CancellationToken cancellationToken)
    {
        try
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return false;
            }

            var description = $"Phantom {DateTime.Now:yyyyMMdd-HHmmss} {operation.Id}";
            if (description.Length > 220)
            {
                description = description[..220];
            }

            _console.Publish("Info", $"Creating restore point before dangerous operation: {operation.Title}");
            var script = $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop";
            var rpResult = await _runner.ExecuteAsync(new PowerShellExecutionRequest
            {
                OperationId = "safety.restore-point",
                StepName = operation.Id,
                Script = script,
                DryRun = false
            }, cancellationToken).ConfigureAwait(false);

            if (rpResult.Success)
            {
                _console.Publish("Info", "Restore point created successfully.");
                return true;
            }

            _console.Publish("Warning", "Restore point creation failed. Dangerous operation blocked.");
            await _log.WriteAsync("Warning", $"Restore point creation failed for {operation.Id}.", cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            _console.Publish("Warning", $"Restore point creation error: {ex.Message}");
            await _log.WriteAsync("Warning", $"Restore point creation exception for {operation.Id}: {ex}", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<OperationStateEvaluation> EvaluateOperationStateAsync(
        OperationDefinition operation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.DetectScript))
        {
            return new OperationStateEvaluation(
                DetectAvailable: false,
                DetectSucceeded: false,
                State: OperationDetectState.Unknown,
                StatusText: "Detect script unavailable");
        }

        var detect = await _runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = operation.Id,
            StepName = "detect",
            Script = operation.DetectScript,
            DryRun = false,
            SkipSafetyBackup = true
        }, cancellationToken).ConfigureAwait(false);

        if (!detect.Success)
        {
            return new OperationStateEvaluation(
                DetectAvailable: true,
                DetectSucceeded: false,
                State: OperationDetectState.Unknown,
                StatusText: "Detect script failed");
        }

        var status = string.IsNullOrWhiteSpace(detect.CombinedOutput)
            ? "Unknown"
            : detect.CombinedOutput.Trim();

        return new OperationStateEvaluation(
            DetectAvailable: true,
            DetectSucceeded: true,
            State: OperationStatusParser.Parse(status),
            StatusText: status);
    }

    private async Task<(bool Attempted, bool Passed, string StatusText)> VerifyOperationStateAsync(
        OperationDefinition operation,
        bool desiredApplied,
        CancellationToken cancellationToken)
    {
        var state = await EvaluateOperationStateAsync(operation, cancellationToken).ConfigureAwait(false);
        if (!state.DetectAvailable)
        {
            return (Attempted: false, Passed: false, StatusText: "Detect script unavailable");
        }

        if (!state.DetectSucceeded)
        {
            return (Attempted: true, Passed: false, StatusText: state.StatusText);
        }

        if (state.State == OperationDetectState.Unknown)
        {
            return (Attempted: true, Passed: false, StatusText: state.StatusText);
        }

        return (Attempted: true, Passed: IsDesiredState(state.State, desiredApplied), StatusText: state.StatusText);
    }

    private static bool IsDesiredState(OperationDetectState state, bool desiredApplied)
    {
        return desiredApplied
            ? state == OperationDetectState.Applied
            : state == OperationDetectState.NotApplied;
    }

    private static bool IsOperationCompatible(OperationDefinition operation, Version currentOsVersion)
    {
        if (operation.Compatibility is null || operation.Compatibility.Length == 0)
        {
            return true;
        }

        foreach (var tokenRaw in operation.Compatibility)
        {
            if (string.IsNullOrWhiteSpace(tokenRaw))
            {
                continue;
            }

            var token = tokenRaw.Trim();
            if (token.Equals("win10", StringComparison.OrdinalIgnoreCase) &&
                currentOsVersion.Major == 10 &&
                currentOsVersion.Build < 22000)
            {
                return true;
            }

            if (token.Equals("win11", StringComparison.OrdinalIgnoreCase) &&
                currentOsVersion.Major == 10 &&
                currentOsVersion.Build >= 22000)
            {
                return true;
            }

            if (token.StartsWith(">=", StringComparison.Ordinal))
            {
                var versionText = token[2..].Trim();
                if (Version.TryParse(versionText, out var minVersion) && currentOsVersion >= minVersion)
                {
                    return true;
                }
            }

            if (token.StartsWith("<=", StringComparison.Ordinal))
            {
                var versionText = token[2..].Trim();
                if (Version.TryParse(versionText, out var maxVersion) && currentOsVersion <= maxVersion)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record OperationStateEvaluation(
        bool DetectAvailable,
        bool DetectSucceeded,
        OperationDetectState State,
        string StatusText);
}

public static class AdminGuard
{
    public static bool IsAdministrator()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"AdminGuard.IsAdministrator failed: {ex}");
            return false;
        }
    }
}
