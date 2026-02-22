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

    public async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(string script, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        _console.Publish("Query", script);
        await _log.WriteAsync("Trace", $"PowerShellQueryService.InvokeAsync start. length={script.Length}", cancellationToken).ConfigureAwait(false);

        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            const string notWindows = "Not running on Windows.";
            _console.Publish("Error", notWindows);
            await _log.WriteAsync("Error", notWindows, cancellationToken).ConfigureAwait(false);
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
            _console.Publish("Error", failedStart);
            await _log.WriteAsync("Error", failedStart, cancellationToken).ConfigureAwait(false);
            return (1, string.Empty, "Failed to start powershell.exe");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        foreach (var line in SplitLines(stdout))
        {
            _console.Publish("Output", line);
        }

        foreach (var line in SplitLines(stderr))
        {
            _console.Publish("Error", line);
        }

        var elapsedMilliseconds = (long)((Stopwatch.GetTimestamp() - startedAt) * 1000d / Stopwatch.Frequency);
        await _log.WriteAsync(
                process.ExitCode == 0 ? "Trace" : "Error",
                $"PowerShellQueryService.InvokeAsync exit={process.ExitCode} durationMs={elapsedMilliseconds} stdoutChars={stdout.Length} stderrChars={stderr.Length}",
                cancellationToken)
            .ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }
}
