using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Phantom.Services;

public sealed class PowerShellQueryService
{
    private const string DiagnosticScriptLoggingEnvironmentVariable = "PHANTOM_DIAGNOSTIC_SCRIPT_LOGGING";
    private static readonly Regex SecretAssignmentRegex = new(
        @"(?im)\b(api[_-]?key|token|password|secret)\b\s*[:=]\s*(['""]?)(?<value>[^'""]+)\2",
        RegexOptions.Compiled);

    private readonly ConsoleStreamService _console;
    private readonly LogService _log;

    public PowerShellQueryService(ConsoleStreamService console, LogService log)
    {
        _console = console;
        _log = log;
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(string script, CancellationToken cancellationToken, bool echoToConsole = true)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var scriptHash = ComputeScriptHash(script);
        var diagnosticsEnabled = IsDiagnosticScriptLoggingEnabled();
        if (echoToConsole)
        {
            _console.Publish("Query", BuildQueryPreview(script), persist: false);
        }
        await _log.WriteAsync("Trace", $"PowerShellQueryService.InvokeAsync start. length={script.Length} hash={scriptHash}", cancellationToken, echoToConsole: false).ConfigureAwait(false);
        if (diagnosticsEnabled)
        {
            await _log.WriteAsync("Query", RedactSensitive(script), cancellationToken, echoToConsole: false).ConfigureAwait(false);
        }
        else
        {
            await _log.WriteAsync("Trace", $"PowerShellQuery body omitted. hash={scriptHash}", cancellationToken, echoToConsole: false).ConfigureAwait(false);
        }

        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            const string notWindows = "Not running on Windows.";
            if (echoToConsole)
            {
                _console.Publish("Error", notWindows, persist: false);
            }
            await _log.WriteAsync("Error", notWindows, cancellationToken, echoToConsole: false).ConfigureAwait(false);
            return (1, string.Empty, "Not running on Windows.");
        }

        var (process, host, error, commandLine) = StartPowerShellProcess(script);
        if (process is null)
        {
            var failedStart = string.IsNullOrWhiteSpace(error)
                ? "Failed to start PowerShell host."
                : error;
            if (echoToConsole)
            {
                _console.Publish("Error", failedStart, persist: false);
            }
            await _log.WriteAsync("Error", failedStart, cancellationToken, echoToConsole: false).ConfigureAwait(false);
            return (1, string.Empty, failedStart);
        }

        await _log.WriteAsync("Trace", $"PowerShellQueryService.InvokeAsync host={host}", cancellationToken, echoToConsole: false).ConfigureAwait(false);
        if (diagnosticsEnabled)
        {
            await _log.WriteAsync("Security", $"PowerShellQuery invocation: {RedactSensitive(commandLine)}", cancellationToken, echoToConsole: false).ConfigureAwait(false);
        }
        else
        {
            await _log.WriteAsync("Security", $"PowerShellQuery invocation host={host} hash={scriptHash}", cancellationToken, echoToConsole: false).ConfigureAwait(false);
        }

