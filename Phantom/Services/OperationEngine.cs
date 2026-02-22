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

    public OperationEngine(IPowerShellRunner runner, UndoStateStore undoStore, NetworkGuardService network, ConsoleStreamService console, LogService log)
    {
        _runner = runner;
        _undoStore = undoStore;
        _network = network;
        _console = console;
        _log = log;
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

        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            _console.Publish("Error", "Batch precheck failed: Windows is required.");
            return PrecheckResult.Failure("Windows is required for operation execution.");
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
                            DryRun = request.DryRun
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

                opResult.Success = allSucceeded;
                if (allSucceeded)
                {
                    opResult.Message = request.DryRun ? "Dry-run completed." : "Completed successfully.";
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
                opResult.Message = ex.Message;
                await _log.WriteAsync("Error", $"{operation.Id}: {ex}", cancellationToken).ConfigureAwait(false);
                _console.Publish("Error", $"Operation exception: {operation.Id}: {ex.Message}");
            }

            result.Results.Add(opResult);
            _console.Publish(opResult.Success ? "Info" : "Warning", $"Operation result: {operation.Id} => {opResult.Message}");
        }

        _console.Publish("Trace", $"ExecuteBatchAsync completed. success={result.Success}, requiresReboot={result.RequiresReboot}");
        return result;
    }
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
        catch
        {
            return false;
        }
    }
}
