using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const string BootstrapScript = "$ErrorActionPreference='Stop';Set-StrictMode -Version Latest;";
    private static readonly HashSet<string> TrustedDownloadHosts =
    [
        "community.chocolatey.org",
        "github.com",
        "raw.githubusercontent.com",
        "www.oo-software.com",
        "dl5.oo-software.com"
    ];
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
        "onedrivesetup.exe"
    };
    private static readonly Regex DownloadCommandRegex = new(@"\b(?:Invoke-WebRequest|iwr|Invoke-RestMethod|irm|curl|wget|Start-BitsTransfer|DownloadString)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EncodedCommandRegex = new(@"\B-(?:enc|encodedcommand)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnsafeLanguageFeatureRegex = new(@"\b(?:Add-Type|Invoke-Command|Start-Job|Register-ScheduledJob)\b|FromBase64String\s*\(|System\.Reflection\.Assembly\s*::\s*Load", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlternateDataStreamRegex = new(@"[A-Za-z]:\\[^|;\r\n]*:[^\\/\r\n]+", RegexOptions.Compiled);
    private static readonly Regex StartProcessLiteralRegex = new(@"Start-Process(?:\s+-FilePath)?\s+['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RegistryPathRegex = new(@"(?:HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER|HKEY_CLASSES_ROOT|HKEY_USERS|HKEY_CURRENT_CONFIG|HKLM|HKCU|HKCR|HKU|HKCC):?\\[^'"";\r\n\)\]\}]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ServiceNameRegex = new(@"(?:Set|Stop|Start|Restart)-Service\b[^;\r\n]*?\b-Name\s+['""]?([A-Za-z0-9_.\-]+)['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScheduledTaskCmdletRegex = new(@"(?:Get|Enable|Disable|Start|Stop)-ScheduledTask\b[^;\r\n]*?\b-TaskPath\s+['""]([^'""]+)['""][^;\r\n]*?\b-TaskName\s+['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScheduledTaskCliRegex = new(@"schtasks(?:\.exe)?\b[^;\r\n]*?\b/TN\s+['""]?([^'""]+)['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProgressLineRegex = new(@"\b(100|[1-9]?\d(?:\.\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SpinnerLineRegex = new(@"^[\s\\/\-|]+$", RegexOptions.Compiled);
    private static readonly Regex ScriptFilePathRegex = new(@"(?:-File(?:Path)?|&)\s+(?:['""](?<path>[A-Za-z]:\\[^'""]+\.ps1)['""]|(?<path>[A-Za-z]:\\\S+\.ps1))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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

    private readonly ConsoleStreamService _console;
    private readonly LogService _log;
    private readonly AppPaths _paths;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly HashSet<string> _trustedCatalogScriptHashes;

    public PowerShellRunner(ConsoleStreamService console, LogService log, AppPaths paths, Func<AppSettings> settingsAccessor)
    {
        _console = console;
        _log = log;
        _paths = paths;
        _settingsAccessor = settingsAccessor;
        _trustedCatalogScriptHashes = BuildTrustedCatalogScriptHashAllowlist(paths);
    }

    public async Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        var scriptHash = ComputeScriptHash(request.Script);
        _console.Publish("Command", $"[{request.OperationId}/{request.StepName}] {request.Script}");
        await _log.WriteAsync("Command", request.Script, cancellationToken).ConfigureAwait(false);
        await _log.WriteAsync(
                "Security",
                $"ScriptAudit op={request.OperationId} step={request.StepName} hash={scriptHash} dryRun={request.DryRun} processMode={request.PreferProcessMode}",
                cancellationToken)
            .ConfigureAwait(false);
        _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync start. op={request.OperationId}, step={request.StepName}, dryRun={request.DryRun}, processMode={request.PreferProcessMode}");

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

        if (!ValidateScriptSafety(request.Script, out var blockedReason))
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

        if (!request.DryRun && !request.SkipSafetyBackup)
        {
            await CreatePreExecutionBackupsAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.DryRun)
        {
            _console.Publish("DryRun", "Dry-run enabled. Command was not executed.");
            await _log.WriteAsync("DryRun", "Dry-run enabled. Command was not executed.", cancellationToken).ConfigureAwait(false);
            return new PowerShellExecutionResult { Success = true, ExitCode = 0 };
        }

        if (request.PreferProcessMode)
        {
            var processModeResult = await ExecuteViaProcessAsync(request, cancellationToken).ConfigureAwait(false);
            _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync preferred process mode completed. op={request.OperationId}, step={request.StepName}, exit={processModeResult.ExitCode}, success={processModeResult.Success}");
            return processModeResult;
        }

        try
        {
            var runspaceResult = await ExecuteViaRunspaceAsync(request, cancellationToken).ConfigureAwait(false);
            _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync runspace completed. op={request.OperationId}, step={request.StepName}, exit={runspaceResult.ExitCode}, success={runspaceResult.Success}");
            return runspaceResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PSInvalidOperationException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _console.Publish("Warning", $"Runspace unavailable, falling back to powershell.exe. {ex.Message}");
            await _log.WriteAsync("Warning", $"Runspace fallback ({request.OperationId}/{request.StepName}): {ex}", cancellationToken).ConfigureAwait(false);
            var processResult = await ExecuteViaProcessAsync(request, cancellationToken).ConfigureAwait(false);
            _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync process fallback completed. op={request.OperationId}, step={request.StepName}, exit={processResult.ExitCode}, success={processResult.Success}");
            return processResult;
        }
        catch (InvalidOperationException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _console.Publish("Warning", $"Runspace unavailable, falling back to powershell.exe. {ex.Message}");
            await _log.WriteAsync("Warning", $"Runspace fallback ({request.OperationId}/{request.StepName}): {ex}", cancellationToken).ConfigureAwait(false);
            var processResult = await ExecuteViaProcessAsync(request, cancellationToken).ConfigureAwait(false);
            _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync process fallback completed. op={request.OperationId}, step={request.StepName}, exit={processResult.ExitCode}, success={processResult.Success}");
            return processResult;
        }
        catch (RuntimeException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (ShouldFallbackOnRuntimeException(request.StepName, ex))
            {
                _console.Publish("Warning", $"Runspace unavailable, falling back to powershell.exe. {ex.Message}");
                await _log.WriteAsync("Warning", $"Runspace fallback ({request.OperationId}/{request.StepName}): {ex}", cancellationToken).ConfigureAwait(false);
                var processResult = await ExecuteViaProcessAsync(request, cancellationToken).ConfigureAwait(false);
                _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync process fallback completed. op={request.OperationId}, step={request.StepName}, exit={processResult.ExitCode}, success={processResult.Success}");
                return processResult;
            }

            var failure = $"Runspace execution failed for mutating step '{request.StepName}'. External fallback was blocked to avoid re-running a partially executed script. {ex.Message}";
            _console.Publish("Error", failure);
            await _log.WriteAsync("Error", $"{request.OperationId}/{request.StepName}: {ex}", cancellationToken).ConfigureAwait(false);
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = 1,
                CombinedOutput = failure
            };
        }
    }

    private static bool ShouldFallbackOnRuntimeException(string stepName, RuntimeException exception)
    {
        if (stepName.StartsWith("detect", StringComparison.OrdinalIgnoreCase) ||
            stepName.StartsWith("capture:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
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

            if (registryBackups.Count == 0)
            {
                continue;
            }

            attempted = true;
            foreach (var registryBackup in registryBackups)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

            if (restored > 0)
            {
                return new BackupCompensationResult
                {
                    Attempted = true,
                    Success = true,
                    Message = $"Restored {restored} registry backup artifact(s) from safety backups for '{operationId}'."
                };
            }
        }

        if (!attempted)
        {
            return new BackupCompensationResult
            {
                Attempted = false,
                Success = false,
                Message = $"Safety backups exist for '{operationId}' but no restorable registry artifacts were found."
            };
        }

        return new BackupCompensationResult
        {
            Attempted = true,
            Success = false,
            Message = $"Safety compensation attempted for '{operationId}' but restore did not succeed (restored={restored}, failed={failed})."
        };
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

                combined.AppendLine(normalized);
                _console.Publish("Output", normalized);
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

                combined.AppendLine(normalized);
                _console.Publish("Error", normalized);
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

                combined.AppendLine(normalized);
                _console.Publish("Warning", normalized);
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

                combined.AppendLine(normalized);
                _console.Publish("Verbose", normalized);
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

                combined.AppendLine(normalized);
                _console.Publish("Debug", normalized);
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

                combined.AppendLine(normalized);
                _console.Publish("Information", normalized);
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

            var isDetectStep = string.Equals(request.StepName, "detect", StringComparison.OrdinalIgnoreCase);
            var detectState = OperationStatusParser.Parse(combined.ToString());
            var hasExplicitDetectState = detectState != OperationDetectState.Unknown;

            var success = !ps.HadErrors || (isDetectStep && hasExplicitDetectState);
            if (ps.HadErrors && isDetectStep && hasExplicitDetectState)
            {
                _console.Publish("Trace", "Detect step returned an explicit state despite runspace errors; treating detect as successful.");
            }

            _console.Publish("Trace", $"ExecuteViaRunspaceAsync finished. success={success}, outputChars={combined.Length}");
            await _log.WriteAsync(success ? "Info" : "Error", combined.ToString(), cancellationToken).ConfigureAwait(false);
            return new PowerShellExecutionResult
            {
                Success = success,
                ExitCode = success ? 0 : 1,
                CombinedOutput = combined.ToString()
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
        var wrapped = "$ProgressPreference='SilentlyContinue';$VerbosePreference='Continue';$DebugPreference='Continue';$InformationPreference='Continue';& { " +
                      request.Script +
                      " } *>&1";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        _console.Publish("Trace", $"ExecuteViaProcessAsync start. op={request.OperationId}, step={request.StepName}");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("RemoteSigned");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encodedCommand);

        var externalCommandLine = $"{psi.FileName} -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -EncodedCommand <sha256:{ComputeScriptHash(wrapped)}>";
        _console.Publish("Security", $"External PowerShell invocation: {externalCommandLine}");
        await _log.WriteAsync("Security", $"External PowerShell invocation: {externalCommandLine}", cancellationToken).ConfigureAwait(false);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.Start();
        var processCompleted = 0;

        var stdoutTask = PumpProcessStreamAsync(process.StandardOutput, line =>
        {
            if (!TryNormalizeConsoleLine(line, out var normalized))
            {
                return;
            }

            outputBuilder.AppendLine(normalized);
            _console.Publish(IsProgressMessage(normalized) ? "Progress" : "Output", normalized);
        }, cancellationToken);

        var stderrTask = PumpProcessStreamAsync(process.StandardError, line =>
        {
            if (!TryNormalizeConsoleLine(line, out var normalized))
            {
                return;
            }

            outputBuilder.AppendLine(normalized);
            _console.Publish("Error", normalized);
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
            var success = process.ExitCode == 0;
            _console.Publish("Trace", $"ExecuteViaProcessAsync finished. exit={process.ExitCode}, success={success}, outputChars={outputBuilder.Length}");

            await _log.WriteAsync(success ? "Info" : "Error", outputBuilder.ToString(), cancellationToken).ConfigureAwait(false);

            return new PowerShellExecutionResult
            {
                Success = success,
                ExitCode = process.ExitCode,
                CombinedOutput = outputBuilder.ToString()
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
        }
    }

    private bool ValidateScriptSafety(string script, out string reason)
    {
        reason = string.Empty;

        if (!_settingsAccessor().EnforceScriptSafetyGuards)
        {
            return true;
        }

        if (EncodedCommandRegex.IsMatch(script))
        {
            reason = "Blocked encoded command invocation pattern (-EncodedCommand/-enc).";
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

        foreach (Match match in StartProcessLiteralRegex.Matches(script))
        {
            var literalPath = match.Groups[1].Value.Trim();
            if (literalPath.Length == 0)
            {
                continue;
            }

            var fileName = Path.GetFileName(literalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = literalPath;
            }

            if (!AllowedStartProcessTargets.Contains(fileName))
            {
                reason = $"Blocked Start-Process target '{fileName}'.";
                return false;
            }
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

        foreach (var command in ast.FindAll(static node => node is CommandAst, searchNestedScriptBlocks: true).OfType<CommandAst>())
        {
            var commandName = command.GetCommandName();
            if (!string.IsNullOrWhiteSpace(commandName))
            {
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

        return true;
    }

    private bool ValidateDownloadSafety(string script, out string reason)
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
            if (!IsTrustedDownloadHost(uri.Host))
            {
                reason = $"Blocked download host '{uri.Host}'. Allowed hosts: {string.Join(", ", TrustedDownloadHosts)}";
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

    private bool ValidateDownloadCommandAst(CommandAst command, out string reason)
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

                if (!IsTrustedDownloadHost(uri.Host))
                {
                    reason = $"Blocked download host '{uri.Host}'. Allowed hosts: {string.Join(", ", TrustedDownloadHosts)}";
                    return false;
                }

                sawLiteralUri = true;
                continue;
            }

            if (!TryExtractLiteralHttpUri(element, out var positionalUri))
            {
                continue;
            }

            if (!IsTrustedDownloadHost(positionalUri.Host))
            {
                reason = $"Blocked download host '{positionalUri.Host}'. Allowed hosts: {string.Join(", ", TrustedDownloadHosts)}";
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

    private bool ValidateDownloadMemberInvocations(ScriptBlockAst ast, out string reason)
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

            if (!IsTrustedDownloadHost(uri.Host))
            {
                reason = $"Blocked download host '{uri.Host}'. Allowed hosts: {string.Join(", ", TrustedDownloadHosts)}";
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

        if (!Uri.TryCreate(literal.Trim(), UriKind.Absolute, out uri))
        {
            return false;
        }

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

    private static bool IsTrustedDownloadHost(string host)
    {
        return TrustedDownloadHosts.Contains(host.ToLowerInvariant());
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

    private bool ValidateOperationAllowlist(string operationId, out string reason)
    {
        reason = string.Empty;
        if (!_settingsAccessor().EnforceScriptSafetyGuards)
        {
            return true;
        }

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
        if (!_settingsAccessor().EnforceScriptSafetyGuards)
        {
            return true;
        }

        if (!IsCatalogBackedOperation(operationId))
        {
            return true;
        }

        if (operationId.StartsWith("tweak.dns.", StringComparison.OrdinalIgnoreCase) ||
            operationId.Equals("tweak.run-oo-shutup10", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_trustedCatalogScriptHashes.Contains(scriptHash))
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

    private static HashSet<string> BuildTrustedCatalogScriptHashAllowlist(AppPaths paths)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        try
        {
            if (File.Exists(paths.TweaksFile))
            {
                var tweaksJson = File.ReadAllText(paths.TweaksFile);
                var tweaks = JsonSerializer.Deserialize<List<TweakDefinition>>(tweaksJson, options) ?? new List<TweakDefinition>();
                foreach (var tweak in tweaks)
                {
                    AddScriptHash(hashes, tweak.DetectScript);
                    AddScriptHash(hashes, tweak.ApplyScript);
                    AddScriptHash(hashes, tweak.UndoScript);
                }
            }
        }
        catch
        {
        }

        try
        {
            if (File.Exists(paths.FixesFile))
            {
                var fixesJson = File.ReadAllText(paths.FixesFile);
                var fixes = JsonSerializer.Deserialize<List<FixDefinition>>(fixesJson, options) ?? new List<FixDefinition>();
                foreach (var fix in fixes)
                {
                    AddScriptHash(hashes, fix.ApplyScript);
                    AddScriptHash(hashes, fix.UndoScript);
                }
            }
        }
        catch
        {
        }

        try
        {
            if (File.Exists(paths.LegacyPanelsFile))
            {
                var panelsJson = File.ReadAllText(paths.LegacyPanelsFile);
                var panels = JsonSerializer.Deserialize<List<LegacyPanelDefinition>>(panelsJson, options) ?? new List<LegacyPanelDefinition>();
                foreach (var panel in panels)
                {
                    AddScriptHash(hashes, panel.LaunchScript);
                }
            }
        }
        catch
        {
        }

        foreach (var tweak in RequestedTweaksCatalog.CreateRequestedTweaks())
        {
            AddScriptHash(hashes, tweak.DetectScript);
            AddScriptHash(hashes, tweak.ApplyScript);
            AddScriptHash(hashes, tweak.UndoScript);
        }

        return hashes;
    }

    private static void AddScriptHash(HashSet<string> hashes, string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        hashes.Add(ComputeScriptHash(script));
    }

    private static string ComputeScriptHash(string script)
    {
        var bytes = Encoding.UTF8.GetBytes(script ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
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
                var qc = await RunProcessAsync("sc.exe", ["qc", service], cancellationToken).ConfigureAwait(false);
                var query = await RunProcessAsync("sc.exe", ["query", service], cancellationToken).ConfigureAwait(false);

                var servicePath = Path.Combine(backupRoot, $"service-{index++:D2}-{SanitizeFileName(service)}.txt");
                var content = new StringBuilder();
                content.AppendLine($"Service: {service}");
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

        return false;
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