        using (process)
        {
            var processCompleted = 0;
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref processCompleted, 0, 0) != 0)
                {
                    return;
                }

                TryCancelProcess(process);
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref processCompleted, 1);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var sanitizedStderr = SuppressProgressCliXml(stderr);

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                await _log.WriteAsync("Output", stdout.Trim(), cancellationToken, echoToConsole: false).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(sanitizedStderr))
            {
                await _log.WriteAsync("Error", sanitizedStderr.Trim(), cancellationToken, echoToConsole: false).ConfigureAwait(false);
            }

            if (echoToConsole)
            {
                var outputPreview = BuildOutputPreview(stdout);
                if (!string.IsNullOrWhiteSpace(outputPreview))
                {
                    _console.Publish("Output", outputPreview, persist: false);
                }

                var stderrLines = SplitLines(sanitizedStderr).ToList();
                foreach (var line in stderrLines.Take(8))
                {
                    _console.Publish("Error", line, persist: false);
                }

                if (stderrLines.Count > 8)
                {
                    _console.Publish("Error", $"... ({stderrLines.Count - 8} more stderr lines)", persist: false);
                }
            }

            var elapsedMilliseconds = (long)((Stopwatch.GetTimestamp() - startedAt) * 1000d / Stopwatch.Frequency);
            if (echoToConsole)
            {
                var stdoutLineCount = CountLines(stdout);
                var stderrLineCount = CountLines(sanitizedStderr);
                _console.Publish(
                    process.ExitCode == 0 ? "Trace" : "Error",
                    $"Query completed. exit={process.ExitCode}, duration={elapsedMilliseconds}ms, stdoutLines={stdoutLineCount}, stderrLines={stderrLineCount}",
                    persist: false);
            }

            await _log.WriteAsync(
                    process.ExitCode == 0 ? "Trace" : "Error",
                    $"PowerShellQueryService.InvokeAsync exit={process.ExitCode} durationMs={elapsedMilliseconds} stdoutChars={stdout.Length} stderrChars={sanitizedStderr.Length}",
                    cancellationToken,
                    echoToConsole: false)
                .ConfigureAwait(false);

            return (process.ExitCode, stdout, sanitizedStderr);
        }
    }

    private static (Process? Process, string Host, string Error, string CommandLine) StartPowerShellProcess(string script)
    {
        Exception? lastError = null;
        var wrappedScript = $"$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';$VerbosePreference='SilentlyContinue';$InformationPreference='Continue';& {{ {script} }}";
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedScript));

        foreach (var host in new[] { "pwsh.exe", "powershell.exe" })
        {
            var psi = new ProcessStartInfo
            {
                FileName = host,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("RemoteSigned");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encodedScript);

            try
            {
                var process = Process.Start(psi);
                if (process is not null)
                {
                    try
                    {
                        process.StandardInput.WriteLine();
                        process.StandardInput.Flush();
                        process.StandardInput.Close();
                    }
                    catch
                    {
                        // Best-effort stdin guard. Query execution still proceeds.
                    }

                    var fullCommandLine = $"{host} -NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -EncodedCommand <redacted>";
                    return (process, host, string.Empty, fullCommandLine);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        return (null, "none", lastError?.Message ?? "Unable to launch pwsh.exe or powershell.exe.", string.Empty);
    }

    private static string BuildQueryPreview(string script)
    {
        var compact = string.Join(" ", SplitLines(script));
        if (string.IsNullOrWhiteSpace(compact))
        {
            return "Query started.";
        }

        var preview = compact.Length > 160 ? compact[..160] + "..." : compact;
        return $"Query: {preview}";
    }

    private static string BuildOutputPreview(string output)
    {
        var lines = SplitLines(output).ToList();
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        if (lines.Count == 1)
        {
            return lines[0].Length > 200 ? lines[0][..200] + "..." : lines[0];
        }

        var first = lines[0].Length > 160 ? lines[0][..160] + "..." : lines[0];
        return $"{lines.Count} lines returned. First: {first}";
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return SplitLines(text).Count();
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private void TryCancelProcess(Process process)
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
            _console.Publish("Trace", "PowerShell query cancellation skipped: process already disposed.", persist: false);
            return;
        }
        catch (InvalidOperationException)
        {
            _console.Publish("Trace", "PowerShell query cancellation skipped: process already exited.", persist: false);
            return;
        }

        try
        {
            _ = process.CloseMainWindow();
            if (!process.WaitForExit(500))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ObjectDisposedException)
        {
            _console.Publish("Trace", "PowerShell query cancellation raced process disposal.", persist: false);
        }
        catch (InvalidOperationException)
        {
            _console.Publish("Trace", "PowerShell query cancellation raced process exit.", persist: false);
        }
        catch (Exception ex)
        {
            _console.Publish("Warning", $"PowerShell query cancellation warning: {ex.Message}", persist: false);
            _ = _log.WriteAsync("Warning", $"PowerShellQueryService.TryCancelProcess: {ex}", CancellationToken.None, echoToConsole: false);
        }
    }

    private static string ComputeScriptHash(string script)
    {
        var bytes = Encoding.UTF8.GetBytes(script ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static bool IsDiagnosticScriptLoggingEnabled()
    {
        var value = Environment.GetEnvironmentVariable(DiagnosticScriptLoggingEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim() switch
        {
            "1" => true,
            var v when v.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }

    private static string RedactSensitive(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return SecretAssignmentRegex.Replace(text, static match =>
        {
            var key = match.Groups[1].Value;
            return $"{key}=<redacted>";
        });
    }

    private static string SuppressProgressCliXml(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        var trimmed = stderr.Trim();
        if (!trimmed.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase))
        {
            return stderr;
        }

        var containsProgressObjects = trimmed.Contains("<Obj S=\"progress\"", StringComparison.OrdinalIgnoreCase);
        var containsErrorObjects = trimmed.Contains(" S=\"error\"", StringComparison.OrdinalIgnoreCase) ||
                                   trimmed.Contains("<S S=\"Error\">", StringComparison.OrdinalIgnoreCase);
        return containsProgressObjects && !containsErrorObjects ? string.Empty : stderr;
    }
}
