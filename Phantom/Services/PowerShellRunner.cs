using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Phantom.Models;

namespace Phantom.Services;

public interface IPowerShellRunner
{
    Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken);
    Task<BackupCompensationResult> TryCompensateFromSafetyBackupsAsync(string operationId, CancellationToken cancellationToken);
}

public sealed class PowerShellRunner : IPowerShellRunner
{
    private const string BootstrapScript = "$ErrorActionPreference='Continue';$PSDefaultParameterValues['*:ErrorAction']='Stop';$WarningPreference='Continue';Set-StrictMode -Version Latest;";
    private static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> TrustedDownloadHosts =
    [
        "community.chocolatey.org",
        "aka.ms",
        "www.oo-software.com",
        "dl5.oo-software.com"
    ];
    private static readonly string[] TrustedGitHubRepositoryPrefixes =
    [
        "/xobash/phantom/",
        "/microsoft/winget-cli/"
    ];
    private const string TrustedDownloadHostsMessage =
        "Allowed hosts: aka.ms, community.chocolatey.org, www.oo-software.com, dl5.oo-software.com, github.com/xobash/phantom/*, github.com/microsoft/winget-cli/*, raw.githubusercontent.com/xobash/phantom/*.";
    private static readonly HashSet<string> AllowedStartProcessTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "wsreset.exe",
        "winsat.exe",
        "dfrgui.exe",
        "cleanmgr.exe",
        "mdsched.exe",
        "appwiz.cpl",
        "ncpa.cpl",
        "services.msc",
        "devmgmt.msc",
        "diskmgmt.msc",
        "gpedit.msc",
        "sysdm.cpl",
        "wf.msc",
        "onedrivesetup.exe",
        "oosu10.exe"
    };
    private static readonly Regex DownloadCommandRegex = new(@"\b(?:Invoke-WebRequest|iwr|Invoke-RestMethod|irm|curl|wget|Start-BitsTransfer|DownloadString)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EncodedCommandRegex = new(@"\B-(?:e|en|enc|enco|encodedcommand)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnsafeLanguageFeatureRegex = new(@"\b(?:Add-Type|Invoke-Command|Start-Job|Register-ScheduledJob)\b|FromBase64String\s*\(|System\.Reflection\.Assembly\s*::\s*Load", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlternateDataStreamRegex = new(@"[A-Za-z]:\\[^|;\r\n]*:[^\\/\r\n]+", RegexOptions.Compiled);
    private static readonly Regex RegistryPathRegex = new(@"(?:HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER|HKEY_CLASSES_ROOT|HKEY_USERS|HKEY_CURRENT_CONFIG|HKLM|HKCU|HKCR|HKU|HKCC):?\\[^'"";\r\n\)\]\}]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ServiceNameRegex = new(@"(?:Set|Stop|Start|Restart)-Service\b[^;\r\n]*?\b-Name\s+['""]?([A-Za-z0-9_.\-]+)['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScheduledTaskCmdletRegex = new(@"(?:Get|Enable|Disable|Start|Stop)-ScheduledTask\b[^;\r\n]*?\b-TaskPath\s+['""]([^'""]+)['""][^;\r\n]*?\b-TaskName\s+['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScheduledTaskCliRegex = new(@"schtasks(?:\.exe)?\b[^;\r\n]*?\b/TN\s+['""]?([^'""]+)['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProgressLineRegex = new(@"\b(100|[1-9]?\d(?:\.\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SpinnerLineRegex = new(@"^[\s\\/\-|]+$", RegexOptions.Compiled);
    private static readonly Regex ScriptFilePathRegex = new(@"(?:-File(?:Path)?|&)\s+(?:['""](?<path>[A-Za-z]:\\[^'""]+\.ps1)['""]|(?<path>[A-Za-z]:\\\S+\.ps1))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ServiceBackupNameRegex = new(@"^Service:\s*(?<name>[A-Za-z0-9_.\-]+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex ServiceStartTypeRegex = new(@"^\s*START_TYPE\s*:\s*\d+\s+(?<type>[A-Z_()]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex ServiceStateRegex = new(@"^\s*STATE\s*:\s*\d+\s+(?<state>[A-Z_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly HashSet<string> DynamicInvokeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoke-Expression",
        "IEX"
    };
    private static readonly HashSet<string> DownloadCommandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoke-WebRequest",
        "iwr",
        "Invoke-RestMethod",
        "irm",
        "curl",
        "wget",
        "Start-BitsTransfer"
    };
    private static readonly HashSet<string> DownloadMemberNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DownloadString",
        "DownloadFile",
        "OpenRead"
    };
    private static readonly HashSet<string> PowerShellHostCommandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell",
        "powershell.exe",
        "pwsh",
        "pwsh.exe"
    };
    private static readonly string[] AllowedOperationPrefixes =
    [
        "tweak.",
        "fix.",
        "feature.",
        "updates.",
        "store.",
        "apps.",
        "services.",
        "panel.",
        "safety.",
        "system.",
        "home."
    ];
    private static readonly string[] WingetFailureMarkers =
    [
        "No package found matching input criteria.",
        "No package found among installed packages.",
        "No installed package found matching input criteria."
    ];

    private readonly ConsoleStreamService _console;
    private readonly LogService _log;
    private readonly AppPaths _paths;
    private readonly object _catalogAllowlistSync = new();
    private HashSet<string>? _trustedCatalogScriptHashes;
    private DateTime _tweaksLastWriteUtc;
    private DateTime _fixesLastWriteUtc;
    private DateTime _panelsLastWriteUtc;

    public PowerShellRunner(ConsoleStreamService console, LogService log, AppPaths paths, Func<AppSettings> settingsAccessor)
    {
        _console = console;
        _log = log;
        _paths = paths;
        _ = settingsAccessor;
    }

    public async Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        var scriptHash = CatalogTrustService.ComputeScriptHash(request.Script);
        if (!request.SuppressConsoleOutput)
        {
            _console.Publish("Command", $"[{request.OperationId}/{request.StepName}] {request.Script}");
        }

        await _log.WriteAsync("Command", request.Script, cancellationToken, echoToConsole: false).ConfigureAwait(false);
        await _log.WriteAsync(
                "Security",
                $"ScriptAudit op={request.OperationId} step={request.StepName} hash={scriptHash} dryRun={request.DryRun} processMode={request.PreferProcessMode}",
                cancellationToken,
                echoToConsole: !request.SuppressConsoleOutput)
            .ConfigureAwait(false);
        if (!request.SuppressConsoleOutput)
        {
            _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync start. op={request.OperationId}, step={request.StepName}, dryRun={request.DryRun}, processMode={request.PreferProcessMode}");
        }

        if (!ValidateOperationAllowlist(request.OperationId, out var blockedOperationReason))
        {
            _console.Publish("Error", $"{request.OperationId}/{request.StepName}: {blockedOperationReason}");
            await _log.WriteAsync("Error", $"{request.OperationId}/{request.StepName}: {blockedOperationReason}", cancellationToken).ConfigureAwait(false);
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = 1,
                CombinedOutput = blockedOperationReason
            };
        }

        if (!ValidateCatalogScriptAllowlist(request.OperationId, scriptHash, out var blockedAllowlistReason))
        {
            _console.Publish("Error", $"{request.OperationId}/{request.StepName}: {blockedAllowlistReason}");
            await _log.WriteAsync("Error", $"{request.OperationId}/{request.StepName}: {blockedAllowlistReason}", cancellationToken).ConfigureAwait(false);
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = 1,
                CombinedOutput = blockedAllowlistReason
            };
        }

        if (!ValidateScriptSafetyGuards(request.Script, out var blockedReason))
        {
            _console.Publish("Error", $"{request.OperationId}/{request.StepName}: {blockedReason}");
            await _log.WriteAsync("Error", $"{request.OperationId}/{request.StepName}: {blockedReason}", cancellationToken).ConfigureAwait(false);
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = 1,
                CombinedOutput = blockedReason
            };
        }

        if (!ValidateExternalScriptSignatures(request.Script, out var signatureError))
        {
            _console.Publish("Error", $"{request.OperationId}/{request.StepName}: {signatureError}");
            await _log.WriteAsync("Error", $"{request.OperationId}/{request.StepName}: {signatureError}", cancellationToken).ConfigureAwait(false);
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = 1,
                CombinedOutput = signatureError
            };
        }

        CancellationTokenSource? timeoutCts = null;
        var effectiveTimeout = request.Timeout ?? DefaultExecutionTimeout;
        try
        {
            var effectiveToken = cancellationToken;
            if (effectiveTimeout > TimeSpan.Zero)
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(effectiveTimeout);
                effectiveToken = timeoutCts.Token;
            }

            if (!request.DryRun && !request.SkipSafetyBackup)
            {
                await CreatePreExecutionBackupsAsync(request, effectiveToken).ConfigureAwait(false);
            }

            if (request.DryRun)
            {
                _console.Publish("DryRun", "Dry-run enabled. Command was not executed.");
                await _log.WriteAsync("DryRun", "Dry-run enabled. Command was not executed.", effectiveToken).ConfigureAwait(false);
                return new PowerShellExecutionResult { Success = true, ExitCode = 0 };
            }

            if (request.PreferProcessMode)
            {
                var processModeResult = await ExecuteViaProcessAsync(request, effectiveToken).ConfigureAwait(false);
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync preferred process mode completed. op={request.OperationId}, step={request.StepName}, exit={processModeResult.ExitCode}, success={processModeResult.Success}");
                }

                return processModeResult;
            }

            try
            {
                var runspaceResult = await ExecuteViaRunspaceAsync(request, effectiveToken).ConfigureAwait(false);
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync runspace completed. op={request.OperationId}, step={request.StepName}, exit={runspaceResult.ExitCode}, success={runspaceResult.Success}");
                }

                return runspaceResult;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PSInvalidOperationException ex) when (!effectiveToken.IsCancellationRequested)
            {
                if (!ShouldAllowExternalFallback(request.StepName))
                {
                    var failure = $"Runspace execution failed for mutating step '{request.StepName}'. External fallback was blocked to avoid changing execution engine/security boundaries. {ex.Message}";
                    _console.Publish("Error", failure);
                    await _log.WriteAsync("Error", $"{request.OperationId}/{request.StepName}: {ex}", effectiveToken).ConfigureAwait(false);
                    return new PowerShellExecutionResult
                    {
                        Success = false,
                        ExitCode = 1,
                        CombinedOutput = failure
                    };
                }

                _console.Publish("Security", $"Runspace unavailable for verification step, falling back to external PowerShell host. {ex.Message}");
                await _log.WriteAsync("Security", $"Runspace fallback ({request.OperationId}/{request.StepName}): {ex}", effectiveToken).ConfigureAwait(false);
                var processResult = await ExecuteViaProcessAsync(request, effectiveToken).ConfigureAwait(false);
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync process fallback completed. op={request.OperationId}, step={request.StepName}, exit={processResult.ExitCode}, success={processResult.Success}");
                }

                return processResult;
            }
            catch (InvalidOperationException ex) when (!effectiveToken.IsCancellationRequested)
            {
                if (!ShouldAllowExternalFallback(request.StepName))
                {
                    var failure = $"Runspace execution failed for mutating step '{request.StepName}'. External fallback was blocked to avoid changing execution engine/security boundaries. {ex.Message}";
                    _console.Publish("Error", failure);
                    await _log.WriteAsync("Error", $"{request.OperationId}/{request.StepName}: {ex}", effectiveToken).ConfigureAwait(false);
                    return new PowerShellExecutionResult
                    {
                        Success = false,
                        ExitCode = 1,
                        CombinedOutput = failure
                    };
                }

                _console.Publish("Security", $"Runspace unavailable for verification step, falling back to external PowerShell host. {ex.Message}");
                await _log.WriteAsync("Security", $"Runspace fallback ({request.OperationId}/{request.StepName}): {ex}", effectiveToken).ConfigureAwait(false);
                var processResult = await ExecuteViaProcessAsync(request, effectiveToken).ConfigureAwait(false);
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync process fallback completed. op={request.OperationId}, step={request.StepName}, exit={processResult.ExitCode}, success={processResult.Success}");
                }

                return processResult;
            }
            catch (RuntimeException ex) when (!effectiveToken.IsCancellationRequested)
            {
                if (ShouldAllowExternalFallback(request.StepName) && ShouldFallbackOnRuntimeException(request.StepName, ex))
                {
                    _console.Publish("Security", $"Runspace unavailable for verification step, falling back to external PowerShell host. {ex.Message}");
                    await _log.WriteAsync("Security", $"Runspace fallback ({request.OperationId}/{request.StepName}): {ex}", effectiveToken).ConfigureAwait(false);
                    var processResult = await ExecuteViaProcessAsync(request, effectiveToken).ConfigureAwait(false);
                    _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync process fallback completed. op={request.OperationId}, step={request.StepName}, exit={processResult.ExitCode}, success={processResult.Success}");
                    return processResult;
                }

                var failure = $"Runspace execution failed for mutating step '{request.StepName}'. External fallback was blocked to avoid re-running a partially executed script. {ex.Message}";
                _console.Publish("Error", failure);
                await _log.WriteAsync("Error", $"{request.OperationId}/{request.StepName}: {ex}", effectiveToken).ConfigureAwait(false);
                return new PowerShellExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    CombinedOutput = failure
                };
            }
        }
        catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var timeoutMessage = $"{request.OperationId}/{request.StepName}: timed out after {effectiveTimeout.TotalSeconds:F0}s.";
            _console.Publish("Error", timeoutMessage);
            await _log.WriteAsync("Error", timeoutMessage, CancellationToken.None).ConfigureAwait(false);
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = 124,
                CombinedOutput = timeoutMessage
            };
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private static bool ShouldAllowExternalFallback(string stepName)
    {
        if (stepName.StartsWith("detect", StringComparison.OrdinalIgnoreCase) ||
            stepName.StartsWith("capture:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldFallbackOnRuntimeException(string stepName, RuntimeException exception)
    {
        if (!ShouldAllowExternalFallback(stepName))
        {
            return false;
        }

        return exception is CommandNotFoundException;
    }

    public async Task<BackupCompensationResult> TryCompensateFromSafetyBackupsAsync(string operationId, CancellationToken cancellationToken)
    {
        var backupRoot = Path.Combine(_paths.RuntimeDirectory, "safety-backups");
        if (!Directory.Exists(backupRoot))
        {
            return new BackupCompensationResult
            {
                Attempted = false,
                Success = false,
                Message = $"No safety backups found for operation '{operationId}'."
            };
        }

        var sanitizedOperationId = SanitizeFileName(operationId);
        var matchingFolders = Directory
            .EnumerateDirectories(backupRoot)
            .Where(path => Path.GetFileName(path).Contains($"-{sanitizedOperationId}-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matchingFolders.Count == 0)
        {
            return new BackupCompensationResult
            {
                Attempted = false,
                Success = false,
                Message = $"No operation-specific safety backups found for '{operationId}'."
            };
        }

        var attempted = false;
        var restored = 0;
        var failed = 0;

        foreach (var folder in matchingFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var registryBackups = Directory
                .EnumerateFiles(folder, "registry-*.reg", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var serviceBackups = Directory
                .EnumerateFiles(folder, "service-*.txt", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var scheduledTaskBackups = Directory
                .EnumerateFiles(folder, "task-*.xml", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (registryBackups.Count == 0 &&
                serviceBackups.Count == 0 &&
                scheduledTaskBackups.Count == 0)
            {
                continue;
            }

            foreach (var registryBackup in registryBackups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempted = true;
                var import = await RunProcessAsync("reg.exe", ["import", registryBackup], cancellationToken).ConfigureAwait(false);
                if (import.ExitCode == 0)
                {
                    restored++;
                }
                else
                {
                    failed++;
                    var stderr = string.IsNullOrWhiteSpace(import.Stderr) ? "unknown error" : import.Stderr.Trim();
                    _console.Publish("Warning", $"Compensation import failed for {registryBackup}: {stderr}");
                }
            }

            foreach (var serviceBackup in serviceBackups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var restoreResult = await TryRestoreServiceBackupAsync(serviceBackup, cancellationToken).ConfigureAwait(false);
                if (!restoreResult.Attempted)
                {
                    continue;
                }

                attempted = true;
                if (restoreResult.Success)
                {
                    restored++;
                }
                else
                {
                    failed++;
                    _console.Publish("Warning", restoreResult.Message);
                }
            }

            foreach (var taskBackup in scheduledTaskBackups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var restoreResult = await TryRestoreScheduledTaskBackupAsync(taskBackup, cancellationToken).ConfigureAwait(false);
                if (!restoreResult.Attempted)
                {
                    continue;
                }

                attempted = true;
                if (restoreResult.Success)
                {
                    restored++;
                }
                else
                {
                    failed++;
                    _console.Publish("Warning", restoreResult.Message);
                }
            }

            if (restored > 0)
            {
                var success = failed == 0;
                return new BackupCompensationResult
                {
                    Attempted = true,
                    Success = success,
                    Message = success
                        ? $"Restored {restored} safety backup artifact(s) from '{operationId}' (registry, services, and scheduled tasks)."
                        : $"Partially restored {restored} safety backup artifact(s) from '{operationId}', but {failed} artifact restore(s) still failed."
                };
            }
        }

        if (!attempted)
        {
            return new BackupCompensationResult
            {
                Attempted = false,
                Success = false,
                Message = $"Safety backups exist for '{operationId}' but no restorable registry, service, or scheduled-task artifacts were found."
            };
        }

        return new BackupCompensationResult
        {
            Attempted = true,
            Success = false,
            Message = $"Safety compensation attempted for '{operationId}' but restore did not succeed (restored={restored}, failed={failed})."
        };
    }

    private async Task<(bool Attempted, bool Success, string Message)> TryRestoreServiceBackupAsync(string backupPath, CancellationToken cancellationToken)
    {
        string backupText;
        try
        {
            backupText = await File.ReadAllTextAsync(backupPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (true, false, $"Compensation restore failed for service backup {backupPath}: {ex.Message}");
        }

        if (!TryParseServiceBackup(backupText, out var serviceName, out var startMode, out var shouldBeRunning))
        {
            return (false, false, string.Empty);
        }

        string safeServiceName;
        try
        {
            safeServiceName = PowerShellInputSanitizer.EnsureServiceName(serviceName, "Safety compensation");
        }
        catch (ArgumentException ex)
        {
            return (true, false, $"Compensation restore failed for service '{serviceName}': {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(startMode))
        {
            var config = await RunProcessAsync("sc.exe", ["config", safeServiceName, "start=", startMode], cancellationToken).ConfigureAwait(false);
            if (config.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(config.Stderr) ? config.Stdout.Trim() : config.Stderr.Trim();
                return (true, false, $"Compensation restore failed for service '{safeServiceName}' startup type: {error}");
            }
        }

        if (shouldBeRunning.HasValue)
        {
            var query = await RunProcessAsync("sc.exe", ["query", safeServiceName], cancellationToken).ConfigureAwait(false);
            if (query.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(query.Stderr) ? query.Stdout.Trim() : query.Stderr.Trim();
                return (true, false, $"Compensation restore failed for service '{safeServiceName}' state query: {error}");
            }

            var isCurrentlyRunning = query.Stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            if (shouldBeRunning.Value != isCurrentlyRunning)
            {
                var action = shouldBeRunning.Value ? "start" : "stop";
                var stateResult = await RunProcessAsync("sc.exe", [action, safeServiceName], cancellationToken).ConfigureAwait(false);
                if (!IsExpectedServiceStateResult(stateResult, shouldBeRunning.Value))
                {
                    var error = string.IsNullOrWhiteSpace(stateResult.Stderr) ? stateResult.Stdout.Trim() : stateResult.Stderr.Trim();
                    return (true, false, $"Compensation restore failed for service '{safeServiceName}' state change: {error}");
                }
            }
        }

        return (true, true, $"Restored service backup for '{safeServiceName}'.");
    }

    private async Task<(bool Attempted, bool Success, string Message)> TryRestoreScheduledTaskBackupAsync(string backupPath, CancellationToken cancellationToken)
    {
        string taskName;
        try
        {
            await using var stream = File.OpenRead(backupPath);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            taskName = document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("URI", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return (true, false, $"Compensation restore failed for scheduled-task backup {backupPath}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(taskName))
        {
            return (false, false, string.Empty);
        }

        var normalizedTaskName = NormalizeScheduledTaskName(taskName);
        var restore = await RunProcessAsync("schtasks.exe", ["/Create", "/TN", normalizedTaskName, "/XML", backupPath, "/F"], cancellationToken).ConfigureAwait(false);
        if (restore.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(restore.Stderr) ? restore.Stdout.Trim() : restore.Stderr.Trim();
            return (true, false, $"Compensation restore failed for scheduled task '{normalizedTaskName}': {error}");
        }

        return (true, true, $"Restored scheduled task backup for '{normalizedTaskName}'.");
    }

    private async Task<PowerShellExecutionResult> ExecuteViaRunspaceAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        var sessionState = InitialSessionState.CreateDefault();
        Runspace? runspace = null;
        PowerShell? ps = null;
        PSDataCollection<PSObject>? output = null;

        EventHandler<DataAddedEventArgs>? outputDataAdded = null;
        EventHandler<DataAddedEventArgs>? errorDataAdded = null;
        EventHandler<DataAddedEventArgs>? warningDataAdded = null;
        EventHandler<DataAddedEventArgs>? verboseDataAdded = null;
        EventHandler<DataAddedEventArgs>? debugDataAdded = null;
        EventHandler<DataAddedEventArgs>? informationDataAdded = null;

        var combined = new StringBuilder();
        var combinedSync = new object();
        try
        {
            runspace = RunspaceFactory.CreateRunspace(sessionState);
            runspace.Open();

            ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(BootstrapScript);
            ps.AddScript(request.Script);

            output = new PSDataCollection<PSObject>();

            outputDataAdded = (_, args) =>
            {
                if (args.Index < 0 || args.Index >= output.Count)
                {
                    return;
                }

                var text = output[args.Index]?.ToString() ?? string.Empty;
                if (!TryNormalizeConsoleLine(text, out var normalized))
                {
                    return;
                }

                lock (combinedSync)
                {
                    combined.AppendLine(normalized);
                }
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Output", normalized);
                }
            };
            output.DataAdded += outputDataAdded;

            errorDataAdded = (_, args) =>
            {
                if (args.Index < 0 || args.Index >= ps.Streams.Error.Count)
                {
                    return;
                }

                var record = ps.Streams.Error[args.Index];
                var text = record.ToString();
                if (!TryNormalizeConsoleLine(text, out var normalized))
                {
                    return;
                }

                lock (combinedSync)
                {
                    combined.AppendLine(normalized);
                }
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Error", normalized);
                }
            };
            ps.Streams.Error.DataAdded += errorDataAdded;

            warningDataAdded = (_, args) =>
            {
                if (args.Index < 0 || args.Index >= ps.Streams.Warning.Count)
                {
                    return;
                }

                var text = ps.Streams.Warning[args.Index].ToString();
                if (!TryNormalizeConsoleLine(text, out var normalized))
                {
                    return;
                }

                lock (combinedSync)
                {
                    combined.AppendLine(normalized);
                }
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Warning", normalized);
                }
            };
            ps.Streams.Warning.DataAdded += warningDataAdded;

            verboseDataAdded = (_, args) =>
            {
                if (args.Index < 0 || args.Index >= ps.Streams.Verbose.Count)
                {
                    return;
                }

                var text = ps.Streams.Verbose[args.Index].ToString();
                if (!TryNormalizeConsoleLine(text, out var normalized))
                {
                    return;
                }

                lock (combinedSync)
                {
                    combined.AppendLine(normalized);
                }
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Verbose", normalized);
                }
            };
            ps.Streams.Verbose.DataAdded += verboseDataAdded;

            debugDataAdded = (_, args) =>
            {
                if (args.Index < 0 || args.Index >= ps.Streams.Debug.Count)
                {
                    return;
                }

                var text = ps.Streams.Debug[args.Index].ToString();
                if (!TryNormalizeConsoleLine(text, out var normalized))
                {
                    return;
                }

                lock (combinedSync)
                {
                    combined.AppendLine(normalized);
                }
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Debug", normalized);
                }
            };
            ps.Streams.Debug.DataAdded += debugDataAdded;

            informationDataAdded = (_, args) =>
            {
                if (args.Index < 0 || args.Index >= ps.Streams.Information.Count)
                {
                    return;
                }

                var text = ps.Streams.Information[args.Index].ToString();
                if (!TryNormalizeConsoleLine(text, out var normalized))
                {
                    return;
                }

                lock (combinedSync)
                {
                    combined.AppendLine(normalized);
                }
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Information", normalized);
                }
            };
            ps.Streams.Information.DataAdded += informationDataAdded;

            var invocationCompleted = 0;
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref invocationCompleted, 0, 0) != 0)
                {
                    return;
                }

                var powerShell = ps;
                if (powerShell is null)
                {
                    return;
                }

                TryStopRunspacePipeline(powerShell, request.OperationId, request.StepName);
            });

            var async = ps.BeginInvoke<PSObject, PSObject>(null, output);

            while (!async.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            ps.EndInvoke(async);
            Interlocked.Exchange(ref invocationCompleted, 1);

            string combinedText;
            lock (combinedSync)
            {
                combinedText = combined.ToString();
            }

            var isDetectStep = string.Equals(request.StepName, "detect", StringComparison.OrdinalIgnoreCase);
            var detectState = OperationStatusParser.Parse(combinedText);
            var hasExplicitDetectState = detectState != OperationDetectState.Unknown;
            var invocationState = ps.InvocationStateInfo.State;
            var success = invocationState is PSInvocationState.Completed;
            if (!success && isDetectStep && hasExplicitDetectState)
            {
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Trace", "Detect step returned an explicit state despite runspace errors; treating detect as successful.");
                }

                success = true;
            }
            else if (success && ps.HadErrors)
            {
                if (ContainsWingetFailureMarker(request.Script, combinedText))
                {
                    success = false;
                    if (!request.SuppressConsoleOutput)
                    {
                        _console.Publish("Error", "Runspace completed but winget reported a package resolution failure.");
                    }
                }
                else
                {
                    if (!request.SuppressConsoleOutput)
                    {
                        _console.Publish("Trace", "Runspace reported non-terminating errors; treating step as successful because the script completed.");
                    }
                }
            }

            if (!request.SuppressConsoleOutput)
            {
                _console.Publish("Trace", $"ExecuteViaRunspaceAsync finished. success={success}, outputChars={combinedText.Length}");
            }

            await _log.WriteAsync(success ? "Info" : "Error", combinedText, cancellationToken, echoToConsole: false).ConfigureAwait(false);
            return new PowerShellExecutionResult
            {
                Success = success,
                ExitCode = success ? 0 : 1,
                CombinedOutput = combinedText
            };
        }
        finally
        {
            if (output is not null && outputDataAdded is not null)
            {
                output.DataAdded -= outputDataAdded;
            }

            if (ps is not null)
            {
                try
                {
                    if (errorDataAdded is not null)
                    {
                        ps.Streams.Error.DataAdded -= errorDataAdded;
                    }

                    if (warningDataAdded is not null)
                    {
                        ps.Streams.Warning.DataAdded -= warningDataAdded;
                    }

                    if (verboseDataAdded is not null)
                    {
                        ps.Streams.Verbose.DataAdded -= verboseDataAdded;
                    }

                    if (debugDataAdded is not null)
                    {
                        ps.Streams.Debug.DataAdded -= debugDataAdded;
                    }

                    if (informationDataAdded is not null)
                    {
                        ps.Streams.Information.DataAdded -= informationDataAdded;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Cancellation disposal can race stream unsubscription.
                }
                finally
                {
                    try
                    {
                        ps.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }

            runspace?.Dispose();
            (sessionState as IDisposable)?.Dispose();
        }
    }

    private async Task<PowerShellExecutionResult> ExecuteViaProcessAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();
        var outputSync = new object();
        var wrapped = "$ProgressPreference='SilentlyContinue';$VerbosePreference='Continue';$DebugPreference='Continue';$InformationPreference='Continue';& { " +
                      request.Script +
                      " } *>&1";
        var processHost = ResolveExternalPowerShellHost();
        var processScriptPath = Path.Combine(
            _paths.RuntimeDirectory,
            $"ps-fallback-{SanitizeFileName(request.OperationId)}-{SanitizeFileName(request.StepName)}-{Guid.NewGuid():N}.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(processScriptPath)!);
        await File.WriteAllTextAsync(processScriptPath, wrapped, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", $"ExecuteViaProcessAsync start. op={request.OperationId}, step={request.StepName}");

        var psi = new ProcessStartInfo
        {
            FileName = processHost,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("RemoteSigned");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(processScriptPath);

        var externalCommandLine = $"{psi.FileName} -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File <sha256:{CatalogTrustService.ComputeScriptHash(wrapped)}>";
        if (!request.SuppressConsoleOutput)
        {
            _console.Publish("Security", $"External PowerShell invocation: {externalCommandLine}");
        }

        await _log.WriteAsync("Security", $"External PowerShell invocation: {externalCommandLine}", cancellationToken, echoToConsole: false).ConfigureAwait(false);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.Start();
        var processCompleted = 0;

        var stdoutTask = PumpProcessStreamAsync(process.StandardOutput, line =>
        {
            if (!TryNormalizeConsoleLine(line, out var normalized))
            {
                return;
            }

            lock (outputSync)
            {
                outputBuilder.AppendLine(normalized);
            }
            if (!request.SuppressConsoleOutput)
            {
                _console.Publish(IsProgressMessage(normalized) ? "Progress" : "Output", normalized);
            }
        }, cancellationToken);

        var stderrTask = PumpProcessStreamAsync(process.StandardError, line =>
        {
            if (!TryNormalizeConsoleLine(line, out var normalized))
            {
                return;
            }

            lock (outputSync)
            {
                outputBuilder.AppendLine(normalized);
            }
            if (!request.SuppressConsoleOutput)
            {
                _console.Publish("Error", normalized);
            }
        }, cancellationToken);

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref processCompleted, 0, 0) != 0)
            {
                return;
            }

            TryCancelExternalProcess(process, request.OperationId, request.StepName);
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            string outputText;
            lock (outputSync)
            {
                outputText = outputBuilder.ToString();
            }

            var success = process.ExitCode == 0;
            if (success && ContainsWingetFailureMarker(request.Script, outputText))
            {
                success = false;
                if (!request.SuppressConsoleOutput)
                {
                    _console.Publish("Error", "External PowerShell completed but winget reported a package resolution failure.");
                }
            }

            var effectiveExitCode = success ? process.ExitCode : (process.ExitCode == 0 ? 1 : process.ExitCode);
            if (!request.SuppressConsoleOutput)
            {
                _console.Publish("Trace", $"ExecuteViaProcessAsync finished. exit={effectiveExitCode}, success={success}, outputChars={outputText.Length}");
            }

            await _log.WriteAsync(success ? "Info" : "Error", outputText, cancellationToken, echoToConsole: false).ConfigureAwait(false);

            return new PowerShellExecutionResult
            {
                Success = success,
                ExitCode = effectiveExitCode,
                CombinedOutput = outputText
            };
        }
        catch (OperationCanceledException)
        {
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // Ignore stream pump errors during cancellation.
            }

            _console.Publish("Warning", $"{request.OperationId}/{request.StepName}: cancelled.");
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref processCompleted, 1);
            try
            {
                if (File.Exists(processScriptPath))
                {
                    File.Delete(processScriptPath);
                }
            }
            catch (Exception ex)
            {
                _console.Publish("Warning", $"Failed to delete temporary process script '{processScriptPath}': {ex.Message}");
            }
        }
    }

    internal static bool ValidateScriptSafetyGuards(string script, out string reason)
    {
        reason = string.Empty;

        if (EncodedCommandRegex.IsMatch(script))
        {
            reason = "Blocked encoded command invocation pattern (-EncodedCommand aliases).";
            return false;
        }

        if (UnsafeLanguageFeatureRegex.IsMatch(script))
        {
            reason = "Blocked unsafe PowerShell language feature pattern (dynamic runtime assembly/job/remote execution).";
            return false;
        }

        if (AlternateDataStreamRegex.IsMatch(script))
        {
            reason = "Blocked alternate data stream path usage.";
            return false;
        }

        if (!ValidateScriptAstSafety(script, out reason))
        {
            return false;
        }

        return ValidateDownloadSafety(script, out reason);
    }

    private static bool ValidateScriptAstSafety(string script, out string reason)
    {
        reason = string.Empty;

        ScriptBlockAst ast;
        ParseError[] parseErrors;
        try
        {
            ast = Parser.ParseInput(script, out _, out parseErrors);
        }
        catch (Exception ex)
        {
            reason = $"Blocked script because AST parsing failed: {ex.Message}";
            return false;
        }

        if (parseErrors.Length > 0)
        {
            var parseMessage = parseErrors[0].Message;
            reason = $"Blocked script because AST parsing reported errors: {parseMessage}";
            return false;
        }

        var dynamicInvokerVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in ast.FindAll(static node => node is AssignmentStatementAst, searchNestedScriptBlocks: true).OfType<AssignmentStatementAst>())
        {
            if (assignment.Left is not VariableExpressionAst variable)
            {
                continue;
            }

            var assignedStringLiteral = TryExtractAssignedStringLiteral(assignment.Right);
            if (string.IsNullOrWhiteSpace(assignedStringLiteral))
            {
                continue;
            }

            if (DynamicInvokeAliases.Contains(assignedStringLiteral.Trim()))
            {
                dynamicInvokerVariables.Add(variable.VariablePath.UserPath);
            }
        }

        foreach (var usingStatement in ast.FindAll(static node => node is UsingStatementAst, searchNestedScriptBlocks: true).OfType<UsingStatementAst>())
        {
            if (usingStatement.UsingStatementKind is UsingStatementKind.Assembly or UsingStatementKind.Module)
            {
                reason = $"Blocked 'using {usingStatement.UsingStatementKind}' directive.";
                return false;
            }
        }

        foreach (var invokeMember in ast.FindAll(static node => node is InvokeMemberExpressionAst, searchNestedScriptBlocks: true).OfType<InvokeMemberExpressionAst>())
        {
            var memberName = TryGetMemberName(invokeMember.Member);
            if (memberName.Equals("InvokeScript", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Blocked InvokeScript() dynamic execution.";
                return false;
            }

            if (!memberName.Equals("Create", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expressionText = invokeMember.Expression.Extent.Text;
            if (!expressionText.Contains("scriptblock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            reason = "Blocked [scriptblock]::Create() dynamic code generation.";
            return false;
        }

        foreach (var command in ast.FindAll(static node => node is CommandAst, searchNestedScriptBlocks: true).OfType<CommandAst>())
        {
            var commandName = command.GetCommandName();
            if (!string.IsNullOrWhiteSpace(commandName))
            {
                if (IsPowerShellHostCommand(commandName) &&
                    CommandContainsEncodedCommandParameter(command, out var encodedParameterAlias))
                {
                    reason = $"Blocked encoded command argument '-{encodedParameterAlias}' in host invocation '{commandName}'.";
                    return false;
                }

                if (DynamicInvokeAliases.Contains(commandName))
                {
                    reason = $"Blocked dynamic script execution command '{commandName}'.";
                    return false;
                }

                continue;
            }

            var firstElement = command.CommandElements.FirstOrDefault();
            if (firstElement is VariableExpressionAst variableExpression &&
                dynamicInvokerVariables.Contains(variableExpression.VariablePath.UserPath))
            {
                reason = $"Blocked dynamic invocation through variable '${variableExpression.VariablePath.UserPath}'.";
                return false;
            }

            reason = "Blocked dynamic invocation that uses a non-literal command target.";
            return false;
        }

        if (!ValidateStartProcessCommands(ast, out reason))
        {
            return false;
        }

        return true;
    }

    private static bool ValidateStartProcessCommands(ScriptBlockAst ast, out string reason)
    {
        reason = string.Empty;
        var startProcessVariableTargets = BuildStartProcessVariableTargetMap(ast);

        foreach (var command in ast.FindAll(static node => node is CommandAst, searchNestedScriptBlocks: true).OfType<CommandAst>())
        {
            var commandName = command.GetCommandName();
            if (!string.Equals(commandName, "Start-Process", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetStartProcessTargetArgument(command, out var targetAst) || targetAst is null)
            {
                reason = "Blocked Start-Process invocation because -FilePath target is missing.";
                return false;
            }

            if (!TryResolveStartProcessTargetFileName(targetAst, startProcessVariableTargets, out var fileName))
            {
                reason = "Blocked Start-Process invocation because -FilePath could not be resolved to an allowlisted executable.";
                return false;
            }

            if (!AllowedStartProcessTargets.Contains(fileName))
            {
                reason = $"Blocked Start-Process target '{fileName}'.";
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> BuildStartProcessVariableTargetMap(ScriptBlockAst ast)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in ast.FindAll(static node => node is AssignmentStatementAst, searchNestedScriptBlocks: true).OfType<AssignmentStatementAst>())
        {
            if (assignment.Left is not VariableExpressionAst variable)
            {
                continue;
            }

            if (!TryResolvePathLikeFileName(assignment.Right, out var fileName))
            {
                continue;
            }

            map[variable.VariablePath.UserPath] = fileName;
        }

        return map;
    }

    private static bool TryResolveStartProcessTargetFileName(
        Ast targetAst,
        IReadOnlyDictionary<string, string> variableTargets,
        out string fileName)
    {
        fileName = string.Empty;

        if (targetAst is VariableExpressionAst variableExpression)
        {
            if (variableTargets.TryGetValue(variableExpression.VariablePath.UserPath, out var mapped) &&
                !string.IsNullOrWhiteSpace(mapped))
            {
                fileName = mapped;
                return true;
            }

            return false;
        }

        return TryResolvePathLikeFileName(targetAst, out fileName);
    }

    private static bool TryResolvePathLikeFileName(Ast ast, out string fileName)
    {
        fileName = string.Empty;
        var text = ast.Extent.Text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"'))
        {
            text = text[1..^1];
        }

        text = text.Replace('/', '\\');
        var candidate = Path.GetFileName(text);
        if (TryNormalizeAllowedTarget(candidate, out fileName))
        {
            return true;
        }

        var executableMatch = Regex.Match(text, @"([A-Za-z0-9_.\-]+\.(?:exe|cpl|msc))", RegexOptions.IgnoreCase);
        if (executableMatch.Success && TryNormalizeAllowedTarget(executableMatch.Groups[1].Value, out fileName))
        {
            return true;
        }

        return false;
    }

    private static bool TryNormalizeAllowedTarget(string? raw, out string fileName)
    {
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        fileName = Path.GetFileName(raw.Trim().Trim('"', '\''));
        return !string.IsNullOrWhiteSpace(fileName);
    }

    private static bool TryGetStartProcessTargetArgument(CommandAst command, out Ast? targetAst)
    {
        targetAst = null;

        for (var i = 1; i < command.CommandElements.Count; i++)
        {
            if (command.CommandElements[i] is not CommandParameterAst parameter ||
                !parameter.ParameterName.Equals("FilePath", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            targetAst = parameter.Argument;
            if (targetAst is not null)
            {
                return true;
            }

            if (i + 1 < command.CommandElements.Count)
            {
                targetAst = command.CommandElements[i + 1];
                return true;
            }

            return false;
        }

        for (var i = 1; i < command.CommandElements.Count; i++)
        {
            if (command.CommandElements[i] is CommandParameterAst parameter)
            {
                if (parameter.Argument is null &&
                    i + 1 < command.CommandElements.Count &&
                    command.CommandElements[i + 1] is not CommandParameterAst)
                {
                    i++;
                }

                continue;
            }

            targetAst = command.CommandElements[i];
            return true;
        }

        return false;
    }

    private static bool ValidateDownloadSafety(string script, out string reason)
    {
        reason = string.Empty;
        if (!DownloadCommandRegex.IsMatch(script))
        {
            return true;
        }

        ScriptBlockAst ast;
        ParseError[] parseErrors;
        try
        {
            ast = Parser.ParseInput(script, out _, out parseErrors);
        }
        catch (Exception ex)
        {
            reason = $"Blocked script because download safety AST parsing failed: {ex.Message}";
            return false;
        }

        if (parseErrors.Length > 0)
        {
            reason = $"Blocked script because download safety parsing reported errors: {parseErrors[0].Message}";
            return false;
        }

        var literalUris = ExtractLiteralHttpUris(ast).DistinctBy(uri => uri.AbsoluteUri).ToList();
        if (literalUris.Count == 0)
        {
            reason = "Blocked download command because no literal allowlisted URL was found.";
            return false;
        }

        foreach (var uri in literalUris)
        {
            if (!IsTrustedDownloadUri(uri))
            {
                reason = $"Blocked download host '{uri.Host}'. {TrustedDownloadHostsMessage}";
                return false;
            }
        }

        foreach (var command in ast.FindAll(static node => node is CommandAst, searchNestedScriptBlocks: true).OfType<CommandAst>())
        {
            var commandName = command.GetCommandName();
            if (string.IsNullOrWhiteSpace(commandName) || !DownloadCommandNames.Contains(commandName))
            {
                continue;
            }

            if (!ValidateDownloadCommandAst(command, out reason))
            {
                return false;
            }
        }

        if (!ValidateDownloadMemberInvocations(ast, out reason))
        {
            return false;
        }

        return true;
    }

    private static bool ValidateDownloadCommandAst(CommandAst command, out string reason)
    {
        reason = string.Empty;
        var commandName = command.GetCommandName() ?? "<dynamic>";
        var sawLiteralUri = false;

        for (var i = 1; i < command.CommandElements.Count; i++)
        {
            var element = command.CommandElements[i];
            if (element is CommandParameterAst parameter && IsDownloadUriParameter(parameter.ParameterName))
            {
                Ast? argument = parameter.Argument;
                if (argument is null)
                {
                    if (i + 1 >= command.CommandElements.Count)
                    {
                        reason = $"Blocked download command '{commandName}' because parameter '-{parameter.ParameterName}' has no value.";
                        return false;
                    }

                    argument = command.CommandElements[++i];
                }

                if (!TryExtractLiteralHttpUri(argument, out var uri))
                {
                    reason = $"Blocked download command '{commandName}' because parameter '-{parameter.ParameterName}' is not a literal URL.";
                    return false;
                }

                if (!IsTrustedDownloadUri(uri))
                {
                    reason = $"Blocked download host '{uri.Host}'. {TrustedDownloadHostsMessage}";
                    return false;
                }

                sawLiteralUri = true;
                continue;
            }

            if (!TryExtractLiteralHttpUri(element, out var positionalUri))
            {
                continue;
            }

            if (!IsTrustedDownloadUri(positionalUri))
            {
                reason = $"Blocked download host '{positionalUri.Host}'. {TrustedDownloadHostsMessage}";
                return false;
            }

            sawLiteralUri = true;
        }

        if (!sawLiteralUri)
        {
            reason = $"Blocked download command '{commandName}' because URL is not a literal allowlisted value.";
            return false;
        }

        return true;
    }

    private static bool IsDownloadUriParameter(string? parameterName)
    {
        return !string.IsNullOrWhiteSpace(parameterName) &&
               (parameterName.Equals("Uri", StringComparison.OrdinalIgnoreCase) ||
                parameterName.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                parameterName.Equals("Url", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ValidateDownloadMemberInvocations(ScriptBlockAst ast, out string reason)
    {
        reason = string.Empty;
        foreach (var invokeMember in ast.FindAll(static n => n is InvokeMemberExpressionAst, searchNestedScriptBlocks: true).OfType<InvokeMemberExpressionAst>())
        {
            var memberName = TryGetMemberName(invokeMember.Member);
            if (string.IsNullOrWhiteSpace(memberName) || !DownloadMemberNames.Contains(memberName))
            {
                continue;
            }

            if (invokeMember.Arguments.Count == 0)
            {
                reason = $"Blocked dynamic download invocation '.{memberName}(...)' because URL argument is missing.";
                return false;
            }

            if (!TryExtractLiteralHttpUri(invokeMember.Arguments[0], out var uri))
            {
                reason = $"Blocked dynamic download invocation '.{memberName}(...)' because URL argument is not a literal URL.";
                return false;
            }

            if (!IsTrustedDownloadUri(uri))
            {
                reason = $"Blocked download host '{uri.Host}'. {TrustedDownloadHostsMessage}";
                return false;
            }
        }

        return true;
    }

    private static string TryGetMemberName(Ast member)
    {
        if (member is StringConstantExpressionAst constant)
        {
            return constant.Value;
        }

        var raw = member.Extent.Text.Trim();
        return raw.Trim('\'', '"');
    }

    private static bool TryExtractLiteralHttpUri(Ast ast, out Uri uri)
    {
        uri = default!;
        string? literal = null;
        switch (ast)
        {
            case StringConstantExpressionAst constant:
                literal = constant.Value;
                break;
            case ExpandableStringExpressionAst expandable when expandable.NestedExpressions.Count == 0:
                literal = expandable.Value;
                break;
        }

        if (string.IsNullOrWhiteSpace(literal))
        {
            return false;
        }

        if (!Uri.TryCreate(literal.Trim(), UriKind.Absolute, out var parsedUri))
        {
            return false;
        }
        uri = parsedUri;

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Uri> ExtractLiteralHttpUris(ScriptBlockAst ast)
    {
        foreach (var node in ast.FindAll(static n => n is StringConstantExpressionAst || n is ExpandableStringExpressionAst, searchNestedScriptBlocks: true))
        {
            if (TryExtractLiteralHttpUri(node, out var uri))
            {
                yield return uri;
            }
        }
    }

    private static bool IsTrustedDownloadUri(Uri uri)
    {
        if (uri is null)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return TrustedGitHubRepositoryPrefixes.Any(prefix =>
                uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        return TrustedDownloadHosts.Contains(host);
    }

    private static string? TryExtractAssignedStringLiteral(StatementAst rightSideStatement)
    {
        var raw = rightSideStatement.Extent.Text.Trim();
        if (raw.Length < 2)
        {
            return null;
        }

        var startsAndEndsWithSingleQuote = raw[0] == '\'' && raw[^1] == '\'';
        var startsAndEndsWithDoubleQuote = raw[0] == '"' && raw[^1] == '"';
        if (!startsAndEndsWithSingleQuote && !startsAndEndsWithDoubleQuote)
        {
            return null;
        }

        return raw[1..^1];
    }

    private static string ResolveExternalPowerShellHost()
    {
        foreach (var candidate in OperatingSystem.IsWindows()
                     ? new[] { "pwsh.exe", "pwsh", "powershell.exe" }
                     : new[] { "pwsh", "powershell" })
        {
            var resolved = TryResolveExecutableFromPath(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
    }

    private static string? TryResolveExecutableFromPath(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        if (Path.IsPathRooted(commandName) && File.Exists(commandName))
        {
            return commandName;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var pathExtensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               ?? new[] { ".exe", ".cmd", ".bat", ".com" })
            : new[] { string.Empty };
        var hasExtension = Path.HasExtension(commandName);

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (hasExtension)
            {
                var candidate = Path.Combine(segment, commandName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            foreach (var extension in pathExtensions)
            {
                var candidate = Path.Combine(segment, commandName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsPowerShellHostCommand(string commandName)
    {
        var trimmed = commandName.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var fileName = Path.GetFileName(trimmed);
        return PowerShellHostCommandNames.Contains(fileName);
    }

    private static bool CommandContainsEncodedCommandParameter(CommandAst command, out string parameterAlias)
    {
        parameterAlias = string.Empty;
        foreach (var parameter in command.CommandElements.OfType<CommandParameterAst>())
        {
            var name = parameter.ParameterName?.Trim() ?? string.Empty;
            if (IsEncodedCommandParameterAlias(name))
            {
                parameterAlias = name;
                return true;
            }
        }

        return false;
    }

    private static bool IsEncodedCommandParameterAlias(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            return false;
        }

        var normalized = parameterName.Trim().TrimStart('-');
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized.Equals("encoding", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("encodedarguments", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string encodedCommand = "encodedcommand";
        if (normalized.Length > encodedCommand.Length)
        {
            return false;
        }

        return encodedCommand.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private bool ValidateOperationAllowlist(string operationId, out string reason)
    {
        reason = string.Empty;
        if (AllowedOperationPrefixes.Any(prefix => operationId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        reason = $"Blocked operation id '{operationId}' because it is not in the PowerShell operation allowlist.";
        return false;
    }

    private bool ValidateCatalogScriptAllowlist(string operationId, string scriptHash, out string reason)
    {
        reason = string.Empty;
        if (!IsCatalogBackedOperation(operationId))
        {
            return true;
        }

        if (!TryGetTrustedCatalogScriptHashes(out var trustedHashes, out var integrityReason))
        {
            reason = $"Blocked script for operation '{operationId}' because catalog integrity validation failed. {integrityReason}";
            return false;
        }

        if (trustedHashes.Contains(scriptHash))
        {
            return true;
        }

        reason = $"Blocked script for operation '{operationId}' because hash {scriptHash} is not in the trusted catalog allowlist.";
        return false;
    }

    private static bool IsCatalogBackedOperation(string operationId)
    {
        return operationId.StartsWith("tweak.", StringComparison.OrdinalIgnoreCase) ||
               operationId.StartsWith("fix.", StringComparison.OrdinalIgnoreCase) ||
               operationId.StartsWith("panel.", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetTrustedCatalogScriptHashes(out HashSet<string> hashes, out string reason)
    {
        reason = string.Empty;
        hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_catalogAllowlistSync)
        {
            var tweaksWrite = GetFileWriteTimeUtc(_paths.TweaksFile);
            var fixesWrite = GetFileWriteTimeUtc(_paths.FixesFile);
            var panelsWrite = GetFileWriteTimeUtc(_paths.LegacyPanelsFile);

            var needsRefresh =
                _trustedCatalogScriptHashes is null ||
                tweaksWrite != _tweaksLastWriteUtc ||
                fixesWrite != _fixesLastWriteUtc ||
                panelsWrite != _panelsLastWriteUtc;

            if (needsRefresh)
            {
                if (!CatalogTrustService.TryValidateCatalogIntegrityAndBuildAllowlist(_paths, out var trustedHashes, out var loadReason))
                {
                    _trustedCatalogScriptHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    reason = loadReason;
                    _console.Publish("Error", $"Trusted catalog allowlist refresh failed: {loadReason}");
                    _tweaksLastWriteUtc = tweaksWrite;
                    _fixesLastWriteUtc = fixesWrite;
                    _panelsLastWriteUtc = panelsWrite;
                    hashes = _trustedCatalogScriptHashes;
                    return false;
                }

                _trustedCatalogScriptHashes = trustedHashes;

                _tweaksLastWriteUtc = tweaksWrite;
                _fixesLastWriteUtc = fixesWrite;
                _panelsLastWriteUtc = panelsWrite;
                _console.Publish("Trace", $"Trusted catalog allowlist refreshed. hashCount={_trustedCatalogScriptHashes.Count}");
            }

            hashes = _trustedCatalogScriptHashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return true;
        }
    }

    private static DateTime GetFileWriteTimeUtc(string path)
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private static bool ValidateExternalScriptSignatures(string script, out string reason)
    {
        reason = string.Empty;
        var paths = ScriptFilePathRegex.Matches(script)
            .Cast<Match>()
            .Select(match => match.Groups["path"].Value.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                reason = $"Blocked external invocation: script file '{path}' does not exist.";
                return false;
            }

            try
            {
                using var ps = PowerShell.Create();
                ps.AddCommand("Get-AuthenticodeSignature");
                ps.AddParameter("FilePath", path);
                var signatures = ps.Invoke();
                if (ps.HadErrors || signatures.Count == 0)
                {
                    reason = $"Blocked external invocation: failed to validate script signature for '{path}'.";
                    return false;
                }

                var status = signatures[0].Properties["Status"]?.Value?.ToString() ?? "Unknown";
                if (!string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"Blocked external invocation: script '{path}' signature status is '{status}' (expected Valid).";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = $"Blocked external invocation: signature validation error for '{path}': {ex.Message}";
                return false;
            }
        }

        return true;
    }

    private async Task CreatePreExecutionBackupsAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var backupTargets = ExtractBackupTargets(request.Script);
            var registryKeys = backupTargets.RegistryKeys;
            var serviceNames = backupTargets.ServiceNames;
            var scheduledTasks = backupTargets.ScheduledTasks;

            if (registryKeys.Count == 0 && serviceNames.Count == 0 && scheduledTasks.Count == 0)
            {
                return;
            }

            var backupRoot = Path.Combine(
                _paths.RuntimeDirectory,
                "safety-backups",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{SanitizeFileName(request.OperationId)}-{SanitizeFileName(request.StepName)}");
            Directory.CreateDirectory(backupRoot);

            var backupFiles = new List<string>();
            var index = 0;
            var registryExistsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in registryKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!registryExistsCache.TryGetValue(key, out var exists))
                {
                    exists = RegistryKeyExists(key);
                    registryExistsCache[key] = exists;
                }

                if (!exists)
                {
                    _console.Publish("Warning", $"Registry backup skipped for {key}: key does not exist.");
                    continue;
                }

                var filePath = Path.Combine(backupRoot, $"registry-{index++:D2}-{SanitizeFileName(key)}.reg");
                var export = await RunProcessAsync("reg.exe", ["export", key, filePath, "/y"], cancellationToken).ConfigureAwait(false);
                if (export.ExitCode == 0 && File.Exists(filePath))
                {
                    backupFiles.Add(filePath);
                }
                else
                {
                    _console.Publish("Warning", $"Registry backup skipped for {key}: {export.Stderr.Trim()}");
                }
            }

            foreach (var service in serviceNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string safeServiceName;
                try
                {
                    safeServiceName = PowerShellInputSanitizer.EnsureServiceName(service, "Safety backup");
                }
                catch (ArgumentException ex)
                {
                    _console.Publish("Warning", $"Service backup skipped: {ex.Message}");
                    continue;
                }

                var qc = await RunProcessAsync("sc.exe", ["qc", safeServiceName], cancellationToken).ConfigureAwait(false);
                var query = await RunProcessAsync("sc.exe", ["query", safeServiceName], cancellationToken).ConfigureAwait(false);

                var servicePath = Path.Combine(backupRoot, $"service-{index++:D2}-{SanitizeFileName(safeServiceName)}.txt");
                var content = new StringBuilder();
                content.AppendLine($"Service: {safeServiceName}");
                content.AppendLine("--- sc qc ---");
                content.AppendLine(string.IsNullOrWhiteSpace(qc.Stdout) ? qc.Stderr.Trim() : qc.Stdout.Trim());
                content.AppendLine("--- sc query ---");
                content.AppendLine(string.IsNullOrWhiteSpace(query.Stdout) ? query.Stderr.Trim() : query.Stdout.Trim());
                await File.WriteAllTextAsync(servicePath, content.ToString(), cancellationToken).ConfigureAwait(false);
                backupFiles.Add(servicePath);
            }

            foreach (var task in scheduledTasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedTask = NormalizeScheduledTaskName(task);
                var export = await RunProcessAsync("schtasks.exe", ["/Query", "/TN", normalizedTask, "/XML"], cancellationToken).ConfigureAwait(false);
                if (export.ExitCode != 0 || string.IsNullOrWhiteSpace(export.Stdout))
                {
                    _console.Publish("Warning", $"Scheduled task backup skipped for {normalizedTask}: {export.Stderr.Trim()}");
                    continue;
                }

                var taskPath = Path.Combine(backupRoot, $"task-{index++:D2}-{SanitizeFileName(normalizedTask)}.xml");
                await File.WriteAllTextAsync(taskPath, export.Stdout, cancellationToken).ConfigureAwait(false);
                backupFiles.Add(taskPath);
            }

            var manifestPath = Path.Combine(backupRoot, "manifest.json");
            var manifest = new
            {
                request.OperationId,
                request.StepName,
                CreatedAt = DateTimeOffset.UtcNow,
                RegistryKeys = registryKeys,
                Services = serviceNames,
                ScheduledTasks = scheduledTasks,
                Files = backupFiles
            };

            await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);

            _console.Publish("Info", $"Safety backup created: {manifestPath}");
        }
        catch (Exception ex)
        {
            _console.Publish("Warning", $"Safety backup failed: {ex.Message}");
            await _log.WriteAsync("Warning", $"Safety backup failed: {ex}", cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> ExtractRegistryKeys(string script)
    {
        foreach (Match match in RegistryPathRegex.Matches(script))
        {
            var value = match.Value.Trim().Trim('\'', '"', ';', ',', ')', ']', '}');
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static BackupTargets ExtractBackupTargets(string script)
    {
        var registryKeys = ExtractRegistryKeys(script)
            .Select(NormalizeRegistryPath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var serviceNames = ExtractServiceNames(script)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var scheduledTasks = ExtractScheduledTasks(script)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BackupTargets(registryKeys, serviceNames, scheduledTasks);
    }

    private static IEnumerable<string> ExtractServiceNames(string script)
    {
        foreach (Match match in ServiceNameRegex.Matches(script))
        {
            var service = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(service))
            {
                yield return service;
            }
        }
    }

    private static IEnumerable<string> ExtractScheduledTasks(string script)
    {
        foreach (Match match in ScheduledTaskCmdletRegex.Matches(script))
        {
            var taskPath = match.Groups[1].Value.Trim();
            var taskName = match.Groups[2].Value.Trim();
            if (!string.IsNullOrWhiteSpace(taskName))
            {
                yield return $"{taskPath}{taskName}";
            }
        }

        foreach (Match match in ScheduledTaskCliRegex.Matches(script))
        {
            var task = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(task))
            {
                yield return task;
            }
        }
    }

    private static string NormalizeRegistryPath(string value)
    {
        var key = value.Trim().Trim('\'', '"');
        return key switch
        {
            _ when key.StartsWith("HKLM:\\", StringComparison.OrdinalIgnoreCase) => "HKEY_LOCAL_MACHINE\\" + key[6..],
            _ when key.StartsWith("HKCU:\\", StringComparison.OrdinalIgnoreCase) => "HKEY_CURRENT_USER\\" + key[6..],
            _ when key.StartsWith("HKCR:\\", StringComparison.OrdinalIgnoreCase) => "HKEY_CLASSES_ROOT\\" + key[6..],
            _ when key.StartsWith("HKU:\\", StringComparison.OrdinalIgnoreCase) => "HKEY_USERS\\" + key[5..],
            _ when key.StartsWith("HKCC:\\", StringComparison.OrdinalIgnoreCase) => "HKEY_CURRENT_CONFIG\\" + key[6..],
            _ => key
        };
    }

    private static string NormalizeScheduledTaskName(string value)
    {
        var task = value.Trim().Trim('\'', '"');
        return task.StartsWith('\\') ? task : "\\" + task;
    }

    private static bool TryParseServiceBackup(string content, out string serviceName, out string startMode, out bool? shouldBeRunning)
    {
        serviceName = string.Empty;
        startMode = string.Empty;
        shouldBeRunning = null;

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var nameMatch = ServiceBackupNameRegex.Match(content);
        if (!nameMatch.Success)
        {
            return false;
        }

        serviceName = nameMatch.Groups["name"].Value.Trim();
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return false;
        }

        var startType = ServiceStartTypeRegex.Match(content).Groups["type"].Value.Trim();
        if (!string.IsNullOrWhiteSpace(startType))
        {
            startMode = MapServiceStartMode(startType);
        }

        var state = ServiceStateRegex.Match(content).Groups["state"].Value.Trim();
        if (state.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            shouldBeRunning = true;
        }
        else if (state.Equals("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            shouldBeRunning = false;
        }

        return true;
    }

    private static string MapServiceStartMode(string rawStartType)
    {
        var normalized = rawStartType.Trim().ToUpperInvariant();
        if (normalized.Contains("DISABLED", StringComparison.Ordinal))
        {
            return "disabled";
        }

        if (normalized.Contains("DEMAND", StringComparison.Ordinal) ||
            normalized.Contains("MANUAL", StringComparison.Ordinal))
        {
            return "demand";
        }

        if (normalized.Contains("AUTO", StringComparison.Ordinal))
        {
            return normalized.Contains("DELAYED", StringComparison.Ordinal) ? "delayed-auto" : "auto";
        }

        return string.Empty;
    }

    private static bool IsExpectedServiceStateResult((int ExitCode, string Stdout, string Stderr) result, bool shouldBeRunning)
    {
        if (result.ExitCode == 0)
        {
            return true;
        }

        var combined = $"{result.Stdout} {result.Stderr}";
        return shouldBeRunning
            ? combined.Contains("FAILED 1056", StringComparison.OrdinalIgnoreCase) ||
              combined.Contains("already running", StringComparison.OrdinalIgnoreCase)
            : combined.Contains("FAILED 1062", StringComparison.OrdinalIgnoreCase) ||
              combined.Contains("has not been started", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RegistryKeyExists(string normalizedPath)
    {
        try
        {
            var value = normalizedPath.Trim();
            var slash = value.IndexOf('\\');
            var hive = slash < 0 ? value : value[..slash];
            var subPath = slash < 0 ? string.Empty : value[(slash + 1)..];

            var registryHive = hive.ToUpperInvariant() switch
            {
                "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
                "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
                "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
                "HKEY_USERS" => RegistryHive.Users,
                "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
                _ => (RegistryHive?)null
            };

            if (registryHive is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(subPath))
            {
                return true;
            }

            using var root = RegistryKey.OpenBaseKey(registryHive.Value, WindowsSupportPolicy.PreferredRegistryView);
            using var key = root.OpenSubKey(subPath, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (1, string.Empty, $"Failed to start {fileName}");
        }

        var processCompleted = 0;
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref processCompleted, 0, 0) != 0)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        });

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        finally
        {
            Interlocked.Exchange(ref processCompleted, 1);
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var normalized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "item" : normalized;
    }

    private static bool IsProgressMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return ProgressLineRegex.IsMatch(line);
    }

    private static bool ContainsWingetFailureMarker(string script, string output)
    {
        if (string.IsNullOrWhiteSpace(script) ||
            script.IndexOf("winget", StringComparison.OrdinalIgnoreCase) < 0 ||
            string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        foreach (var marker in WingetFailureMarkers)
        {
            if (output.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryNormalizeConsoleLine(string line, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || ShouldSuppressNoisyConsoleLine(trimmed))
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }

    private static bool ShouldSuppressNoisyConsoleLine(string line)
    {
        if (SpinnerLineRegex.IsMatch(line))
        {
            return true;
        }

        if (line.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains("Preparing modules for first use.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains("<Obj S=\"progress\"", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("<Objs Version=", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("</Objs>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var containsWingetProgressSize = line.Contains("KB /", StringComparison.OrdinalIgnoreCase) ||
                                         line.Contains("MB /", StringComparison.OrdinalIgnoreCase) ||
                                         line.Contains("GB /", StringComparison.OrdinalIgnoreCase);
        if (containsWingetProgressSize && (line.Contains('%') || line.Contains("Ôû", StringComparison.Ordinal)))
        {
            return true;
        }

        // winget may emit mojibake block characters and text-only progress bars.
        if (line.Contains("Ôû", StringComparison.Ordinal) && line.Contains('%'))
        {
            return true;
        }

        if (LooksLikeWingetProgressBar(line))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeWingetProgressBar(string line)
    {
        if (!line.Contains('%'))
        {
            return false;
        }

        var openBracket = line.IndexOf('[');
        var closeBracket = line.IndexOf(']');
        if (openBracket < 0 || closeBracket <= openBracket)
        {
            return false;
        }

        var width = closeBracket - openBracket - 1;
        if (width < 8)
        {
            return false;
        }

        for (var i = openBracket + 1; i < closeBracket; i++)
        {
            var ch = line[i];
            if (ch != '#' && ch != '-' && ch != ' ')
            {
                return false;
            }
        }

        return true;
    }

    private static async Task PumpProcessStreamAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        var lineBuilder = new StringBuilder();
        var buffer = new char[1];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var ch = buffer[0];
            if (ch == '\r' || ch == '\n')
            {
                if (lineBuilder.Length == 0)
                {
                    continue;
                }

                onLine(lineBuilder.ToString());
                lineBuilder.Clear();
                continue;
            }

            lineBuilder.Append(ch);
        }

        if (lineBuilder.Length > 0)
        {
            onLine(lineBuilder.ToString());
        }
    }

    private void TryStopRunspacePipeline(PowerShell ps, string operationId, string stepName)
    {
        var stopIssued = false;
        try
        {
            var state = ps.InvocationStateInfo.State;
            if (state is PSInvocationState.Completed or PSInvocationState.Failed or PSInvocationState.Stopped)
            {
                return;
            }

            ps.Stop();
            stopIssued = true;
            _console.Publish("Warning", $"{operationId}/{stepName}: cancellation requested. Runspace pipeline stop issued.");
        }
        catch (ObjectDisposedException)
        {
            // Cancellation raced disposal; no action required.
        }
        catch (InvalidOperationException)
        {
            // Pipeline already finished while cancellation was processed.
        }
        catch (Exception ex)
        {
            _console.Publish("Warning", $"{operationId}/{stepName}: runspace cancellation warning: {ex.Message}");
        }
        finally
        {
            if (stopIssued)
            {
                try
                {
                    ps.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex)
                {
                    _console.Publish("Warning", $"{operationId}/{stepName}: runspace disposal warning: {ex.Message}");
                }
            }
        }
    }

    private void TryCancelExternalProcess(Process process, string operationId, string stepName)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        try
        {
            _console.Publish("Warning", $"{operationId}/{stepName}: cancellation requested. Attempting graceful external PowerShell shutdown.");
            _ = process.CloseMainWindow();
            if (!process.WaitForExit(500))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ObjectDisposedException)
        {
            // Process disposed concurrently.
        }
        catch (InvalidOperationException)
        {
            // Process exited while attempting shutdown.
        }
        catch (Exception ex)
        {
            _console.Publish("Warning", $"{operationId}/{stepName}: external cancellation warning: {ex.Message}");
        }
    }

    private sealed record BackupTargets(
        List<string> RegistryKeys,
        List<string> ServiceNames,
        List<string> ScheduledTasks);
}
