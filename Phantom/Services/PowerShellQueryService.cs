using System.Diagnostics;

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

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            const string failedStart = "Failed to start powershell.exe";
            if (echoToConsole)
            {
                _console.Publish("Error", failedStart, persist: false);
            }
            await _log.WriteAsync("Error", failedStart, cancellationToken, echoToConsole: false).ConfigureAwait(false);
            return (1, string.Empty, "Failed to start powershell.exe");
        }

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
