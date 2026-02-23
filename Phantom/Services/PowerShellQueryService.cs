using System.Diagnostics;
using System.Text;

namespace Phantom.Services;

public sealed class PowerShellQueryService
{
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
        if (echoToConsole)
        {
            _console.Publish("Query", BuildQueryPreview(script), persist: false);
        }
        await _log.WriteAsync("Trace", $"PowerShellQueryService.InvokeAsync start. length={script.Length}", cancellationToken, echoToConsole: false).ConfigureAwait(false);
        await _log.WriteAsync("Query", script, cancellationToken, echoToConsole: false).ConfigureAwait(false);

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

        var (process, host, error) = StartPowerShellProcess(script);
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

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                await _log.WriteAsync("Output", stdout.Trim(), cancellationToken, echoToConsole: false).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                await _log.WriteAsync("Error", stderr.Trim(), cancellationToken, echoToConsole: false).ConfigureAwait(false);
            }

            if (echoToConsole)
            {
                var outputPreview = BuildOutputPreview(stdout);
                if (!string.IsNullOrWhiteSpace(outputPreview))
                {
                    _console.Publish("Output", outputPreview, persist: false);
                }

                var stderrLines = SplitLines(stderr).ToList();
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
                var stderrLineCount = CountLines(stderr);
                _console.Publish(
                    process.ExitCode == 0 ? "Trace" : "Error",
                    $"Query completed. exit={process.ExitCode}, duration={elapsedMilliseconds}ms, stdoutLines={stdoutLineCount}, stderrLines={stderrLineCount}",
                    persist: false);
            }

            await _log.WriteAsync(
                    process.ExitCode == 0 ? "Trace" : "Error",
                    $"PowerShellQueryService.InvokeAsync exit={process.ExitCode} durationMs={elapsedMilliseconds} stdoutChars={stdout.Length} stderrChars={stderr.Length}",
                    cancellationToken,
                    echoToConsole: false)
                .ConfigureAwait(false);

            return (process.ExitCode, stdout, stderr);
        }
    }

    private static (Process? Process, string Host, string Error) StartPowerShellProcess(string script)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Exception? lastError = null;

        foreach (var host in new[] { "pwsh.exe", "powershell.exe" })
        {
            var psi = new ProcessStartInfo
            {
                FileName = host,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encodedScript);

            try
            {
                var process = Process.Start(psi);
                if (process is not null)
                {
                    return (process, host, string.Empty);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        return (null, "none", lastError?.Message ?? "Unable to launch pwsh.exe or powershell.exe.");
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
}
