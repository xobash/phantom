using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Phantom.Models;

namespace Phantom.Services;

public interface IPowerShellRunner
{
    Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken);
}

public sealed class PowerShellRunner : IPowerShellRunner
{
    private const string BootstrapScript = "$ErrorActionPreference='Stop';$env:PSExecutionPolicyPreference='Bypass';Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force -ErrorAction SilentlyContinue;";

    private readonly ConsoleStreamService _console;
    private readonly LogService _log;

    public PowerShellRunner(ConsoleStreamService console, LogService log)
    {
        _console = console;
        _log = log;
    }

    public async Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
    {
        _console.Publish("Command", $"[{request.OperationId}/{request.StepName}] {request.Script}");
        await _log.WriteAsync("Command", request.Script, cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", $"PowerShellRunner.ExecuteAsync start. op={request.OperationId}, step={request.StepName}, dryRun={request.DryRun}");

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
}
