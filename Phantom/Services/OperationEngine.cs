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
        if (!AdminGuard.IsAdministrator())
        {
            return PrecheckResult.Failure("Administrator privileges are required.");
        }

        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return PrecheckResult.Failure("Windows is required for operation execution.");
        }

        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        var drive = new DriveInfo(systemDrive);
        if (drive.AvailableFreeSpace < 500L * 1024 * 1024)
        {
            return PrecheckResult.Failure("Insufficient disk space (<500MB free). Operation blocked.");
        }

        var requiresNetwork = operations.SelectMany(o => o.RunScripts).Any(s => s.RequiresNetwork);
        if (requiresNetwork && !_network.IsOnline())
        {
            return PrecheckResult.Failure("Offline detected. Network-required operations were blocked before execution.");
        }

        await _log.WriteAsync("Info", "Batch precheck passed.", cancellationToken).ConfigureAwait(false);
        return PrecheckResult.Success();
    }

    public async Task<OperationBatchResult> ExecuteBatchAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        var result = new OperationBatchResult();
        var undoState = await _undoStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        foreach (var operation in request.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                    var stepResult = await _runner.ExecuteAsync(new PowerShellExecutionRequest
                    {
                        OperationId = operation.Id,
                        StepName = step.Name,
                        Script = step.Script,
                        DryRun = request.DryRun
                    }, cancellationToken).ConfigureAwait(false);

                    if (!stepResult.Success)
                    {
                        allSucceeded = false;
                        opResult.Message = $"Step failed: {step.Name}";
                        break;
                    }
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
            }
            catch (Exception ex)
            {
                opResult.Message = ex.Message;
                await _log.WriteAsync("Error", $"{operation.Id}: {ex}", cancellationToken).ConfigureAwait(false);
            }

            result.Results.Add(opResult);
        }

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
