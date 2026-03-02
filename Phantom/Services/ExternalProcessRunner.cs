using System.Diagnostics;
using System.Text;

namespace Phantom.Services;

public sealed class ExternalProcessRequest
{
    public string OperationId { get; init; } = string.Empty;
    public string StepName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed class ExternalProcessResult
{
    public bool Success { get; init; }
    public bool TimedOut { get; init; }
    public int ExitCode { get; init; }
    public long DurationMs { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
}

public sealed class ExternalProcessRunner
{
    private const int TimeoutExitCode = 124;

    private readonly ConsoleStreamService _console;
    private readonly LogService _log;

    public ExternalProcessRunner(ConsoleStreamService console, LogService log)
    {
        _console = console;
        _log = log;
    }

    public async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            throw new ArgumentException("FilePath is required.", nameof(request));
        }

        var args = request.Arguments ?? Array.Empty<string>();
        var argsText = string.Join(" ", args.Select(WindowsCommandLine.QuoteArgument));
        _console.Publish("Command", $"[{request.OperationId}/{request.StepName}] {request.FilePath} {argsText}".TrimEnd());

        var stopwatch = Stopwatch.StartNew();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process
        {
            StartInfo = BuildStartInfo(request.FilePath, args)
        };

        CancellationTokenSource? timeoutCts = null;
        CancellationToken effectiveToken = cancellationToken;
        if (request.Timeout > TimeSpan.Zero)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(request.Timeout);
            effectiveToken = timeoutCts.Token;
        }

        Task stdoutTask = Task.CompletedTask;
        Task stderrTask = Task.CompletedTask;
        var timedOut = false;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{request.FilePath}'.");
            }

            stdoutTask = PumpStreamAsync(process.StandardOutput, line =>
            {
                stdout.AppendLine(line);
                _console.Publish("Output", line);
            }, effectiveToken);

            stderrTask = PumpStreamAsync(process.StandardError, line =>
            {
                stderr.AppendLine(line);
                _console.Publish("Error", line);
            }, effectiveToken);

            await process.WaitForExitAsync(effectiveToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcess(process);
            await AwaitAndIgnoreCancellation(stdoutTask).ConfigureAwait(false);
            await AwaitAndIgnoreCancellation(stderrTask).ConfigureAwait(false);
        }
        catch
        {
            TryKillProcess(process);
            throw;
        }
        finally
        {
            timeoutCts?.Dispose();
        }

        stopwatch.Stop();
        var stdoutText = stdout.ToString().TrimEnd();
        var stderrText = stderr.ToString().TrimEnd();
        var exitCode = timedOut ? TimeoutExitCode : process.ExitCode;
        var success = !timedOut && exitCode == 0;

        _console.Publish("Trace", $"Process finished. op={request.OperationId}, step={request.StepName}, exit={exitCode}, durationMs={stopwatch.ElapsedMilliseconds}, timeout={timedOut}");
        await _log.WriteAsync("Trace", $"ProcessAudit op={request.OperationId} step={request.StepName} exe={request.FilePath} args={argsText} exit={exitCode} durationMs={stopwatch.ElapsedMilliseconds}", CancellationToken.None).ConfigureAwait(false);

        return new ExternalProcessResult
        {
            Success = success,
            TimedOut = timedOut,
            ExitCode = exitCode,
            DurationMs = stopwatch.ElapsedMilliseconds,
            FilePath = request.FilePath,
            Arguments = argsText,
            Stdout = stdoutText,
            Stderr = stderrText
        };
    }

    private static ProcessStartInfo BuildStartInfo(string filePath, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static async Task PumpStreamAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            onLine(line);
        }
    }

    private static async Task AwaitAndIgnoreCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
