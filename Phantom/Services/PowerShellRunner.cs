using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Phantom.Models;

namespace Phantom.Services;

public interface IPowerShellRunner
{
    Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken);
}

public sealed class PowerShellRunner : IPowerShellRunner
{
    private const string BootstrapScript = "$ErrorActionPreference='Stop';$env:PSExecutionPolicyPreference='Bypass';Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force -ErrorAction SilentlyContinue;";
    private static readonly HashSet<string> TrustedDownloadHosts =
    [
        "aka.ms",
        "community.chocolatey.org",
        "github.com",
        "raw.githubusercontent.com"
    ];
    private static readonly Regex UrlRegex = new(@"https?://[^\s'""`]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DownloadCommandRegex = new(@"\b(?:Invoke-WebRequest|iwr|Invoke-RestMethod|irm|curl|wget|Start-BitsTransfer|DownloadString)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DynamicExecutionRegex = new(@"\b(?:Invoke-Expression|IEX)\b|DownloadString\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RegistryPathRegex = new(@"(?:HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER|HKEY_CLASSES_ROOT|HKEY_USERS|HKEY_CURRENT_CONFIG|HKLM|HKCU|HKCR|HKU|HKCC):?\\[A-Za-z0-9_\\\-\.\{\}\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ServiceNameRegex = new(@"(?:Set|Stop|Start|Restart)-Service\b[^;\r\n]*?\b-Name\s+['""]?([A-Za-z0-9_.\-]+)['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScheduledTaskCmdletRegex = new(@"(?:Get|Enable|Disable|Start|Stop)-ScheduledTask\b[^;\r\n]*?\b-TaskPath\s+['""]([^'""]+)['""][^;\r\n]*?\b-TaskName\s+['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScheduledTaskCliRegex = new(@"schtasks(?:\.exe)?\b[^;\r\n]*?\b/TN\s+['""]?([^'""]+)['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ConsoleStreamService _console;
    private readonly LogService _log;
    private readonly AppPaths _paths;
    private readonly Func<AppSettings> _settingsAccessor;

    public PowerShellRunner(ConsoleStreamService console, LogService log, AppPaths paths, Func<AppSettings> settingsAccessor)
    {
        _console = console;
        _log = log;
        _paths = paths;
        _settingsAccessor = settingsAccessor;
    }

    public async Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        _console.Publish("Command", $"[{request.OperationId}/{request.StepName}] {request.Script}");
        await _log.WriteAsync("Command", request.Script, cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync start. op={request.OperationId}, step={request.StepName}, dryRun={request.DryRun}");

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

        if (!request.DryRun)
        {
            await CreatePreExecutionBackupsAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.DryRun)
        {
            _console.Publish("DryRun", "Dry-run enabled. Command was not executed.");
            await _log.WriteAsync("DryRun", "Dry-run enabled. Command was not executed.", cancellationToken).ConfigureAwait(false);
            return new PowerShellExecutionResult { Success = true, ExitCode = 0 };
        }

        try
        {
            var runspaceResult = await ExecuteViaRunspaceAsync(request, cancellationToken).ConfigureAwait(false);
            _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync runspace completed. op={request.OperationId}, step={request.StepName}, exit={runspaceResult.ExitCode}, success={runspaceResult.Success}");
            return runspaceResult;
        }
        catch (Exception ex)
        {
            _console.Publish("Warning", $"Runspace unavailable, falling back to powershell.exe. {ex.Message}");
            await _log.WriteAsync("Warning", $"Runspace fallback: {ex}", cancellationToken).ConfigureAwait(false);
            var processResult = await ExecuteViaProcessAsync(request, cancellationToken).ConfigureAwait(false);
            _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync process fallback completed. op={request.OperationId}, step={request.StepName}, exit={processResult.ExitCode}, success={processResult.Success}");
            return processResult;
        }
    }

    private async Task<PowerShellExecutionResult> ExecuteViaRunspaceAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        var sessionState = InitialSessionState.CreateDefault();
        using var runspace = RunspaceFactory.CreateRunspace(sessionState);
        runspace.Open();

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(BootstrapScript);
        ps.AddScript(request.Script);

        var output = new PSDataCollection<PSObject>();
        var combined = new StringBuilder();

        output.DataAdded += (_, args) =>
        {
            if (args.Index < 0 || args.Index >= output.Count)
            {
                return;
            }

            var text = output[args.Index]?.ToString() ?? string.Empty;
            combined.AppendLine(text);
            _console.Publish("Output", text);
        };

        ps.Streams.Error.DataAdded += (_, args) =>
        {
            if (args.Index < 0 || args.Index >= ps.Streams.Error.Count)
            {
                return;
            }

            var record = ps.Streams.Error[args.Index];
            var text = record.ToString();
            combined.AppendLine(text);
            _console.Publish("Error", text);
        };

        ps.Streams.Warning.DataAdded += (_, args) =>
        {
            if (args.Index < 0 || args.Index >= ps.Streams.Warning.Count)
            {
                return;
            }

            var text = ps.Streams.Warning[args.Index].ToString();
            combined.AppendLine(text);
            _console.Publish("Warning", text);
        };

        ps.Streams.Verbose.DataAdded += (_, args) =>
        {
            if (args.Index < 0 || args.Index >= ps.Streams.Verbose.Count)
            {
                return;
            }

            var text = ps.Streams.Verbose[args.Index].ToString();
            combined.AppendLine(text);
            _console.Publish("Verbose", text);
        };

        ps.Streams.Debug.DataAdded += (_, args) =>
        {
            if (args.Index < 0 || args.Index >= ps.Streams.Debug.Count)
            {
                return;
            }

            var text = ps.Streams.Debug[args.Index].ToString();
            combined.AppendLine(text);
            _console.Publish("Debug", text);
        };

        ps.Streams.Information.DataAdded += (_, args) =>
        {
            if (args.Index < 0 || args.Index >= ps.Streams.Information.Count)
            {
                return;
            }

            var text = ps.Streams.Information[args.Index].ToString();
            combined.AppendLine(text);
            _console.Publish("Information", text);
        };

        var async = ps.BeginInvoke<PSObject, PSObject>(null, output);

        while (!async.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        ps.EndInvoke(async);
        var success = !ps.HadErrors;
        _console.Publish("Trace", $"ExecuteViaRunspaceAsync finished. success={success}, outputChars={combined.Length}");
        await _log.WriteAsync(success ? "Info" : "Error", combined.ToString(), cancellationToken).ConfigureAwait(false);
        return new PowerShellExecutionResult
        {
            Success = success,
            ExitCode = success ? 0 : 1,
            CombinedOutput = combined.ToString()
        };
    }

    private async Task<PowerShellExecutionResult> ExecuteViaProcessAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();
        var wrapped = $"$VerbosePreference='Continue';$DebugPreference='Continue';$InformationPreference='Continue';& {{ {request.Script} }} *>&1";
        _console.Publish("Trace", $"ExecuteViaProcessAsync start. op={request.OperationId}, step={request.StepName}");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{wrapped.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            outputBuilder.AppendLine(args.Data);
            _console.Publish("Output", args.Data);
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            outputBuilder.AppendLine(args.Data);
            _console.Publish("Error", args.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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

    private bool ValidateScriptSafety(string script, out string reason)
    {
        reason = string.Empty;

        if (!_settingsAccessor().EnforceScriptSafetyGuards)
        {
            return true;
        }

        if (DynamicExecutionRegex.IsMatch(script))
        {
            reason = "Blocked dynamic script execution pattern (Invoke-Expression/IEX/DownloadString).";
            return false;
        }

        if (!DownloadCommandRegex.IsMatch(script))
        {
            return true;
        }

        foreach (Match match in UrlRegex.Matches(script))
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var host = uri.Host.ToLowerInvariant();
            if (!TrustedDownloadHosts.Contains(host))
            {
                reason = $"Blocked download host '{host}'. Allowed hosts: {string.Join(", ", TrustedDownloadHosts)}";
                return false;
            }
        }

        return true;
    }

    private async Task CreatePreExecutionBackupsAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var registryKeys = ExtractRegistryKeys(request.Script).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var serviceNames = ExtractServiceNames(request.Script).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var scheduledTasks = ExtractScheduledTasks(request.Script).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

            foreach (var key in registryKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = NormalizeRegistryPath(key);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                var filePath = Path.Combine(backupRoot, $"registry-{index++:D2}-{SanitizeFileName(normalized)}.reg");
                var export = await RunProcessAsync("reg.exe", $"export \"{normalized}\" \"{filePath}\" /y", cancellationToken).ConfigureAwait(false);
                if (export.ExitCode == 0 && File.Exists(filePath))
                {
                    backupFiles.Add(filePath);
                }
                else
                {
                    _console.Publish("Warning", $"Registry backup skipped for {normalized}: {export.Stderr.Trim()}");
                }
            }

            foreach (var service in serviceNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var qc = await RunProcessAsync("sc.exe", $"qc \"{service}\"", cancellationToken).ConfigureAwait(false);
                var query = await RunProcessAsync("sc.exe", $"query \"{service}\"", cancellationToken).ConfigureAwait(false);

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
                var export = await RunProcessAsync("schtasks.exe", $"/Query /TN \"{normalizedTask}\" /XML", cancellationToken).ConfigureAwait(false);
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (1, string.Empty, $"Failed to start {fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var normalized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "item" : normalized;
    }
}
